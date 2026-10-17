using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace AirGrab.Core;

/// <summary>SoftwareBitmap 的底层像素指针访问。CsWinRT 下经 COM 查询取得。</summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>手掌检测框，坐标为归一化前的原图像素。</summary>
public readonly record struct PalmBox(float X1, float Y1, float X2, float Y2, float Score)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
    public float Area => Math.Max(0f, Width) * Math.Max(0f, Height);
    public float CenterX => (X1 + X2) / 2f;
    public float CenterY => (Y1 + Y2) / 2f;

    public static float IoU(PalmBox a, PalmBox b)
    {
        float x1 = MathF.Max(a.X1, b.X1);
        float y1 = MathF.Max(a.Y1, b.Y1);
        float x2 = MathF.Min(a.X2, b.X2);
        float y2 = MathF.Min(a.Y2, b.Y2);

        float inter = MathF.Max(0f, x2 - x1) * MathF.Max(0f, y2 - y1);
        float union = a.Area + b.Area - inter;
        return union <= 0f ? 0f : inter / union;
    }
}

/// <summary>
/// 摄像头通道：MediaCapture 采帧 + ONNX Runtime 两阶段手部推理。
///
/// iOS 端用的是 Apple Vision 的 <c>VNDetectHumanHandPoseRequest</c>，Windows 无等价 API，
/// 但两侧的关键点定义同源 —— 都是 MediaPipe 的 21 点模型，
/// 因此这里用 MediaPipe 官方模型的 ONNX 转换版跑出等价结果，下游状态机完全不区分来源。
///
/// 管线：
/// <code>
/// MediaCapture 采帧 → BGRA8 SoftwareBitmap
///   → palm_detection（192×192，保持宽高比 + 黑边补齐）
///       → 2016 anchors 加性解码 + sigmoid + NMS
///   → 按手掌框扩大裁剪
///   → handpose（224×224）→ 21 关键点
///   → 张开度 = 指尖到掌根平均距离 ÷ 掌长
/// </code>
///
/// **置信度与稳定性**：palm 分数低于 <see cref="PalmScoreThreshold"/> 整帧丢弃；
/// handpose 的存在置信度低于 <see cref="HandScoreThreshold"/> 丢弃；
/// 姿态类别需连续 <see cref="StableFramesRequired"/> 帧一致才切换，
/// 单帧噪声不进入状态机。这些闸门与 iOS / Android 端一一对应。
///
/// 推理按 <see cref="InferIntervalMs"/> 节流（约 10 fps）—— 手势判定依赖 500ms 级
/// 时间窗，10 fps 的采样密度足够，而全速推理会占满 CPU。
/// </summary>
public sealed class HandPoseDetector : IDisposable
{
    // ---- 模型规格 ----
    private const int PalmInput = 192;
    private const int HandInput = 224;
    private const int AnchorCount = 2016;

    // ---- 质量闸门 ----
    private const float PalmScoreThreshold = 0.75f;
    private const float HandScoreThreshold = 0.6f;
    private const float NmsThreshold = 0.3f;
    private const int StableFramesRequired = 3;
    private const int InferIntervalMs = 100;

    // ---- 张开度标定（与 iOS / Android 同值）----
    private const float OpennessFist = 1.05f;
    private const float OpennessSpread = 1.65f;

    // ---- MediaPipe 21 点索引 ----
    private const int Wrist = 0;
    private const int MiddleMcp = 9;
    private static readonly int[] Tips = { 8, 12, 16, 20 };

    // ---- 手掌框放大参数（沿用 MediaPipe 的手部裁剪比例）----
    private const float PalmBoxShiftY = -0.4f;
    private const float PalmBoxEnlarge = 3f;

    private readonly DispatcherQueue _dispatcher;
    private readonly float[] _anchors;
    private InferenceSession? _palm;
    private InferenceSession? _hand;

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private long _lastInferTicks;
    private bool _disposed;

    private PoseCategory _lastCategory = PoseCategory.Neutral;
    private int _stableCount;

