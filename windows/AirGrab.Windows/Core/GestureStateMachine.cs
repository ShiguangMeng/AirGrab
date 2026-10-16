namespace AirGrab.Core;

public enum GestureState { Idle, Hover, Grabbing, Carrying, Drop }

public static class GestureStateExtensions
{
    public static string Title(this GestureState state) => state switch
    {
        GestureState.Idle => "待机",
        GestureState.Hover => "悬停",
        GestureState.Grabbing => "抓取",
        GestureState.Carrying => "搬运",
        GestureState.Drop => "投送",
        _ => "待机"
    };

    public static string Code(this GestureState state) => state switch
    {
        GestureState.Idle => "IDLE",
        GestureState.Hover => "HOVER",
        GestureState.Grabbing => "GRABBING",
        GestureState.Carrying => "CARRYING",
        GestureState.Drop => "DROP",
        _ => "IDLE"
    };

    public static string Hint(this GestureState state) => state switch
    {
        GestureState.Idle => "等待手势",
        GestureState.Hover => "手掌已识别",
        GestureState.Grabbing => "握拳确认 0.5s",
        GestureState.Carrying => "移向目标设备",
        GestureState.Drop => "张开手掌释放",
        _ => "等待手势"
    };

    /// <summary>进度指示用序号，Idle 为 0。</summary>
    public static int Step(this GestureState state) => state switch
    {
        GestureState.Idle => 0,
        GestureState.Hover => 1,
        GestureState.Grabbing => 2,
        GestureState.Carrying => 3,
        GestureState.Drop => 4,
        _ => 0
    };
}

/// <summary>
/// 单帧手部姿态。
/// 摄像头通道写入 MediaPipe 关键点的推断值；模拟通道由指针事件合成同一结构，
/// 因此状态机对输入来源完全无感知。
/// </summary>
public readonly record struct HandPose(double Openness, double X, double Y, float Confidence, int HandCount)
{
    public static HandPose None => new(0, 0.5, 0.5, 0, 0);

    public bool IsFist => Openness <= 0.35;
    public bool IsOpenPalm => Openness >= 0.62;
}

public enum MultiHandPolicy { Reject, UseNearest }

