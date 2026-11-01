import SwiftUI
import UniformTypeIdentifiers

struct EditorWindow: View {
    @EnvironmentObject var c: AppController
    @State private var selection: ID?
    @State private var tab = Tab.easy

    enum Tab: String, CaseIterable { case easy = "Easy", pro = "Pro", device = "Device & Rules" }

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                Section("Profiles") {
                    ForEach(c.state?.library.profiles ?? []) { p in
                        HStack {
                            Text(p.name)
                            Spacer()
                            if p.id == c.activeProfile?.id {
                                Image(systemName: "speaker.wave.2.fill").foregroundStyle(Color.accentColor).font(.caption)
                            }
                            if c.state?.currentGroup?.favorites.contains(p.id) == true {
                                Image(systemName: "star.fill").foregroundStyle(.yellow).font(.caption)
                            }
                        }
                        .tag(p.id)
                        .contextMenu {
                            Button("Use Now") { c.select(p.id) }
                            Button("Duplicate") { duplicate(p) }
                            Button("Export Equalizer APO…") { export(p) }
                            Divider()
                            Button("Delete", role: .destructive) { c.delete(p.id) }
                        }
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 200, ideal: 230)
            .toolbar {
                ToolbarItem {
                    Button { add() } label: { Image(systemName: "plus") }.help("New profile")
                }
            }
        } detail: {
            VStack(spacing: 0) {
                Picker("", selection: $tab) {
                    ForEach(Tab.allCases, id: \.self) { Text($0.rawValue) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 420).padding(.vertical, 10)

                if tab == .device {
                    DeviceSettings()
                } else if let id = selection ?? c.activeProfile?.id, let p = c.state?.profile(id) {
                    ProfileEditor(original: p, pro: tab == .pro).id(p.id)
                } else {
                    ContentUnavailableView("Pick a profile", systemImage: "slider.horizontal.3")
                }
            }
        }
        .onAppear { selection = selection ?? c.activeProfile?.id }
    }

    private func add() {
        let p = Profile.new("New Profile")
        c.save(p)
        selection = p.id
    }

    private func duplicate(_ p: Profile) {
        var d = p
        d.id = newID()
        d.name = p.name + " copy"
        d.tags.removeAll { $0 == "builtin" }
        c.save(d)
        selection = d.id
    }

    private func export(_ p: Profile) {
        guard let text = c.exportAPO(p.id) else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "\(p.name).txt"
        panel.allowedContentTypes = [.plainText]
        if panel.runModal() == .OK, let url = panel.url {
            try? text.write(to: url, atomically: true, encoding: .utf8)
        }
    }
}

/// Edits a draft copy; every change is auditioned live and saved (debounced).
struct ProfileEditor: View {
    @EnvironmentObject var c: AppController
    let original: Profile
    let pro: Bool
    @State private var draft: Profile
    @State private var selectedBand: Int?
    @State private var audition = true

    init(original: Profile, pro: Bool) {
        self.original = original
        self.pro = pro
        _draft = State(initialValue: original)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField("Name", text: $draft.name).textFieldStyle(.plain).font(.title2.weight(.semibold))
                Spacer()
                Toggle("Audition", isOn: $audition).toggleStyle(.switch).controlSize(.small)
                    .help("Hear this profile while editing, even if it isn't the active one")
                Button("Use Now") { c.select(draft.id) }
                    .disabled(c.activeProfile?.id == draft.id)
            }
            CurveView(profile: $draft, selected: $selectedBand, interactive: pro)
                .frame(minHeight: 240)
            ScrollView {
                if pro { ProPanel(profile: $draft, selected: $selectedBand) } else { EasyPanel(profile: $draft) }
            }
        }
        .padding([.horizontal, .bottom], 16)
        .onChange(of: draft) { _, d in
            if audition { c.preview(d) }
            c.save(d, debounce: true)
        }
        .onChange(of: audition) { _, on in c.preview(on ? draft : nil) }
        .onAppear { if audition { c.preview(draft) } }
        .onDisappear { c.save(draft); c.preview(nil) }
    }
}

struct EasyPanel: View {
    @EnvironmentObject var c: AppController
    @Binding var profile: Profile

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            GroupBox("Tone") {
                VStack {
                    row("Bass", \.bass, "105 Hz shelf")
                    row("Mids", \.mids, "1 kHz")
                    row("Clarity", \.clarity, "3 kHz presence")
                    row("Treble", \.treble, "8 kHz shelf")
                }.padding(6)
            }
            GroupBox("Headphone correction") {
                HStack {
                    if let src = profile.correctionSource {
                        Label("\(src.provider): \(src.name) · \(profile.correction.count) filters", systemImage: "checkmark.seal.fill")
                        Spacer()
                        Button("Remove") { profile.correction = []; profile.correctionSource = nil }
                    } else {
                        Text("None. Import an AutoEQ ParametricEQ.txt to correct your headphones' sound.")
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button("Import AutoEQ…") { c.importFile(asCorrection: true) }
                    }
                }.padding(6)
            }
            GroupBox("Comfort") {
                VStack(alignment: .leading) {
                    HStack {
                        Text("Loudness").frame(width: 70, alignment: .leading)
                        Slider(value: $profile.loudness, in: 0...1)
                        Text(profile.loudness == 0 ? "off" : "\(Int(profile.loudness * 100))%")
                            .font(.caption.monospacedDigit()).frame(width: 38)
                    }
                    Toggle("Clipping protection (limiter)", isOn: $profile.limiter)
                }.padding(6)
            }
        }
    }

    private func row(_ name: String, _ kp: WritableKeyPath<Macros, Double>, _ hint: String) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 0) {
                Text(name)
                Text(hint).font(.caption2).foregroundStyle(.secondary)
            }.frame(width: 100, alignment: .leading)
            Slider(value: Binding(get: { profile.macros[keyPath: kp] },
                                  set: { profile.macros[keyPath: kp] = ($0 * 2).rounded() / 2 }), in: -12...12)
            Text(String(format: "%+.1f dB", profile.macros[keyPath: kp]))
                .font(.callout.monospacedDigit()).frame(width: 64, alignment: .trailing)
        }
    }
}
