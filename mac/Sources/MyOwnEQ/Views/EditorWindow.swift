import SwiftUI
import UniformTypeIdentifiers

enum Nav: Hashable {
    case profile(ID)
    case devices, sync, appearance
}

struct EditorWindow: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @EnvironmentObject var lang: Lang
    @State private var nav: Nav?
    @State private var search = ""
    @StateObject private var session = EditSession()
    /// Copies made from presets map to the editor they came from, so the
    /// editor (and an in-progress drag) survives the switch to the copy.
    @State private var forks: [ID: ID] = [:]

    private func editorKey(_ id: ID) -> ID {
        var k = id
        while let p = forks[k] { k = p }
        return k
    }

    private func go(_ n: Nav?) {
        guard n != nav else { return }
        if session.confirmLeave() { nav = n }
    }

    var body: some View {
        NavigationSplitView {
            Sidebar(nav: Binding(get: { nav }, set: { go($0) }), search: $search)
                .navigationSplitViewColumnWidth(min: 260, ideal: 290, max: 380)
        } detail: {
            ZStack {
                ThemeBackground()
                Group {
                    switch nav {
                    case .devices:
                        page(L("Devices & Rules"), L("Default profile, favorites, linked headphones and app rules"), "headphones") { DeviceSettings() }
                    case .sync:
                        page(L("Sync & Updates"), L("Keep your Mac and PC in step"), "arrow.triangle.2.circlepath") { SyncSettings() }
                    case .appearance: AppearanceSettings()
                    case .profile(let id):
                        if let p = c.state?.profile(id) {
                            ProfileEditor(original: p) { new, old in
                                forks[new] = editorKey(old)
                                nav = .profile(new)
                            }
                            .id(editorKey(p.id))
                        } else { empty }
                    case nil: empty
                    }
                }
                .transition(.asymmetric(insertion: .opacity.combined(with: .offset(y: 10)), removal: .opacity))
            }
            .modifier(ClearTitleBar())
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: nav)
            .overlay(alignment: .bottom) { NoticeToast().padding(.bottom, 18) }
        }
        .id(lang.code) // re-render every text when the language changes; `nav` survives
        .environmentObject(session)
        .tint(theme.current.accentColor)
        .preferredColorScheme(theme.current.colorScheme)
        .onAppear {
            if nav == nil, let id = c.activeProfile?.id { nav = .profile(id) }
        }
    }

    private func page<V: View>(_ title: String, _ subtitle: String, _ icon: String, @ViewBuilder _ v: () -> V) -> some View {
        VStack(spacing: 0) {
            PageHeader(title: title, subtitle: subtitle, icon: icon).padding([.horizontal, .top], 20).padding(.bottom, 4)
            v().scrollContentBackground(.hidden)
        }
    }

    private var empty: some View {
        VStack(spacing: 14) {
            AppLogo()
                .frame(width: 84, height: 84)
            Text(L("Pick a profile")).font(.title3.weight(.semibold))
            Text(L("or make a new one with +")).foregroundStyle(.secondary)
        }
    }
}

/// One title bar for every page: transparent, theme background showing through.
/// Pages that scroll (Form / ScrollView: Devices, Sync, Preferences) otherwise get the
/// toolbar material and, on macOS 26+, the scroll-edge blur, so the bar "changed"
/// while navigating. The window title text is hidden too (the sidebar shows the logo).
struct ClearTitleBar: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26.0, *) {
            content
                .toolbarBackground(.hidden, for: .windowToolbar)
                .toolbar(removing: .title)
                .scrollEdgeEffectHidden(true, for: .top)
        } else if #available(macOS 15.0, *) {
            content
                .toolbarBackground(.hidden, for: .windowToolbar)
                .toolbar(removing: .title)
        } else {
            content.toolbarBackground(.hidden, for: .windowToolbar)
        }
    }
}

// MARK: - sidebar

