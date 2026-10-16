# AirGrab · 隔空手势抓取投送

把「选中 → 分享 → 选设备 → 确认」这四步压缩成一条连续的物理手势流：
**张开手掌 → 握拳抓取 → 移向目标设备 → 张开手掌投送**。

跨四端实现，Windows / Android / iOS / iPadOS 共用同一套手势状态机与传输协议，
不依赖任何厂商账号体系。

---

## 四端

| 端 | 目录 | UI 技术栈 | 设计语言 | 手势识别 |
|---|---|---|---|---|
| Windows 11 | `windows/AirGrab.Windows` | WinUI 3 (Windows App SDK 1.8) + C# / .NET 8 | Fluent 2 + Mica，标题栏自绘 | MediaPipe Hand Landmarker |
| Android | `android/` | Kotlin + Jetpack Compose | Material 3 Expressive | MediaPipe Tasks Vision |
| iOS | `ios/` | Swift + SwiftUI | Liquid Glass (iOS 26) | Vision `VNDetectHumanHandPoseRequest` |
| iPadOS | `ios/`（同一工程，`TARGETED_DEVICE_FAMILY = 1,2`） | 同上 | 同上 | 同上 |

## 交互模型

```
IDLE ──手掌靠近──▶ HOVER ──张开后快速握拳并停留 0.5s──▶ GRABBING
                                                          │
                                         拳保持、随手掌移动 ▼
                                                      CARRYING
                                                          │
                                          目标侧张开手掌 ▼
                                                        DROP ──▶ 投递
```

三道防误触闸门：

1. **序列校验** —— 未构成完整的「张开 → 握拳 → 移动 → 张开」序列一律不响应，挥手、挠头不触发。
2. **握拳停留窗口** —— 握拳需持续约 0.5 秒才被确认为有意识的抓取。
3. **多手拒绝** —— 画面中同时出现两只手时整帧作废，宁可要求重试也不猜测意图。

目标设备判定综合三个信号加权投票：手部移动方向与设备方位的匹配度、设备间距离、历史投送频次。
候选不唯一时把决定权交还用户，在目标端弹出接收确认。

## 传输协议

明文格式：

```
AGG|<type>|<size>|<name>|<payload>
```

`type` ∈ `text` / `link` / `image` / `file`。净荷允许包含分隔符，
因此解码按**前四个**分隔符切分，第四段之后全部归净荷。

| 内容 | 通道 | 落地方式 |
|---|---|---|
| 文本 | UDP 直投 | 进目标端剪贴板 |
| 链接 | UDP 直投 | 交给默认浏览器 |
| 图片 | TCP 分片 | 存入目标端相册 |
| 任意文件 | TCP 分片 | 落接收目录 |

TCP 分片结构（小端）：

```
[4B 净荷总长][4B 片偏移][4B 片长][4B 片 CRC32][片数据]
```

片长 64 KB，逐片 CRC32 校验，校验失败的片整条连接作废 —— 不把损坏数据写进结果集。
偏移量使接收端可按序重组，为断点续传预留。

**信任模型**：局域网配对码完成首次配对，之后持久化密钥。
设备发现走 UDP 广播信标，每 2 秒一次，超过 8 秒未续期即判定离线。

## 构建

### Windows

需要 .NET SDK 8+ 与 Windows 10 1809 以上。

```bash
dotnet build windows/AirGrab.Windows/AirGrab.Windows.csproj -c Release -r win-x64
```

工程为**非打包（unpackaged）**模式，`WindowsAppSDKSelfContained` 已开启，
产物目录自带 Windows App SDK 运行时，无需单独安装。

### iOS / iPadOS

工程由 [XcodeGen](https://github.com/yonaskolb/XcodeGen) 从 `ios/project.yml` 生成，
仓库内不保存 `.xcodeproj`，避免二进制文件冲突。

最低支持 **iOS / iPadOS 16.0**。代码未使用任何 iOS 17+ 的 API，
版本门槛由 `AnyShapeStyle` 与 `.scrollIndicators(_:axes:)` 决定（两者均为 iOS 16 引入）。
iPad 与 iPhone 共用同一工程（`TARGETED_DEVICE_FAMILY = 1,2`）。

```bash
brew install xcodegen
cd ios && xcodegen generate
open AirGrab.xcodeproj
```

**没有 macOS 时**：向 `main` 分支推送 `ios/` 下的改动会触发
[`.github/workflows/ios.yml`](.github/workflows/ios.yml)，
在 GitHub 的 macOS runner 上自动编译，并把以下产物作为 artifact 上传：

- `AirGrab-simulator.zip` —— 可直接拖入 iOS 模拟器运行
- `AirGrab-unsigned.ipa` —— 未签名，需用 Sideloadly / AltStore 配合你自己的 Apple ID 签名后安装

### Android

需要 JDK 17+ 与 Android SDK（platform 35）。

```bash
# 手势识别依赖 MediaPipe 官方模型，需先放到 assets（CI 会自动下载）
mkdir -p android/app/src/main/assets
curl -fsSL -o android/app/src/main/assets/hand_landmarker.task \
  https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task

cd android && gradle wrapper --gradle-version 8.11.1 && ./gradlew assembleDebug
```

模型缺失时应用不会崩溃 —— `HandPoseDetector.start()` 会捕获失败并把状态置为
「模型不可用，已回退模拟输入」，此时触摸通道仍然可用。

也可以用 Android Studio 直接打开 `android/` 目录。

## 设计说明

- **iOS / iPadOS** 以 Liquid Glass 为基线：底色只存在于壁纸，卡片一律走玻璃材质，
  控件浮于内容之上。`GlassSurface` 在 iOS 26 用系统玻璃效果，
  更早的 SDK 回退到超薄材质 + 高光描边，两条分支观感刻意保持一致。
- **Windows** 标题栏整体交给 XAML 自绘（`ExtendsContentIntoTitleBar`），
  系统按钮保留原生悬停与按下反馈，仅把底色改为透明以融入 Mica。
- **Android** 采用 Material 3 Expressive 的弹簧物理动效与动态取色。
- DPI 感知为 Per-Monitor V2，多屏不同缩放比下手势浮层保持像素对齐。

## 与「隔空传送」的差异

公开资料描述的信任模型建立在厂商账号体系之上，跨厂商设备无法复用。
AirGrab 改为局域网配对码 + 首次信任后持久化密钥，因此四端之间可平权互通。

方位判定方面，原实现借助 UWB 测距；AirGrab 不假设硬件具备 UWB，
改为方向向量 + 信号强度 + 历史权重的加权投票，并保留人工二次确认兜底。

## 许可

MIT
