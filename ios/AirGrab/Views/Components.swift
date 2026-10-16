import SwiftUI

/// 玻璃卡片：载荷在四端统一的呈现形态。
/// 缩略区走类型渐变，文字区走玻璃，保证在任何壁纸上都保持可读的层次。
struct PayloadTile: View {
    let payload: Payload
    var compact: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                payload.kind.gradient
                Image(systemName: payload.kind.symbol)
                    .font(.system(size: compact ? 22 : 28, weight: .medium))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.28), radius: 6, y: 2)
            }
            .frame(height: compact ? 74 : 96)
            .overlay(
                LinearGradient(colors: [.white.opacity(0.34), .clear],
                               startPoint: .topLeading, endPoint: .center)
            )

            VStack(alignment: .leading, spacing: 3) {
                Text(payload.name)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(AGTheme.ink)
                    .lineLimit(1)
                Text(payload.sizeText)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(AGTheme.inkTertiary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(.regularMaterial)
        }
        .clipShape(RoundedRectangle(cornerRadius: AGTheme.cornerTile, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: AGTheme.cornerTile, style: .continuous)
                .strokeBorder(.white.opacity(0.55), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.16), radius: 12, y: 6)
    }
}

/// 列表行：用于 iPad 分栏与较窄的纵向空间。
struct PayloadRow: View {
    let payload: Payload

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                payload.kind.gradient
                Image(systemName: payload.kind.symbol)
                    .font(.system(size: 18, weight: .medium))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.25), radius: 4, y: 1)
            }
            .frame(width: 46, height: 46)
            .clipShape(RoundedRectangle(cornerRadius: 13, style: .continuous))

            VStack(alignment: .leading, spacing: 3) {
                Text(payload.name)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(AGTheme.ink)
                    .lineLimit(1)
                Text(payload.sizeText)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(AGTheme.inkSecondary)
            }

            Spacer(minLength: 6)

            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(AGTheme.inkTertiary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .glassSurface(cornerRadius: 17)
    }
}

struct DeviceRow: View {
    let peer: Peer
    var action: (() -> Void)?

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle()
                    .fill(AGTheme.accent.opacity(0.14))
                Image(systemName: peer.platformSymbol)
                    .font(.system(size: 17, weight: .medium))
                    .foregroundStyle(AGTheme.accent)
            }
            .frame(width: 42, height: 42)

            VStack(alignment: .leading, spacing: 3) {
                Text(peer.name)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(AGTheme.ink)
                    .lineLimit(1)
                Text("\(peer.platform) · \(peer.address)")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(AGTheme.inkSecondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 6)

            if let action {
                Button(action: action) {
                    Text("投送")
                        .font(.system(size: 12.5, weight: .semibold))
                        .padding(.horizontal, 14)
                        .padding(.vertical, 7)
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .background(AGTheme.accent, in: Capsule())
            } else {
                LiveBadge()
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .glassSurface(cornerRadius: 17)
    }
}

struct LiveBadge: View {
    var text: String = "可接收"

    var body: some View {
        HStack(spacing: 5) {
            Circle()
                .fill(AGTheme.ok)
                .frame(width: 6, height: 6)
            Text(text)
                .font(.system(size: 10.5, weight: .semibold))
                .foregroundStyle(Color(red: 0.05, green: 0.55, blue: 0.35))
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 4)
        .background(AGTheme.ok.opacity(0.16), in: Capsule())
    }
}

/// 手势状态面板：五态进度 + 手型 + 当前搬运载荷。
struct GesturePanel: View {
    let state: GestureState
    let carried: String
    let hint: String

    private let states: [GestureState] = [.hover, .grabbing, .carrying, .drop]

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 15, style: .continuous)
                    .fill(handBackground)
                Image(systemName: state == .carrying || state == .grabbing ? "hand.raised.fill" : "hand.raised")
                    .font(.system(size: 22, weight: .medium))
                    .foregroundStyle(handForeground)
                    .scaleEffect(state == .grabbing ? 0.9 : 1)
                    .animation(.spring(response: 0.32, dampingFraction: 0.62), value: state)
            }
            .frame(width: 48, height: 48)

            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(state.title)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(AGTheme.ink)
                    Text(state.rawValue)
                        .font(.system(size: 10.5, design: .monospaced))
                        .foregroundStyle(AGTheme.inkTertiary)
                    Spacer(minLength: 0)
                    Text(carried)
                        .font(.system(size: 10.5, design: .monospaced))
                        .foregroundStyle(AGTheme.inkSecondary)
                        .lineLimit(1)
                }

                HStack(spacing: 5) {
                    ForEach(states, id: \.self) { item in
                        Capsule()
                            .fill(barColor(for: item))
                            .frame(height: 4)
                    }
                }

                Text(hint)
                    .font(.system(size: 11))
                    .foregroundStyle(AGTheme.inkSecondary)
            }
        }
        .padding(14)
        .glassSurface(cornerRadius: 20)
    }

    private var handBackground: some ShapeStyle {
        switch state {
        case .grabbing: return AnyShapeStyle(LinearGradient(colors: [AGTheme.accent, Color(red: 0.48, green: 0.29, blue: 1.0)], startPoint: .topLeading, endPoint: .bottomTrailing))
        case .carrying: return AnyShapeStyle(LinearGradient(colors: [Color(red: 0.48, green: 0.29, blue: 1.0), Color(red: 0.71, green: 0.29, blue: 1.0)], startPoint: .topLeading, endPoint: .bottomTrailing))
        case .drop:     return AnyShapeStyle(LinearGradient(colors: [AGTheme.ok, Color(red: 0.06, green: 0.63, blue: 0.42)], startPoint: .topLeading, endPoint: .bottomTrailing))
        default:        return AnyShapeStyle(Color.black.opacity(0.06))
        }
    }

    private var handForeground: Color {
        switch state {
        case .grabbing, .carrying, .drop: return .white
        default: return AGTheme.accent
        }
    }

    private func barColor(for item: GestureState) -> AnyShapeStyle {
        if state.step > item.step {
            return AnyShapeStyle(AGTheme.ok)
        }
        if state.step == item.step {
            return AnyShapeStyle(LinearGradient(colors: [AGTheme.accent, Color(red: 0.48, green: 0.29, blue: 1.0)],
                                                startPoint: .leading, endPoint: .trailing))
        }
        return AnyShapeStyle(Color.black.opacity(0.10))
    }
}

/// 浮动胶囊 Tab Bar（Liquid Glass 基线）。
struct GlassTabBar<T: Hashable & Identifiable>: View {
    @Binding var selection: T
    let items: [T]
    let title: (T) -> String
    let symbol: (T) -> String

    var body: some View {
        HStack(spacing: 4) {
            ForEach(items) { item in
                Button {
                    withAnimation(.spring(response: 0.34, dampingFraction: 0.74)) {
                        selection = item
                    }
                } label: {
                    VStack(spacing: 3) {
                        Image(systemName: symbol(item))
                            .font(.system(size: 17, weight: .medium))
                        Text(title(item))
                            .font(.system(size: 9.5, weight: .semibold))
                    }
                    .frame(width: 64)
                    .padding(.vertical, 7)
                    .foregroundStyle(selection == item ? AGTheme.accent : AGTheme.inkSecondary)
                    .background(
                        Group {
                            if selection == item {
                                Capsule().fill(.white.opacity(0.86))
                                    .shadow(color: .black.opacity(0.10), radius: 6, y: 2)
                            }
                        }
                    )
                }
                .buttonStyle(.plain)
            }
        }
        .padding(7)
        .glassSurface(cornerRadius: 34)
    }
}