// parity: sidebar
struct Sidebar: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Binding var nav: Nav?
    @Binding var search: String
    @AppStorage("showPresets") private var showPresets = true
    @State private var sharing: ShareInfo?

    var body: some View {
        let s = c.state
        let favIds = Set(s?.currentGroup?.favorites ?? [])
        let all = (s?.library.profiles ?? []).filter { search.isEmpty || $0.name.localizedCaseInsensitiveContains(search) }
        let favs = all.filter { favIds.contains($0.id) }
        let mine = all.filter { !favIds.contains($0.id) && !$0.isBuiltin }
        let presets = all.filter { !favIds.contains($0.id) && $0.isBuiltin }

        List(selection: $nav) {
            NowPlayingCard()
                .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 8, trailing: 0))
                .listRowSeparator(.hidden)
                .selectionDisabled()

            if !favs.isEmpty {
                Section(L("Favorites")) { ForEach(favs) { row($0) } }
            }
            Section(L("My Profiles")) {
                ForEach(mine) { row($0) }
                if mine.isEmpty {
                    Text(search.isEmpty ? L("Edit a preset or press + to make your own") : L("Nothing matches “%@”", search))
                        .foregroundStyle(.secondary).font(.callout)
                }
            }
            if !presets.isEmpty {
                Section {
                    if showPresets || !search.isEmpty { ForEach(presets) { row($0) } }
                } header: {
                    HStack {
                        Text(L("Presets"))
                        Spacer()
                        Button {
                            withAnimation(.spring(response: 0.3)) { showPresets.toggle() }
                        } label: {
                            Image(systemName: "chevron.right")
                                .rotationEffect(.degrees(showPresets ? 90 : 0))
                        }
                        .buttonStyle(.borderless)
                        .help(showPresets ? L("Hide presets") : L("Show presets"))
                    }
                }
            }
            Section(L("Settings")) {
                Label(L("Devices & Rules"), systemImage: "headphones").tag(Nav.devices)
                Label(L("Sync & Updates"), systemImage: "arrow.triangle.2.circlepath").tag(Nav.sync)
                Label(L("Preferences"), systemImage: "paintpalette").tag(Nav.appearance)
            }
        }
        .listStyle(.sidebar)
        .searchable(text: $search, placement: .sidebar, prompt: L("Search profiles"))
        .toolbar {
            ToolbarItem {
                Button {
                    if let id = c.importCodeFromClipboard() { nav = .profile(id) }
                } label: { Image(systemName: "square.and.arrow.down.on.square") }
                    .help(L("Import a share code from the clipboard"))
            }
            ToolbarItem {
                Button { add() } label: { Image(systemName: "plus") }.help(L("New profile"))
            }
        }
        .sheet(item: $sharing) { ShareSheet(info: $0) }
    }

    private func row(_ p: Profile) -> some View {
        ProfileRow(profile: p, active: p.id == c.activeProfile?.id)
            .tag(Nav.profile(p.id))
            .contextMenu {
                Button(L("Use Now")) { c.select(p.id) }
                Button(L("Rename…")) { promptRename(p) }.disabled(p.isBuiltin)
                Button(c.state?.currentGroup?.favorites.contains(p.id) == true ? L("Remove from Favorites") : L("Add to Favorites")) {
                    c.toggleFavorite(p.id)
                }
                Button(L("Duplicate")) { duplicate(p) }
                Button(L("Share…")) { sharing = c.share(p) }
                Button(L("Export Equalizer APO…")) { export(p) }
                Divider()
                // parity: delete-confirm
                Button(L("Delete"), role: .destructive) {
                    let a = NSAlert()
                    a.messageText = L("Delete “%@”? This syncs to your other machines.", p.name)
                    a.alertStyle = .warning
                    a.addButton(withTitle: L("Delete"))
                    a.addButton(withTitle: L("Cancel"))
                    if a.runModal() == .alertFirstButtonReturn { c.delete(p.id) }
                }
                .disabled(p.isBuiltin)
            }
    }

    // parity: rename
    private func promptRename(_ p: Profile) {
        if let name = askName(L("Rename “%@”", p.name), current: p.name) { c.rename(p.id, to: name) }
    }

    private func add() {
        let p = Profile.new(L("New Profile"))
        c.save(p)
        nav = .profile(p.id)
    }

    private func duplicate(_ p: Profile) {
        var d = p
        d.id = newID()
        d.name = p.name + L(" copy")
        d.tags.removeAll { $0 == "builtin" }
        c.save(d)
        nav = .profile(d.id)
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

struct ProfileRow: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    let profile: Profile
    let active: Bool
    @State private var shape: [Double] = []

    var body: some View {
        HStack(spacing: 10) {
            MiniCurve(values: shape)
                .frame(width: 34, height: 18)
                .padding(4)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(theme.current.ink.opacity(0.07)))
            Text(profile.name).lineLimit(1)
            Spacer(minLength: 4)
            if active {
                Image(systemName: "waveform")
                    .symbolEffect(.variableColor.iterative, options: .repeating, isActive: c.engineRunning && theme.motionAllowed)
                    .foregroundStyle(theme.current.accentColor)
                    .font(.caption.weight(.bold))
                    .help(L("Playing now"))
            }
        }
        .padding(.vertical, 2)
        .onAppear(perform: refresh)
        .onChange(of: profile) { _, _ in refresh() }
    }

    private func refresh() {
        guard let cv = c.curve(for: profile, points: 40) else { return }
        shape = cv.total.map { $0 - cv.preamp }
    }
}