    /// <summary>推理出的一帧手部姿态。无有效手部时发出 <see cref="HandPose.None"/>。</summary>
    public event Action<HandPose>? PoseReady;

    /// <summary>供界面显示的预览帧（BGRA8）。</summary>
    public event Action<SoftwareBitmap>? PreviewFrame;

    /// <summary>状态说明，供界面直接展示。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>诊断行：原始张开度比值，便于按实际手型二次标定。</summary>
    public event Action<string>? DiagnosticsChanged;

    public bool IsRunning => _reader is not null;

    private enum PoseCategory { Fist, Open, Neutral }

    public HandPoseDetector(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _anchors = BuildAnchors();
    }

    /// <summary>
    /// 惰性加载两个模型。构造阶段不加载 —— 模型合计约 8 MB，
    /// 且加载失败只应让摄像头通道降级，不应导致应用启动失败。
    ///
    /// 模型以嵌入资源随程序集分发，运行时从资源流读到内存再交给推理会话，
    /// 因此产物不需要额外的模型目录，单文件发布也能直接运行。
    /// </summary>
    private bool EnsureModels()
    {
        if (_palm is not null && _hand is not null) return true;

        try
        {
            _palm ??= new InferenceSession(ReadModelResource("palm_detection_mediapipe_2023feb.onnx"));
            _hand ??= new InferenceSession(ReadModelResource("handpose_estimation_mediapipe_2023feb.onnx"));
            return true;
        }
        catch (Exception ex)
        {
            Report($"手势模型加载失败：{ex.Message}");
            return false;
        }
    }

    private static byte[] ReadModelResource(string fileName)
    {
        var assembly = typeof(HandPoseDetector).Assembly;

        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            throw new FileNotFoundException($"程序集中未找到模型资源 {fileName}");
        }