/// <summary>
/// 手势状态机：把连续的手部姿态流收敛成离散的抓取 / 投送事件。
///
/// 四道防误触闸门，缺一不可：
/// 1. 握拳必须**连续**持续 <see cref="HoldDuration"/> 才被确认为有意识的抓取；
/// 2. 画面中同时出现两只手时整帧作废，宁可要求重试也不猜测意图；
/// 3. 张开手掌必须连续保持 <see cref="OpenPalmHoldDuration"/> 才被记为「见过张开」，
///    单帧噪声不会打开抓取的前置条件；
/// 4. 「见过张开」只在 <see cref="OpenPalmValidity"/> 时间窗内有效 —— 张开的动作必须
///    紧邻随后的握拳，隔了很久的旧状态不能为凭空握拳背书。
///
/// 第 3、4 条是误触发的直接原因：检测器偶发噪声若把某一帧判为张开，
/// 没有时效约束时它会永久为后续任何握拳放行，表现为「手没动也抓取了内容」。
/// </summary>
public sealed class GestureStateMachine
{
    public TimeSpan HoldDuration { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>张开手掌需连续保持的时长，用于确认「见过张开」这一前置条件。</summary>
    public TimeSpan OpenPalmHoldDuration { get; set; } = TimeSpan.FromMilliseconds(180);
    /// <summary>「见过张开」的有效期；超出则前置条件失效。</summary>
    public TimeSpan OpenPalmValidity { get; set; } = TimeSpan.FromSeconds(2.5);
    /// <summary>搬运阶段张开手掌需连续保持的时长，用于确认投送意图。</summary>
    public TimeSpan DropHoldDuration { get; set; } = TimeSpan.FromMilliseconds(120);
    public MultiHandPolicy MultiHandPolicy { get; set; } = MultiHandPolicy.Reject;

    public GestureState State { get; private set; } = GestureState.Idle;
    public string PayloadName { get; set; } = "—";

    /// <summary>抓取被确认时触发，参数为掌心位置。</summary>
    public event Action<HandPose>? GrabConfirmed;
    /// <summary>投送被确认时触发。</summary>
    public event Action<HandPose>? DropConfirmed;
    /// <summary>抓取或投送被取消时触发。</summary>
    public event Action? Cancelled;
    /// <summary>状态发生变化时触发。</summary>
    public event Action<GestureState>? StateChanged;

    private DateTimeOffset? _fistSince;
    /// <summary>当前这段连续张开的起点。</summary>
    private DateTimeOffset? _openSince;
    /// <summary>最近一次被确认的张开时刻，受 OpenPalmValidity 约束。</summary>
    private DateTimeOffset? _lastOpenAt;
    /// <summary>搬运阶段连续张开的起点。</summary>
    private DateTimeOffset? _dropSince;
    private bool _hasGrabbed;
    private bool _isCarrying;
    private HandPose _lastPose = HandPose.None;

    public void Ingest(HandPose pose, DateTimeOffset now)
    {
        if (pose.Confidence <= 0.25f)
        {
            ResetToIdleIfIdle();
            return;
        }
        if (pose.HandCount > 1 && MultiHandPolicy == MultiHandPolicy.Reject)
        {
            Cancel();
            return;
        }

        _lastPose = pose;

        if (_isCarrying)
        {
            // 搬运阶段：保持拳，掌心位置持续输出；张开并保持即投送。
            if (pose.IsOpenPalm)
            {
                if (_dropSince is { } since)
                {
                    if (now - since >= DropHoldDuration)
                    {
                        _isCarrying = false;
                        _dropSince = null;
                        SetState(GestureState.Drop);
                        DropConfirmed?.Invoke(pose);
                    }
                }
                else
                {
                    _dropSince = now;
                }
            }
            else
            {
                _dropSince = null;
            }
            return;
        }

        if (_hasGrabbed)
        {
            if (pose.IsFist)
            {
                _isCarrying = true;
                SetState(GestureState.Carrying);
            }
            else if (pose.IsOpenPalm)
            {
                Cancel();
            }
            return;
        }

        if (pose.IsFist)
        {
            if (_fistSince is { } since)
            {
                if (now - since >= HoldDuration &&
                    _lastOpenAt is { } opened &&
                    now - opened <= OpenPalmValidity)
                {
                    // 抓取成立的前提是「近期」出现过张开手掌，防止凭空握拳触发。
                    _hasGrabbed = true;
                    SetState(GestureState.Grabbing);
                    GrabConfirmed?.Invoke(pose);
                }
            }
            else
            {
                _fistSince = now;
                SetState(GestureState.Grabbing);
            }
            return;
        }

        _fistSince = null;

        if (pose.IsOpenPalm)
        {
            // 张开需连续保持 OpenPalmHoldDuration 才确认，单帧噪声不打开抓取前置条件。
            DateTimeOffset start = _openSince ?? now;
            _openSince = start;
            if (now - start >= OpenPalmHoldDuration)
            {
                _lastOpenAt = now;
                if (State is GestureState.Idle or GestureState.Hover)
                {
                    SetState(GestureState.Hover);
                }
            }
        }
        else
        {
            _openSince = null;
            if (State == GestureState.Grabbing)
            {
                SetState(GestureState.Hover);
            }
        }
    }

    /// <summary>模拟通道在按下即抓取的场景下直接进入搬运。</summary>
    public void BeginCarrying(string name)
    {
        _hasGrabbed = true;
        _isCarrying = true;
        PayloadName = name;
        SetState(GestureState.Carrying);
    }

    public void ConfirmDrop()
    {
        if (!_isCarrying) return;
        _isCarrying = false;
        _dropSince = null;
        SetState(GestureState.Drop);
        DropConfirmed?.Invoke(_lastPose);
    }

    public void Cancel()
    {
        bool wasActive = _isCarrying || _hasGrabbed;
        _hasGrabbed = false;
        _isCarrying = false;
        _fistSince = null;
        _openSince = null;
        _dropSince = null;
        SetState(GestureState.Idle);
        if (wasActive) Cancelled?.Invoke();
    }

    /// <summary>一次完整投送结束后复位，等待下一轮手势。</summary>
    public void Finish()
    {
        _hasGrabbed = false;
        _isCarrying = false;
        _fistSince = null;
        _openSince = null;
        _lastOpenAt = null;
        _dropSince = null;
        PayloadName = "—";
        SetState(GestureState.Idle);
    }

    private void ResetToIdleIfIdle()
    {
        if (_isCarrying || _hasGrabbed) return;
        _fistSince = null;
        _openSince = null;
        _dropSince = null;
        SetState(GestureState.Idle);
    }

    private void SetState(GestureState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke(next);
    }
}