/// Top of the sidebar: logo, output device, what's playing and the master switch.
struct NowPlayingCard: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore

    var body: some View {
        let t = theme.current
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                AppLogo().frame(width: 30, height: 30)
                Text("MyOwnEQ").font(.headline.weight(.heavy)).lineLimit(1).minimumScaleFactor(0.8)
                Spacer(minLength: 4)
                Toggle("", isOn: Binding(get: { c.state?.enabled ?? true }, set: { c.setEnabled($0) }))
                    .toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            HStack(spacing: 8) {
                Image(systemName: c.current?.kind.symbol ?? "speaker.slash")
                    .frame(width: 18)
                    .foregroundStyle(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                VStack(alignment: .leading, spacing: 1) {
                    Text(c.state?.currentGroup?.name ?? c.current?.name ?? L("No output")).font(.callout.weight(.semibold)).lineLimit(1)
                    Text("\(c.activeProfile?.name ?? L("Flat")) · \(c.reason)").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(GlassBackground(radius: 12, highlighted: c.engineRunning && c.state?.enabled != false))
    }
}

// MARK: - profile editor

/// Edits a draft copy, saved (debounced). Only the active profile is heard
/// while editing; any other one is heard only while "Hold to listen" is held.
struct ProfileEditor: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @EnvironmentObject var session: EditSession
    let original: Profile
    /// Called after a preset was copied into a new profile (new id, old id).
    var onFork: (ID, ID) -> Void = { _, _ in }
    @State private var draft: Profile
    /// Last version written to the library (to know what's unsaved).
    @State private var saved: Profile
    @State private var selectedBand: Int?
    @State private var holding = false
    @AppStorage("editorPro") private var pro = false
    @AppStorage("autoSave") private var autoSave = true
    @FocusState private var nameFocused: Bool
    /// Set while adopting an outside change (rename, sync) into the draft.
    @State private var adopting = false

    init(original: Profile, onFork: @escaping (ID, ID) -> Void = { _, _ in }) {
        self.original = original
        self.onFork = onFork
        _draft = State(initialValue: original)
        _saved = State(initialValue: original)
    }

    private var dirty: Bool { draft != saved }

    var body: some View {
        let isActive = c.activeProfile?.id == draft.id
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 8) {
                            TextField(L("Name"), text: $draft.name).textFieldStyle(.plain).font(.system(size: 26, weight: .bold))
                                .help(draft.isBuiltin ? L("Presets can't be renamed. Change anything and a copy is made for you.") : L("Rename"))
                                .disabled(draft.isBuiltin)
                                .focused($nameFocused)
                                .onSubmit { nameFocused = false }
                                .fixedSize()
                            if !draft.isBuiltin {
                                Button { nameFocused = true } label: {
                                    Image(systemName: "pencil").font(.title3)
                                }
                                .buttonStyle(.borderless)
                                .foregroundStyle(nameFocused ? AnyShapeStyle(theme.current.accentColor) : AnyShapeStyle(.secondary))
                                .help(L("Rename"))
                            }
                        }
                        HStack(spacing: 6) {
                            if isActive { Tag(text: L("PLAYING"), color: theme.current.accentColor) }
                            if draft.isBuiltin { Tag(text: L("PRESET"), color: .secondary) }
                            if let src = draft.correctionSource { Tag(text: "AUTOEQ · \(src.name.uppercased())", color: .secondary) }
                        }
                    }
                    Spacer()
                    PillPicker(selection: $pro, options: [(false, L("Easy")), (true, L("Pro"))])
                }
                if draft.isBuiltin {
                    Label(L("This is a preset. Change anything and a copy is created for you; the preset stays untouched."),
                          systemImage: "doc.on.doc")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if !autoSave && dirty {
                    HStack {
                        Label(L("Unsaved changes"), systemImage: "pencil.circle.fill")
                            .foregroundStyle(theme.current.accentColor).font(.callout.weight(.semibold))
                        Spacer()
                        Button(L("Discard")) { discard() }.buttonStyle(GlowButtonStyle(prominent: false))
                        Button(L("Save")) { commit() }.buttonStyle(GlowButtonStyle()).keyboardShortcut("s", modifiers: .command)
                    }
                    .padding(10)
                    .background(GlassBackground(radius: 12, highlighted: true))
                    .transition(.move(edge: .top).combined(with: .opacity))
                }
                HStack {
                    if !isActive {
                        HoldToListen(holding: $holding)
                    }
                    Spacer()
                    if isActive {
                        Label(L("In use"), systemImage: "checkmark.circle.fill").foregroundStyle(theme.current.accentColor).font(.callout.weight(.semibold))
                    } else {
                        Button(L("Use Now")) {
                            if dirty { commit() }
                            withAnimation(.spring) { c.select(draft.id) }
                        }.buttonStyle(GlowButtonStyle())
                    }
                }

                CurveView(profile: $draft, selected: $selectedBand, edit: pro ? .pro : .easy)
                    .frame(height: 300)
                    .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                    .background(GlassBackground(radius: 16))
                    .overlay(alignment: .bottomTrailing) {
                        Text(hint).font(.caption2).foregroundStyle(.secondary).padding(10)
                    }

                Group {
                    if pro { ProPanel(profile: $draft, selected: $selectedBand) } else { EasyPanel(profile: $draft) }
                }
                .transition(.opacity.combined(with: .scale(scale: 0.98, anchor: .top)))
            }
            .padding(20)
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: pro)
            .animation(.spring(response: 0.35, dampingFraction: 0.85), value: dirty)
        }
        .scrollContentBackground(.hidden)
        .onChange(of: draft) { _, d in changed(d, wasActive: isActive) }
        .onChange(of: holding) { _, on in c.preview(on ? draft : nil) }
        .onChange(of: c.activeProfile?.id) { _, id in
            if id != draft.id, !holding { c.preview(nil) }
        }
        .onAppear(perform: register)
        .onChange(of: dirty) { _, _ in register() }
        .onChange(of: autoSave) { _, on in if on && dirty { commit() } }
        .onChange(of: original) { _, o in
            // Outside change (sidebar rename, sync from the other machine):
            // take it, unless we have our own edits waiting to be written.
            guard o != draft, o.id == draft.id, !dirty, c.pendingSave?.id != draft.id else { return }
            adopting = true
            draft = o
            saved = o
        }
        .onDisappear {
            if autoSave { c.flushSave() } else if dirty, ask(draft.name) == .alertFirstButtonReturn { commit() }
            c.preview(nil)
            session.clear()
        }
    }

    private var hint: String {
        if !pro { return L("Drag the dots: Bass, Mids, Clarity, Treble") }
        if case .graphic = draft.eq { return L("Drag the sliders up and down on the graph") }
        return L("Drag the dots · Shift-drag or wheel changes Q · double-click adds a band")
    }

    private func changed(_ d: Profile, wasActive: Bool) {
        if adopting {
            adopting = false
            return
        }
        // Editing a preset: the core copies it into a new profile (and keeps it
        // playing if it was); keep editing the copy.
        // parity: preset-fork
        if d.isBuiltin, d.id == original.id, d != original, let copy = c.forkPreset(d, originalName: original.name) {
            saved = copy
            adopting = true
            draft = copy
            onFork(copy.id, original.id)
            return
        }
        if c.activeProfile?.id == d.id || holding { c.preview(d) }
        if autoSave {
            c.save(d, debounce: true)
            saved = d
        }
    }

    private func commit() {
        c.save(draft)
        saved = draft
    }

    private func discard() {
        draft = saved
        if c.activeProfile?.id == draft.id || holding { c.preview(draft) }
    }

    private func register() {
        session.dirtyName = (!autoSave && dirty) ? draft.name : nil
        session.commit = { commit() }
        session.discard = { discard() }
    }
}

