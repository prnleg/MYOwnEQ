import SwiftUI

/// Menubar popover: device, on/off, why-this-profile, favorites, Easy knobs.
struct QuickPanel: View {
    @EnvironmentObject var c: AppController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if let err = c.engineError {
                VStack(alignment: .leading, spacing: 6) {
                    Label(err, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).font(.callout).fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button("Open Privacy Settings") { c.openPrivacySettings() }
                        Button("Retry") { c.restartEngine() }
                    }.controlSize(.small)
                }
            }
            Divider()
            activeRow
            favorites
            if let p = c.activeProfile {
                EasyKnobs(profile: p).padding(.top, 4)
            }
            Divider()
            footer
            if let m = c.lastMessage {
                Text(m).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(14)
        .frame(width: 330)
    }

    private var header: some View {
        HStack {
            Image(systemName: c.current?.kind.symbol ?? "speaker.slash")
                .font(.title2).frame(width: 28)
            VStack(alignment: .leading, spacing: 1) {
                Text(c.state?.currentGroup?.name ?? c.current?.name ?? "No output").font(.headline).lineLimit(1)
                Text(c.engineRunning ? "\(Int((c.state?.sampleRate ?? 0) / 1000)) kHz · EQ on" : "EQ off")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Toggle("", isOn: Binding(get: { c.state?.enabled ?? true }, set: { c.setEnabled($0) }))
                .toggleStyle(.switch).labelsHidden()
        }
    }

    private var activeRow: some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(c.activeProfile?.name ?? "Flat").font(.title3.weight(.semibold))
                Text(c.reason).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Menu {
                ForEach(c.state?.library.profiles ?? []) { p in
                    Button(p.name) { c.select(p.id) }
                }
            } label: { Text("Change") }
                .menuStyle(.borderlessButton).fixedSize()
            if let p = c.activeProfile {
                let fav = c.state?.currentGroup?.favorites.contains(p.id) ?? false
                Button { c.toggleFavorite(p.id) } label: {
                    Image(systemName: fav ? "star.fill" : "star")
                }.buttonStyle(.borderless).help("Favorite for this device")
            }
        }
    }

    @ViewBuilder private var favorites: some View {
        let favs = (c.state?.currentGroup?.favorites ?? []).compactMap { c.state?.profile($0) }
        if !favs.isEmpty {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 90), spacing: 6)], spacing: 6) {
                ForEach(favs) { p in
                    let active = p.id == c.activeProfile?.id
                    Button { c.select(p.id) } label: {
                        Text(p.name).lineLimit(1).frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)
                    .tint(active ? .accentColor : nil)
                }
            }
        }
    }

    private var footer: some View {
        HStack {
            Button("Editor…") {
                openWindow(id: "editor")
                NSApp.activate(ignoringOtherApps: true)
            }
            Menu("Import") {
                Button("eqMac presets") { c.importEqMac() }
                Button("AutoEQ ParametricEQ.txt…") { c.importFile(asCorrection: true) }
                Button("Equalizer APO / Peace config…") { c.importFile(asCorrection: false) }
            }.fixedSize()
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }
        }.controlSize(.small)
    }
}

/// Bass / Mids / Clarity / Treble knobs. Live preview while dragging, save on release.
struct EasyKnobs: View {
    @EnvironmentObject var c: AppController
    let profile: Profile
    @State private var draft: Profile?

    var body: some View {
        let p = draft ?? profile
        VStack(spacing: 6) {
            knob("Bass", \.bass, p)
            knob("Mids", \.mids, p)
            knob("Clarity", \.clarity, p)
            knob("Treble", \.treble, p)
        }
    }

    private func knob(_ label: String, _ kp: WritableKeyPath<Macros, Double>, _ p: Profile) -> some View {
        HStack {
            Text(label).frame(width: 52, alignment: .leading).font(.callout)
            Slider(value: Binding(
                get: { p.macros[keyPath: kp] },
                set: { v in
                    var d = draft ?? profile
                    d.macros[keyPath: kp] = (v * 2).rounded() / 2
                    draft = d
                    c.preview(d)
                }
            ), in: -12...12) { editing in
                if !editing, let d = draft {
                    c.save(d)
                    c.preview(nil)
                    draft = nil
                }
            }
            Text(String(format: "%+.1f", p.macros[keyPath: kp]))
                .font(.caption.monospacedDigit()).frame(width: 38, alignment: .trailing)
        }
    }
}
