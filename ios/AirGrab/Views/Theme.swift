import SwiftUI

/// 设计令牌。iOS 端以 Liquid Glass 为基线：内容优先、控件浮于内容之上、
/// 半透明材质随背景折射，因此大面积底色只出现在壁纸上，卡片一律走玻璃。
enum AGTheme {
    static let accent      = Color(red: 0.04, green: 0.52, blue: 1.00)
    static let ink         = Color(red: 0.04, green: 0.04, blue: 0.06)
    static let inkSecondary = Color(white: 0.35, opacity: 0.62)
    static let inkTertiary  = Color(white: 0.35, opacity: 0.36)

    static let ok    = Color(red: 0.10, green: 0.76, blue: 0.49)
    static let warn  = Color(red: 0.94, green: 0.63, blue: 0.13)

    /// 壁纸：暖橙 + 冷蓝 + 紫，为玻璃提供可折射的色彩层次。
    static var wallpaper: some View {
        ZStack {
            Color(red: 0.99, green: 0.97, blue: 0.95)
            RadialGradient(colors: [Color(red: 1.0, green: 0.82, blue: 0.66), .clear],
                           center: .init(x: 0.16, y: 0.06), startRadius: 0, endRadius: 420)
            RadialGradient(colors: [Color(red: 0.65, green: 0.78, blue: 1.0), .clear],
                           center: .init(x: 0.88, y: 0.10), startRadius: 0, endRadius: 400)
            RadialGradient(colors: [Color(red: 0.84, green: 0.71, blue: 1.0), .clear],
                           center: .init(x: 0.5, y: 1.04), startRadius: 0, endRadius: 480)
        }
        .ignoresSafeArea()
    }

    static let cornerCard: CGFloat = 22
    static let cornerTile: CGFloat = 16
}

/// Liquid Glass 材质封装。
///
/// iOS 26 起直接使用系统玻璃效果；更早的 SDK 回退到超薄材质 + 高光描边。
/// 两条分支的观感刻意保持一致（同样的圆角、同样的顶部高光），
/// 因此界面层不需要知道自己跑在哪一版系统上。
struct GlassSurface: ViewModifier {
    var cornerRadius: CGFloat = AGTheme.cornerCard
    var tint: Color?

    @ViewBuilder
    func body(content: Content) -> some View {
        #if compiler(>=6.2)
        if #available(iOS 26.0, *) {
            content.glassEffect(glassStyle, in: .rect(cornerRadius: cornerRadius))
        } else {
            legacy(content)
        }
        #else
        legacy(content)
        #endif
    }

    #if compiler(>=6.2)
    @available(iOS 26.0, *)
    private var glassStyle: Glass {
        if let tint {
            return Glass.regular.tint(tint.opacity(0.28))
        }
        return Glass.regular
    }
    #endif

    private func legacy(_ content: Content) -> some View {
        content
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(
                        LinearGradient(colors: [.white.opacity(0.85), .white.opacity(0.28)],
                                       startPoint: .top, endPoint: .bottom),
                        lineWidth: 1
                    )
            )
    }
}

extension View {
    func glassSurface(cornerRadius: CGFloat = AGTheme.cornerCard, tint: Color? = nil) -> some View {
        modifier(GlassSurface(cornerRadius: cornerRadius, tint: tint))
    }
}

/// 载荷类型 -> 渐变色，四端共用同一套色彩映射，保证跨设备观感一致。
extension PayloadKind {
    var gradient: LinearGradient {
        let colors: [Color]
        switch self {
        case .image: colors = [Color(red: 1.00, green: 0.62, blue: 0.42), Color(red: 1.00, green: 0.37, blue: 0.56), Color(red: 0.64, green: 0.29, blue: 1.00)]
        case .link:  colors = [Color(red: 0.31, green: 0.67, blue: 1.00), Color(red: 0.23, green: 0.43, blue: 1.00), Color(red: 0.37, green: 0.23, blue: 1.00)]
        case .file:  colors = [Color(red: 0.08, green: 0.84, blue: 0.64), Color(red: 0.07, green: 0.70, blue: 0.78), Color(red: 0.23, green: 0.55, blue: 1.00)]
        case .text:  colors = [Color(red: 0.71, green: 0.55, blue: 1.00), Color(red: 0.48, green: 0.36, blue: 1.00), Color(red: 0.29, green: 0.20, blue: 0.68)]
        }
        return LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing)
    }
}