/// Lets the window ask "save changes?" before leaving a profile with unsaved edits.
// parity: auto-save
final class EditSession: ObservableObject {
    var dirtyName: String?
    var commit: () -> Void = {}
    var discard: () -> Void = {}

    func clear() {
        dirtyName = nil
        commit = {}
        discard = {}
    }

    /// Returns false if the user cancelled.
    func confirmLeave() -> Bool {
        guard let name = dirtyName else { return true }
        switch ask(name, cancel: true) {
        case .alertFirstButtonReturn: commit()
        case .alertSecondButtonReturn: discard()
        default: return false
        }
        dirtyName = nil
        return true
    }
}

/// "Save changes to X?" — Save / Don't Save (/ Cancel).
func ask(_ name: String, cancel: Bool = false) -> NSApplication.ModalResponse {
    let a = NSAlert()
    a.messageText = L("Save changes to “%@”?", name)
    a.informativeText = L("Auto-save is off. Turn it on in Preferences to never see this again.")
    a.addButton(withTitle: L("Save"))
    a.addButton(withTitle: L("Don't Save"))
    if cancel { a.addButton(withTitle: L("Cancel")) }
    return a.runModal()
}

struct Tag: View {
    let text: String
    let color: Color
    var body: some View {
        Text(text).font(.system(size: 9, weight: .heavy)).tracking(0.6)
            .padding(.horizontal, 7).padding(.vertical, 3)
            .foregroundStyle(color)
            .background(Capsule().fill(color.opacity(0.14)))
    }
}