        using Stream? stream = assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"无法打开模型资源 {resource}");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ---------------------------------------------------------------- 生命周期

    public async Task<bool> StartAsync()
    {
        if (_reader is not null) return true;
        if (!EnsureModels()) return false;

        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                // CPU 内存偏好：SoftwareBitmap 可直接锁定读取，无需 GPU 回读。
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            _capture = new MediaCapture();
            await _capture.InitializeAsync(settings);

            MediaFrameSource? source = _capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);

            if (source is null)
            {
                Report("未找到彩色摄像头源");
                return false;
            }

            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.FrameArrived += OnFrameArrived;
            await _reader.StartAsync();

            Report("手部 21 关键点 · ONNX 端侧推理");
            return true;
        }
        catch (Exception ex)
        {
            // 摄像头被占用、隐私设置禁用、或设备不存在 —— 都不应让界面崩掉。
            Report($"摄像头不可用，已回退模拟输入（{ex.Message}）");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            _reader.Dispose();
            _reader = null;
        }

        _capture?.Dispose();
        _capture = null;

        _lastCategory = PoseCategory.Neutral;
        _stableCount = 0;
    }

    // ---------------------------------------------------------------- 采帧

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long now = Environment.TickCount64;
        if (now - _lastInferTicks < InferIntervalMs) return;
        _lastInferTicks = now;

        using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
        SoftwareBitmap? bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) return;

        SoftwareBitmap bgra = bitmap;
        bool converted = false;

        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                converted = true;
            }

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            byte[] pixels = ReadBgra(bgra, width, height);

            HandPose pose = Detect(pixels, width, height);
            PoseReady?.Invoke(pose);

            // 预览抽帧：每 3 帧送一次即可。1080p 位图每张约 8 MB，
            // 10 fps 全量上屏会给 UI 线程带来不必要的渲染压力。
            if (++_previewTick % 3 == 0)
            {
                QueuePreview(bgra);
            }
        }
        catch (Exception ex)
        {
            Report($"帧处理失败：{ex.Message}");
        }
        finally
        {
            if (converted) bgra.Dispose();
        }
    }

    /// <summary>待界面上屏的预览帧；只保留最新一张。</summary>
    private SoftwareBitmap? _pendingPreview;
    private bool _previewScheduled;
    private int _previewTick;

    /// <summary>
    /// 把当前帧排进预览队列。
    ///
    /// 两个约束决定了这里不能简单地把原始帧丢给界面：
    /// 1. 摄像头帧通常是 1080p，每帧约 8 MB，10 fps 下会持续制造大对象，
    ///    `SoftwareBitmapSource` 在高频大图渲染时容易失败；
    /// 2. 界面消费速度未必跟得上采集，排队会不断堆积。
    /// 因此先把帧缩到 480 宽以内，并且只保留最新一帧 —— 迟到的旧帧直接丢弃。
    /// </summary>
    private void QueuePreview(SoftwareBitmap source)
    {
        SoftwareBitmap? prepared = PreparePreview(source);
        if (prepared is null) return;

        SoftwareBitmap? discarded = Interlocked.Exchange(ref _pendingPreview, prepared);
        discarded?.Dispose();

        if (_previewScheduled) return;
        _previewScheduled = true;

        _dispatcher.TryEnqueue(() =>
        {
            _previewScheduled = false;

            SoftwareBitmap? frame = Interlocked.Exchange(ref _pendingPreview, null);
            if (frame is null) return;

            // 没有订阅者时也要释放，否则每帧泄漏一张位图。
            if (PreviewFrame is null)
            {
                frame.Dispose();
                return;
            }

            try
            {
                PreviewFrame.Invoke(frame);
            }
            catch (Exception ex)
            {
                // 预览是辅助信息，渲染失败不应影响手势识别。
                frame.Dispose();
                Report($"预览不可用：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 统一预览帧的像素格式。
    ///
    /// `SoftwareBitmap.Convert` 没有接收 `BitmapTransform` 的重载 —— 缩放只能发生在
    /// 解码阶段，对已在内存里的位图只能做格式转换。所以这里只做格式归一到 BGRA8，
    /// 尺寸压力交给调用方的抽帧节流处理。
    ///
    /// 源位图在帧引用释放后即失效，因此格式已达标时也要 `Copy` 出独立副本。
    /// </summary>
    private static SoftwareBitmap? PreparePreview(SoftwareBitmap source)
    {
        try
        {
            if (source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
            {
                return SoftwareBitmap.Copy(source);
            }

            return SoftwareBitmap.Convert(
                source,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe byte[] ReadBgra(SoftwareBitmap bitmap, int width, int height)
    {
        byte[] bytes = new byte[width * height * 4];

        using BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using IMemoryBufferReference reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* source, out uint _);

        int stride = buffer.GetPlaneDescription(0).Stride;
        int rowBytes = width * 4;

        fixed (byte* destination = bytes)
        {
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    source + (long)y * stride,
                    destination + (long)y * rowBytes,
                    rowBytes,
                    rowBytes);
            }
        }

        return bytes;
    }

    // ---------------------------------------------------------------- 推理

    private HandPose Detect(byte[] bgra, int width, int height)
    {
        // ── 第一阶段：手掌定位 ──
        float[] palmTensor = LetterboxToPalmTensor(bgra, width, height, out float ratio, out int padLeft, out int padTop);
        IReadOnlyList<PalmBox> boxes = RunPalm(palmTensor, width, height, ratio, padLeft, padTop);

        if (boxes.Count == 0)
        {
            ResetStability();
            return HandPose.None;
        }

        // ── 第二阶段：21 关键点 ──
        PalmBox primary = boxes[0];
        float[] handTensor = CropToHandTensor(bgra, width, height, primary, out float cropLeft, out float cropTop, out float cropSize);

        (float[] landmarks, float score) = RunHand(handTensor);
        if (score < HandScoreThreshold || landmarks.Length < 21 * 2)
        {
            ResetStability();
            return HandPose.None;
        }

        // 关键点从 224 空间映射回原图像素
        float step = cropSize / HandInput;
        Span<float> px = stackalloc float[21];
        Span<float> py = stackalloc float[21];
        for (int i = 0; i < 21; i++)
        {
            px[i] = cropLeft + landmarks[i * 2] * step;
            py[i] = cropTop + landmarks[i * 2 + 1] * step;
        }

        float openness = ComputeOpenness(px, py, width, height, out float rawRatio);
        PoseCategory category = CategoryOf(openness);

        // 时间一致性：类别连续若干帧一致才切换，未达成时沿用上一次已发布的张开度。
        if (category == _lastCategory) _stableCount++;
        else { _lastCategory = category; _stableCount = 1; }

        if (_stableCount < StableFramesRequired)
        {
            ReportDiagnostics($"比值 {rawRatio:0.00} · 稳定中 {_stableCount}/{StableFramesRequired} · 置信 {score:0.00}");
            return HandPose.None;
        }

        ReportDiagnostics($"比值 {rawRatio:0.00} → 张开度 {openness:0.00} · {CategoryLabel(category)} · 置信 {score:0.00} · {boxes.Count} 手");

        float centerX = (px[Wrist] + px[MiddleMcp]) / 2f / width;
        float centerY = (py[Wrist] + py[MiddleMcp]) / 2f / height;

        return new HandPose(
            openness,
            Math.Clamp(centerX, 0, 1),
            Math.Clamp(centerY, 0, 1),
            score,
            boxes.Count);
    }

    /// <summary>
    /// 保持宽高比缩放到 192×192，不足处补黑边。
    /// 与参考实现的差别仅有插值方式（这里用双线性而非 INTER_AREA），对检测结果无实质影响。
    /// </summary>
    private static float[] LetterboxToPalmTensor(byte[] bgra, int width, int height, out float ratio, out int padLeft, out int padTop)
    {
        ratio = MathF.Min((float)PalmInput / width, (float)PalmInput / height);
        int scaledW = Math.Max(1, (int)(width * ratio));
        int scaledH = Math.Max(1, (int)(height * ratio));
        padLeft = (PalmInput - scaledW) / 2;
        padTop = (PalmInput - scaledH) / 2;

        float[] tensor = new float[PalmInput * PalmInput * 3];
        float stepX = (float)width / scaledW;
        float stepY = (float)height / scaledH;

        for (int y = 0; y < scaledH; y++)
        {
            float sy = (y + 0.5f) * stepY;
            int oy = y + padTop;

            for (int x = 0; x < scaledW; x++)
            {
                float sx = (x + 0.5f) * stepX;
                (float b, float g, float r) = SampleBilinear(bgra, width, height, sx, sy);

                int index = (oy * PalmInput + x + padLeft) * 3;
                // 模型按 RGB / [0,1] 训练
                tensor[index] = r;
                tensor[index + 1] = g;
                tensor[index + 2] = b;
            }
        }

        return tensor;
    }

    /// <summary>
    /// 按手掌框裁剪出正方形区域并缩放到 224×224。
    /// 中心按 MediaPipe 的惯例上移 0.4 倍框高并放大 3 倍，以完整覆盖五指。
    /// </summary>
    private static float[] CropToHandTensor(byte[] bgra, int width, int height, PalmBox box,
        out float cropLeft, out float cropTop, out float cropSize)
    {
        float centerX = box.CenterX;
        float centerY = box.CenterY + PalmBoxShiftY * box.Height;

        cropSize = MathF.Max(box.Width, box.Height) * PalmBoxEnlarge;
        cropLeft = centerX - cropSize / 2f;
        cropTop = centerY - cropSize / 2f;

        float step = cropSize / HandInput;
        float[] tensor = new float[HandInput * HandInput * 3];

        for (int y = 0; y < HandInput; y++)
        {
            float sy = cropTop + (y + 0.5f) * step;
            for (int x = 0; x < HandInput; x++)
            {
                float sx = cropLeft + (x + 0.5f) * step;

                // 越界区域保持 0（黑），与训练时的 padding 行为一致。
                if (sx < 0 || sy < 0 || sx >= width - 1 || sy >= height - 1) continue;

                (float b, float g, float r) = SampleBilinear(bgra, width, height, sx, sy);
                int index = (y * HandInput + x) * 3;
                tensor[index] = r;
                tensor[index + 1] = g;
                tensor[index + 2] = b;
            }
        }

        return tensor;
    }

    private static (float b, float g, float r) SampleBilinear(byte[] bgra, int width, int height, float x, float y)
    {
        int x0 = Math.Clamp((int)MathF.Floor(x), 0, width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, height - 1);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float fx = Math.Clamp(x - x0, 0f, 1f);
        float fy = Math.Clamp(y - y0, 0f, 1f);

        int i00 = (y0 * width + x0) * 4;
        int i10 = (y0 * width + x1) * 4;
        int i01 = (y1 * width + x0) * 4;
        int i11 = (y1 * width + x1) * 4;

        float w00 = (1 - fx) * (1 - fy);
        float w10 = fx * (1 - fy);
        float w01 = (1 - fx) * fy;
        float w11 = fx * fy;

        float b = bgra[i00] * w00 + bgra[i10] * w10 + bgra[i01] * w01 + bgra[i11] * w11;
        float g = bgra[i00 + 1] * w00 + bgra[i10 + 1] * w10 + bgra[i01 + 1] * w01 + bgra[i11 + 1] * w11;
        float r = bgra[i00 + 2] * w00 + bgra[i10 + 2] * w10 + bgra[i01 + 2] * w01 + bgra[i11 + 2] * w11;

        return (b / 255f, g / 255f, r / 255f);
    }

    /// <summary>
    /// palm_detection 后处理：sigmoid → 加性解码 → 贪心 NMS。
    ///
    /// 解码是**加性**的（不是 SSD 的乘性 exp 形式）：anchors 只提供中心点，
    /// 预测值除以输入边长即为中心偏移与宽高。这一点与参考实现严格一致。
    /// </summary>
    private IReadOnlyList<PalmBox> RunPalm(float[] tensor, int imageWidth, int imageHeight,
        float ratio, int padLeft, int padTop)
    {
        var input = new DenseTensor<float>(tensor, new[] { 1, PalmInput, PalmInput, 3 });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_palm!.InputMetadata.Keys.First(), input)
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _palm.Run(inputs);

        Tensor<float> boxes = results[0].AsTensor<float>();   // [1, 2016, 18]
        Tensor<float> scores = results[1].AsTensor<float>();  // [1, 2016, 1]

        float scale = MathF.Max(imageWidth, imageHeight);
        var candidates = new List<PalmBox>();

        for (int i = 0; i < AnchorCount; i++)
        {
            float score = Sigmoid(scores[0, i, 0]);
            if (score < PalmScoreThreshold) continue;

            float anchorX = _anchors[i * 2];
            float anchorY = _anchors[i * 2 + 1];

            float cx = boxes[0, i, 0] / PalmInput + anchorX;
            float cy = boxes[0, i, 1] / PalmInput + anchorY;
            float w = boxes[0, i, 2] / PalmInput;
            float h = boxes[0, i, 3] / PalmInput;

            candidates.Add(new PalmBox(
                (cx - w / 2f) * scale - padLeft,
                (cy - h / 2f) * scale - padTop,
                (cx + w / 2f) * scale - padLeft,
                (cy + h / 2f) * scale - padTop,
                score));
        }

        if (candidates.Count == 0) return Array.Empty<PalmBox>();

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var kept = new List<PalmBox>();
        foreach (PalmBox box in candidates)
        {
            bool suppressed = kept.Any(k => PalmBox.IoU(k, box) > NmsThreshold);
            if (!suppressed) kept.Add(box);

            // 状态机最多需要区分「一只手 / 多只手」，保留前两个即可。
            if (kept.Count >= 2) break;
        }

        return kept;
    }

    private (float[] landmarks, float score) RunHand(float[] tensor)
    {
        var input = new DenseTensor<float>(tensor, new[] { 1, HandInput, HandInput, 3 });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_hand!.InputMetadata.Keys.First(), input)
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _hand.Run(inputs);

        // 输出顺序：21 点(63) / 存在置信度(1) / 左右手(1) / 世界坐标(63)
        Tensor<float> landmarks = results[0].AsTensor<float>();
        Tensor<float> confidence = results[1].AsTensor<float>();

        float[] flat = new float[63];
        for (int i = 0; i < 63; i++) flat[i] = landmarks[0, i];

        return (flat, confidence[0, 0]);
    }

    /// <summary>
    /// 张开度：四指指尖到**掌根**的平均距离 ÷ 掌长。
    /// 参考点必须是掌根而非掌心中点 —— 掌心做参考会压缩张开与握拳的差异，
    /// 使五指张开算出的值够不到张开阈值（该错误曾在三端同时存在）。
    /// </summary>
    private static float ComputeOpenness(ReadOnlySpan<float> px, ReadOnlySpan<float> py,
        int width, int height, out float rawRatio)
    {
        float palmLength = MathF.Sqrt(
            MathF.Pow(px[MiddleMcp] - px[Wrist], 2) + MathF.Pow(py[MiddleMcp] - py[Wrist], 2));

        if (palmLength < 1e-3f)
        {
            rawRatio = 0f;
            return 0f;
        }

        float sum = 0f;
        foreach (int tip in Tips)
        {
            sum += MathF.Sqrt(
                MathF.Pow(px[tip] - px[Wrist], 2) + MathF.Pow(py[tip] - py[Wrist], 2)) / palmLength;
        }

        rawRatio = sum / Tips.Length;
        return Math.Clamp((rawRatio - OpennessFist) / (OpennessSpread - OpennessFist), 0f, 1f);
    }

    // ---------------------------------------------------------------- anchors

    /// <summary>
    /// 生成 palm_detection 的 2016 个 anchor 中心。
    ///
    /// 构成 = 24×24 网格每点重复 2 次 + 12×12 网格每点重复 6 次，行优先，中心取 (i+0.5)/n。
    /// 该规则已与 OpenCV Zoo 参考实现的硬编码表逐元素核对（最大偏差 3.3e-08，纯浮点精度）。
    /// 由于解码只使用 anchor 的中心坐标，同点重复的 anchor 等价，不影响结果。
    /// </summary>
    private static float[] BuildAnchors()
    {
        var anchors = new List<float>(AnchorCount * 2);
        AppendGrid(anchors, 24, 2);
        AppendGrid(anchors, 12, 6);
        return anchors.ToArray();
    }

    private static void AppendGrid(List<float> target, int size, int repeat)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float cx = (x + 0.5f) / size;
                float cy = (y + 0.5f) / size;

                for (int r = 0; r < repeat; r++)
                {
                    target.Add(cx);
                    target.Add(cy);
                }
            }
        }
    }

    // ---------------------------------------------------------------- 辅助

    private static float Sigmoid(float value)
        => 1f / (1f + MathF.Exp(-value));

    private static PoseCategory CategoryOf(float openness)
    {
        if (openness <= 0.35f) return PoseCategory.Fist;
        if (openness >= 0.62f) return PoseCategory.Open;
        return PoseCategory.Neutral;
    }

    private static string CategoryLabel(PoseCategory category) => category switch
    {
        PoseCategory.Fist => "握拳",
        PoseCategory.Open => "张开",
        _ => "过渡"
    };

    private void ResetStability()
    {
        _lastCategory = PoseCategory.Neutral;
        _stableCount = 0;
    }

    private void Report(string message) => _dispatcher.TryEnqueue(() => StatusChanged?.Invoke(message));

    private void ReportDiagnostics(string message)
        => _dispatcher.TryEnqueue(() => DiagnosticsChanged?.Invoke(message));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _palm?.Dispose();
        _hand?.Dispose();
    }
}
