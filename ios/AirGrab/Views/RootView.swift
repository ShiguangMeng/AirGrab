import SwiftUI
import UIKit

struct RootView: View {
    @EnvironmentObject private var model: AppModel

    @State private var tab: Tab = .send
    @State private var dragging: Payload?
    @State private var dragTranslation: CGSize = .zero
    @State private var dropTarget: Peer?

    enum Tab: String, CaseIterable, Identifiable {
        case send, devices, history, settings
        var id: String { rawValue }
        var title: String {
            switch self {
            case .send:     return "投送"
            case .devices:  return "设备"
            case .history:  return "记录"
            case .settings: return "设置"
            }
        }
        var symbol: String {
            switch self {
            case .send:     return "hand.raised"
            case .devices:  return "dot.radiowaves.left.and.right"
            case .history:  return "clock"
            case .settings: return "gearshape"
            }
        }
    }

    var body: some View {
        ZStack {
            AGTheme.wallpaper

            VStack(spacing: 0) {
                content
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)

                GlassTabBar(selection: $tab,
                            items: Tab.allCases,
                            title: { $0.title },
                            symbol: { $0.symbol })
                    .padding(.bottom, 10)
            }
        }
        .overlay(alignment: .top) { dragOverlay }
        .overlay(alignment: .bottom) { toastOverlay }
        .task { model.start() }
    }

    // MARK: - 页面

    @ViewBuilder
    private var content: some View {
        switch tab {
        case .send:     sendPage
        case .devices:  devicesPage
        case .history:  historyPage
        case .settings: settingsPage
        }
    }

    private var sendPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header

                GesturePanel(state: model.state,
                             carried: model.carriedName,
                             hint: model.state.hint)

                HStack(spacing: 8) {
                    Text("可抓取内容")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(AGTheme.ink)

                    Spacer()

                    // 移动端的抓取源就是本机屏幕，不需要文件选择器或相册授权。
                    Button {
                        model.captureScreen()
                    } label: {
                        Label("抓取本机屏幕", systemImage: "camera.viewfinder")
                            .font(.system(size: 11.5, weight: .medium))
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)

                    Text("\(model.outbox.count) 项")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(AGTheme.inkTertiary)
                }

                LazyVGrid(columns: [GridItem(.flexible(), spacing: 12),
                                    GridItem(.flexible(), spacing: 12)],
                          spacing: 12) {
                    ForEach(model.outbox) { payload in
                        PayloadTile(payload: payload)
                            .opacity(dragging?.id == payload.id ? 0.24 : 1)
                            .scaleEffect(dragging?.id == payload.id ? 0.96 : 1)
                            .gesture(dragGesture(for: payload))
                    }
                }

                Text("按住卡片并拖动 = 握拳抓取；松手 = 张开手掌投送")
                    .font(.system(size: 11))
                    .foregroundStyle(AGTheme.inkTertiary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.top, 4)
            }
            .padding(.horizontal, 18)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .scrollIndicators(.hidden)
    }

    private var devicesPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header

                if model.peers.isEmpty {
                    emptyCard(title: "未发现附近设备",
                              detail: "请确认对方设备已打开 AirGrab，并与本机处于同一局域网。")
                } else {
                    ForEach(model.peers) { peer in
                        DeviceRow(peer: peer) {
                            guard let first = model.outbox.first else { return }
                            model.send(first, to: peer)
                        }
                    }
                }

                GlassNote(title: "发现机制",
                          detail: "UDP 广播信标每 2 秒一次，超过 8 秒未续期即判定离线。不依赖账号体系。")
            }
            .padding(.horizontal, 18)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .scrollIndicators(.hidden)
    }

    private var historyPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header

                sectionCard(title: "已接收", items: model.inbox, empty: "暂无接收记录")
                sectionCard(title: "已投送", items: model.outbox, empty: "暂无投送记录")

                GlassNote(title: "传输层",
                          detail: "TCP 分片，片长 64KB，逐片 CRC32 校验；校验失败的片整条丢弃。")
            }
            .padding(.horizontal, 18)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .scrollIndicators(.hidden)
    }

    private var settingsPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header

                VStack(alignment: .leading, spacing: 12) {
                    Text("手势通道")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(AGTheme.ink)

                    HStack(spacing: 8) {
                        ForEach(AppModel.Channel.allCases) { item in
                            Button {
                                model.setChannel(item)
                            } label: {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(item.title)
                                        .font(.system(size: 13, weight: .semibold))
                                    Text(item == .simulated ? "指针事件驱动状态机" : "Vision 手部 21 关键点")
                                        .font(.system(size: 10.5))
                                        .foregroundStyle(model.channel == item ? .white.opacity(0.85) : AGTheme.inkSecondary)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal, 12)
                                .padding(.vertical, 10)
                                .foregroundStyle(model.channel == item ? .white : AGTheme.ink)
                                .background(
                                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                                        .fill(model.channel == item ? AnyShapeStyle(AGTheme.accent) : AnyShapeStyle(.white.opacity(0.5)))
                                )
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                .padding(14)
                .glassSurface(cornerRadius: 20)

                VStack(alignment: .leading, spacing: 10) {
                    infoLine("接收服务", model.transfer.isServing ? "已监听 :25586" : "未启动")
                    Divider().opacity(0.4)
                    infoLine("发现端口", "UDP :25585")
                    Divider().opacity(0.4)
                    infoLine("协议", "AGG|<type>|<size>|<name>|<payload>")
                    Divider().opacity(0.4)
                    infoLine("本机标识", "\(UIDevice.current.userInterfaceIdiom == .pad ? "iPadOS" : "iOS") · \(model.discovery.displayName)")
                    Divider().opacity(0.4)
                    infoLine("状态", model.statusLine)
                }
                .padding(14)
                .glassSurface(cornerRadius: 20)
            }
            .padding(.horizontal, 18)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .scrollIndicators(.hidden)
    }

    // MARK: - 片段

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(tab == .send ? "投送" : tab.title)
                .font(.system(size: 30, weight: .bold))
                .foregroundStyle(AGTheme.ink)
            Text("\(model.peers.count) 台设备在附近 · \(model.channel.title) · 隔空手势已开启")
                .font(.system(size: 12))
                .foregroundStyle(AGTheme.inkSecondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 4)
    }

    private func sectionCard(title: String, items: [Payload], empty: String) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(AGTheme.ink)

            if items.isEmpty {
                Text(empty)
                    .font(.system(size: 12))
                    .foregroundStyle(AGTheme.inkTertiary)
                    .padding(.vertical, 8)
            } else {
                ForEach(items.prefix(6)) { payload in
                    PayloadRow(payload: payload)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func emptyCard(title: String, detail: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(AGTheme.ink)
            Text(detail)
                .font(.system(size: 12))
                .foregroundStyle(AGTheme.inkSecondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .glassSurface(cornerRadius: 20)
    }

    private func infoLine(_ title: String, _ value: String) -> some View {
        HStack {
            Text(title)
                .font(.system(size: 12))
                .foregroundStyle(AGTheme.inkSecondary)
            Spacer(minLength: 12)
            Text(value)
                .font(.system(size: 11.5, design: .monospaced))
                .foregroundStyle(AGTheme.ink)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    // MARK: - 拖拽（模拟手势通道）

    private func dragGesture(for payload: Payload) -> some Gesture {
        DragGesture(minimumDistance: 10, coordinateSpace: .global)
            .onChanged { value in
                if dragging == nil {
                    dragging = payload
                    model.beginSimulatedDrag(payload)
                }
                dragTranslation = value.translation
                dropTarget = model.peers.first
            }
            .onEnded { _ in
                model.simulateDrop(on: dropTarget)
                dragging = nil
                dragTranslation = .zero
                dropTarget = nil
            }
    }

    @ViewBuilder
    private var dragOverlay: some View {
        if let dragging {
            HStack(spacing: 10) {
                Image(systemName: dragging.kind.symbol)
                    .font(.system(size: 15, weight: .medium))
                    .foregroundStyle(.white)
                    .frame(width: 32, height: 32)
                    .background(dragging.kind.gradient, in: RoundedRectangle(cornerRadius: 10, style: .continuous))

                VStack(alignment: .leading, spacing: 2) {
                    Text(dragging.name)
                        .font(.system(size: 12.5, weight: .semibold))
                        .foregroundStyle(AGTheme.ink)
                        .lineLimit(1)
                    Text(dropTarget.map { "松开投送至 \($0.name)" } ?? "松手返回原处")
                        .font(.system(size: 10.5))
                        .foregroundStyle(AGTheme.inkSecondary)
                        .lineLimit(1)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .glassSurface(cornerRadius: 16, tint: AGTheme.accent)
            .offset(y: 64)
            .shadow(color: .black.opacity(0.22), radius: 18, y: 10)
            .allowsHitTesting(false)
            .transition(.scale(scale: 0.8).combined(with: .opacity))
        }
    }

    @ViewBuilder
    private var toastOverlay: some View {
        if let toast = model.toast {
            HStack(spacing: 11) {
                ZStack {
                    RoundedRectangle(cornerRadius: 11, style: .continuous)
                        .fill(LinearGradient(colors: [AGTheme.ok, Color(red: 0.06, green: 0.63, blue: 0.42)],
                                             startPoint: .topLeading, endPoint: .bottomTrailing))
                    Image(systemName: "checkmark")
                        .font(.system(size: 15, weight: .bold))
                        .foregroundStyle(.white)
                }
                .frame(width: 34, height: 34)

                VStack(alignment: .leading, spacing: 2) {
                    Text(toast)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white)
                        .lineLimit(1)
                    if let detail = model.toastDetail {
                        Text(detail)
                            .font(.system(size: 11))
                            .foregroundStyle(.white.opacity(0.72))
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 15)
            .padding(.vertical, 12)
            .background(Color(red: 0.07, green: 0.09, blue: 0.15).opacity(0.93),
                        in: RoundedRectangle(cornerRadius: 17, style: .continuous))
            .shadow(color: .black.opacity(0.34), radius: 22, y: 10)
            .padding(.horizontal, 20)
            .padding(.bottom, 96)
            .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }
}

/// 说明型玻璃卡，用于承载协议 / 机制描述。
struct GlassNote: View {
    let title: String
    let detail: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundStyle(AGTheme.ink)
            Text(detail)
                .font(.system(size: 11.5))
                .foregroundStyle(AGTheme.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .glassSurface(cornerRadius: 18)
    }
}