// parity: easy-panel
struct EasyPanel: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Binding var profile: Profile

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Card(title: L("Tone"), icon: "dial.medium") {
                VStack(spacing: 12) {
                    row(L("Bass"), \.bass, L("105 Hz shelf"))
                    row(L("Mids"), \.mids, "1 kHz")
                    row(L("Clarity"), \.clarity, L("3 kHz presence"))
                    row(L("Treble"), \.treble, L("8 kHz shelf"))
                    row(L("Tilt"), \.tilt, L("warmer ↔ brighter"))
                }
            }
            HStack(alignment: .top, spacing: 14) {
                Card(title: L("Headphone correction"), icon: "checkmark.seal") {
                    if let src = profile.correctionSource {
                        Text("\(src.provider): \(src.name)").font(.callout.weight(.semibold))
                        Text(L("%@ filters, applied before your tone", profile.correction.count)).font(.caption).foregroundStyle(.secondary)
                        Button(L("Remove")) { profile.correction = []; profile.correctionSource = nil }.buttonStyle(GlowButtonStyle(prominent: false))
                    } else {
                        Text(L("Import an AutoEQ ParametricEQ.txt to make your headphones sound neutral."))
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        Button(L("Import AutoEQ…")) { c.importFile(asCorrection: true) }.buttonStyle(GlowButtonStyle(prominent: false))
                    }
                }
                Card(title: L("Comfort"), icon: "ear") {
                    HStack {
                        Text(L("Loudness")).frame(width: 70, alignment: .leading)
                        GlowSlider(value: $profile.loudness, range: 0...1, bipolar: false)
                        Text(profile.loudness == 0 ? "off" : "\(Int(profile.loudness * 100))%")
                            .font(.caption.monospacedDigit()).frame(width: 34, alignment: .trailing)
                    }
                    CrossfeedRow(value: $profile.crossfeed)
                    Toggle(L("Clipping protection"), isOn: $profile.limiter).toggleStyle(.switch).controlSize(.small)
                }
            }
        }
    }

    private func row(_ name: String, _ kp: WritableKeyPath<Macros, Double>, _ hint: String) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 0) {
                Text(name).font(.callout.weight(.semibold))
                Text(hint).font(.caption2).foregroundStyle(.secondary)
            }.frame(width: 100, alignment: .leading)
            GlowSlider(value: $profile.macros[dynamicMember: kp], range: -12...12, step: 0.5)
            Text(String(format: "%+.1f dB", profile.macros[keyPath: kp]))
                .font(.callout.monospacedDigit().weight(.medium))
                .contentTransition(.numericText(value: profile.macros[keyPath: kp]))
                .animation(.snappy, value: profile.macros[keyPath: kp])
                .frame(width: 66, alignment: .trailing)
        }
    }
}

