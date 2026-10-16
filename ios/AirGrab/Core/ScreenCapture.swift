import UIKit

/// 抓取本机当前屏幕内容。
///
/// 移动端的投送源就是屏幕本身 —— 不需要文件选择器、相册权限或屏幕录制授权：
/// 把 key window 的图层层级直接绘制成位图即可。
///
/// 已知限制：`drawHierarchy` 无法捕获由独立渲染进程合成的图层，
/// 例如 AVPlayerLayer 播放中的视频画面。这类区域会呈现为空白，
/// 属于平台渲染模型的边界，非本实现可绕过。
enum ScreenCapture {
    static func snapshot() -> UIImage? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let windows = scenes.flatMap { $0.windows }

        guard let window = windows.first(where: { $0.isKeyWindow }) ?? windows.first else {
            return nil
        }

        let bounds = window.bounds
        guard bounds.width > 1, bounds.height > 1 else { return nil }

        let renderer = UIGraphicsImageRenderer(bounds: bounds)
        return renderer.image { _ in
            window.drawHierarchy(in: bounds, afterScreenUpdates: false)
        }
    }
}
