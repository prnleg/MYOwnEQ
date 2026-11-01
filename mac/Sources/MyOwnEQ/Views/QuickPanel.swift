import SwiftUI

/// Menubar popover: device, on/off, why-this-profile, live curve, favorites, Easy knobs.
struct QuickPanel: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Environment(\.openWindow) private var openWindow
    @State private var draft: Profile?

    var body: some View {
        let t = theme.current
        VStack(alignment: .leading, spacing: 12) {
            header
            if let err = c.engineError {
                VStack(alignment: .leading, spacing: 8) {
                    Label(err, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).font(.callout).fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button(L("Open Privacy Settings")) { c.openPrivacySettings() }.buttonStyle(GlowButtonStyle(prominent: false))
                        Button(L("Retry")) { c.restartEngine() }.buttonStyle(GlowButtonStyle())
                    }
                }
                .padding(10).background(GlassBackground(radius: 12))
            }
            if let u = c.update, u.isReady {
                HStack {
                    Label(L("MyOwnEQ %@ ready", u.version ?? ""), systemImage: "arrow.down.circle.fill").font(.callout)
                    Spacer()
                    Button(L("Restart")) { c.installUpdateAndQuit() }.buttonStyle(GlowButtonStyle())
                }
                .padding(10).background(GlassBackground(radius: 12, highlighted: true))
                .transition(.move(edge: .top).combined(with: .opacity))
            }

            nowPlaying
            favorites
            if c.activeProfile != nil {
                EasyKnobs(draft: $draft).padding(12).background(GlassBackground(radius: 14))
            }
            footer
            if let m = c.lastMessage {
                Text(m).font(.caption).foregroundStyle(.secondary).transition(.opacity)
            }
        }
        .padding(14)
        .frame(width: 340)
        .background(ThemeBackground(compact: true))
        .tint(t.accentColor)
        .environment(\.colorScheme, t.colorScheme)
        .animation(.spring(response: 0.4, dampingFraction: 0.8), value: c.activeProfile?.id)
        .animation(.spring(response: 0.4, dampingFraction: 0.8), value: c.update?.isReady)
    }

    private var header: some View {
        HStack(spacing: 10) {
            AppLogo()
                .frame(width: 32, height: 32)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 5) {
                    Image(systemName: c.current?.kind.symbol ?? "speaker.slash").font(.caption)
                    Text(c.state?.currentGroup?.name ?? c.current?.name ?? L("No output")).font(.headline).lineLimit(1)
                }
                Text(c.engineRunning ? "\(Int((c.state?.sampleRate ?? 0) / 1000)) kHz · EQ on" : "EQ off")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if let sync = c.state?.sync, sync.mode != "off" {
                Image(systemName: sync.status == "connected" ? "arrow.triangle.2.circlepath" : "exclamationmark.arrow.triangle.2.circlepath")
                    .foregroundStyle(sync.status == "connected" ? Color.secondary : Color.orange)
                    .help(sync.status == "connected" ? L("Synced") : L("Sync: %@ %@", sync.status, sync.detail))
            }
            Toggle("", isOn: Binding(get: { c.state?.enabled ?? true }, set: { c.setEnabled($0) }))
                .toggleStyle(.switch).labelsHidden()
        }
    }

    private var nowPlaying: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(c.activeProfile?.name ?? L("Flat")).font(.title3.weight(.bold))
                        .contentTransition(.opacity)
                    Text(c.reason).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Menu {
                    ForEach(c.state?.library.profiles ?? []) { p in
                        Button(p.name) { c.select(p.id) }
                    }
                } label: { Text(L("Change")) }
                    .menuStyle(.borderlessButton).fixedSize()
                if let p = c.activeProfile {
                    let fav = c.state?.currentGroup?.favorites.contains(p.id) ?? false
                    Button { c.toggleFavorite(p.id) } label: {
                        Image(systemName: fav ? "star.fill" : "star")
                            .foregroundStyle(fav ? AnyShapeStyle(.yellow) : AnyShapeStyle(.secondary))
                            .symbolEffect(.bounce, value: fav)
                    }.buttonStyle(.borderless).help(L("Favorite for this device"))
                }
            }
            if let p = draft ?? c.activeProfile {
                CurveView(profile: .constant(p), selected: .constant(nil), interactive: false, compact: true)
                    .frame(height: 74)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
        }
        .padding(12)
        .background(GlassBackground(radius: 14, highlighted: c.engineRunning && c.state?.enabled != false))
    }

    @ViewBuilder private var favorites: some View {
        let favs = (c.state?.currentGroup?.favorites ?? []).compactMap { c.state?.profile($0) }
        if !favs.isEmpty {
            let t = theme.current
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 94), spacing: 6)], spacing: 6) {
                ForEach(favs) { p in
                    let active = p.id == c.activeProfile?.id
                    Button { c.select(p.id) } label: {
                        Text(p.name).font(.callout.weight(active ? .semibold : .regular)).lineLimit(1)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                            .foregroundStyle(active ? .white : t.ink)
                            .background {
                                if active {
                                    Capsule().fill(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                                        .shadow(color: t.accentColor.opacity(0.5), radius: 6)
                                } else {
                                    Capsule().fill(t.ink.opacity(0.08)).overlay(Capsule().strokeBorder(t.ink.opacity(0.12)))
                                }
                            }
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var footer: some View {
        HStack {
            Button {
                openWindow(id: "editor")
                NSApp.activate(ignoringOtherApps: true)
            } label: { Label(L("Editor"), systemImage: "slider.horizontal.3") }
                .buttonStyle(GlowButtonStyle())
            Menu(L("Import")) {
                Button(L("eqMac presets")) { c.importEqMac() }
                Button(L("AutoEQ ParametricEQ.txt…")) { c.importFile(asCorrection: true) }
                Button(L("Equalizer APO / Peace config…")) { c.importFile(asCorrection: false) }
            }.menuStyle(.borderlessButton).fixedSize()
            Spacer()
            Button(L("Quit")) { NSApp.terminate(nil) }.buttonStyle(GlowButtonStyle(prominent: false))
        }
    }
}

/// Bass / Mids / Clarity / Treble. Live preview while dragging, save on release.
struct EasyKnobs: View {
    @EnvironmentObject var c: AppController
    @Binding var draft: Profile?

    var body: some View {
        if let p = draft ?? c.activeProfile {
            VStack(spacing: 8) {
                knob(L("Bass"), \.bass, p)
                knob(L("Mids"), \.mids, p)
                knob(L("Clarity"), \.clarity, p)
                knob(L("Treble"), \.treble, p)
            }
        }
    }

    private func knob(_ label: String, _ kp: WritableKeyPath<Macros, Double>, _ p: Profile) -> some View {
        HStack(spacing: 10) {
            Text(label).frame(width: 52, alignment: .leading).font(.callout.weight(.medium))
            GlowSlider(value: Binding(
                get: { p.macros[keyPath: kp] },
                set: { v in
                    guard var d = draft ?? c.activeProfile else { return }
                    d.macros[keyPath: kp] = v
                    draft = d
                    c.preview(d)
                }
            ), range: -12...12, step: 0.5) { editing in
                if !editing, let d = draft {
                    c.save(d)
                    c.preview(nil)
                    draft = nil
                }
            }
            Text(String(format: "%+.1f", p.macros[keyPath: kp]))
                .font(.caption.monospacedDigit().weight(.medium))
                .contentTransition(.numericText(value: p.macros[keyPath: kp]))
                .frame(width: 36, alignment: .trailing)
        }
    }
}