/// Headphone crossfeed strength (Easy "Comfort" and Pro "Output").
// parity: crossfeed
struct CrossfeedRow: View {
    @Binding var value: Crossfeed

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(L("Crossfeed"))
            PillPicker(selection: $value, options: Crossfeed.allCases.map { ($0, $0.label) })
            Text(L("Headphones: mixes a little of each side into the other ear, like speakers. Less tiring on long sessions."))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }
}

extension Crossfeed {
    var label: String {
        switch self {
        case .off: L("Off")
        case .light: L("Light")
        case .normal: L("Normal")
        case .strong: L("Strong")
        }
    }
}

/// Press and hold to hear a profile; release to go back to what was playing.
// parity: hold-to-listen
struct HoldToListen: View {
    @EnvironmentObject var theme: ThemeStore
    @Binding var holding: Bool
    @State private var pulse = false

    var body: some View {
        let t = theme.current
        HStack(spacing: 6) {
            Image(systemName: holding ? "ear.fill" : "ear")
                .symbolEffect(.variableColor.iterative, options: .repeating, isActive: holding && theme.motionAllowed)
            Text(holding ? L("Listening…") : L("Hold to listen"))
        }
        .font(.callout.weight(.semibold))
        .foregroundStyle(holding ? .white : t.ink)
        .padding(.horizontal, 14).padding(.vertical, 7)
        .background {
            ZStack {
                if holding {
                    Capsule().fill(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                        .shadow(color: t.accentColor.opacity(0.7), radius: 10)
                    Capsule().stroke(t.accentColor.opacity(pulse ? 0 : 0.7), lineWidth: 2)
                        .scaleEffect(pulse ? 1.25 : 1)
                } else {
                    Capsule().fill(t.ink.opacity(0.08)).overlay(Capsule().strokeBorder(t.ink.opacity(0.12)))
                }
            }
        }
        .scaleEffect(holding ? 0.97 : 1)
        .animation(.spring(response: 0.25, dampingFraction: 0.6), value: holding)
        .contentShape(Capsule())
        .onLongPressGesture(minimumDuration: 3600, maximumDistance: 400, perform: {}, onPressingChanged: { p in
            holding = p
            if p && theme.motionAllowed {
                pulse = false
                withAnimation(.easeOut(duration: 1).repeatForever(autoreverses: false)) { pulse = true }
            } else {
                pulse = false
            }
        })
        .help(L("Hear this profile while you hold the button. Use Now switches to it for real."))
    }
}

/// Small modal text prompt (rename). Returns nil on cancel.
func askName(_ title: String, current: String) -> String? {
    let a = NSAlert()
    a.messageText = title
    let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
    field.stringValue = current
    a.accessoryView = field
    a.addButton(withTitle: L("Rename"))
    a.addButton(withTitle: L("Cancel"))
    a.window.initialFirstResponder = field
    guard a.runModal() == .alertFirstButtonReturn else { return nil }
    let v = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
    return v.isEmpty ? nil : v
}
