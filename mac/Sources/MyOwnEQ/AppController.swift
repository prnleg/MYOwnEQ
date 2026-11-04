import AppKit
import Combine
import Foundation
import OSLog

@MainActor
final class AppController: ObservableObject {
    @Published private(set) var state: CoreState?
    @Published private(set) var devices: [OutputDevice] = []
    @Published private(set) var current: OutputDevice?
    @Published private(set) var engineError: String?
    @Published private(set) var engineRunning = false
    /// Toast message; clears itself after a few seconds (see `notify`).
    @Published private(set) var notice: Notice?
    private var noticeTask: Task<Void, Never>?
    /// Shared constants from the core (graphic frequencies, knob spots…).
    private(set) var constants: UiConstants?
    /// Main volume of the current output (0…1), nil when it has none.
    @Published private(set) var volume: Double?
    @Published private(set) var volumeSettable = false

    let core: CoreBridge
    private let engine = TapEngine()
    private var deviceWatcher: DeviceWatcher?
    private var appWatcher: AppWatcher?
    private var lastApps: [AppWatcher.App] = []
    private var saveTask: Task<Void, Never>?
    private var syncTimer: Timer?
    private var updateTimer: Timer?
    @Published private(set) var update: UpdateInfo?
    @Published private(set) var discovered: [FoundHost] = []
    @Published private(set) var discovering = false

    init() {
        core = CoreBridge(dataDir: CoreBridge.dataDir, machineID: CoreBridge.machineID())
        Lang.shared.attach(core)
        core.command("ui_constants")
        constants = core.decodeLast(UiConstants.self)
        // Who we are for the other machines in sync ("connected machines" list).
        core.command("set_app_info", ["name": Host.current().localizedName ?? ProcessInfo.processInfo.hostName,
                                      "platform": "mac", "version": Self.appVersion])
        reload()
        deviceWatcher = DeviceWatcher { [weak self] in
            Task { @MainActor in self?.devicesChanged() }
        }
        appWatcher = AppWatcher { [weak self] apps in
            Task { @MainActor in self?.appsChanged(apps) }
        }
        devicesChanged()
        appWatcher?.fire()
        // Updates: configure, and if a verified update is already waiting from
        // the last session, this launch is the "restart": install it now.
        // Dev builds (build-app.sh --native/--run) never self-update: they share
        // the data folder with the installed app and would install its update.
        if Self.devBuild == nil, !Self.screenshotMode {
            updateReply(core.command("update_configure", ["version": Self.appVersion, "platform": "mac"]))
            if update?.isReady == true, update?.auto != false {
                installUpdateAndQuit()
                return
            }
            updateTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.updateReply(self?.core.command("update_tick") ?? [:]) }
            }
        }
        // Poll sync a few times per second (cheap no-op when sync is off).
        syncTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.syncTick()
                self?.controlTick()
            }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.engine.stop()
                // "Install on restart": quitting with a ready update installs it.
                if Self.devBuild == nil, self.update?.isReady == true, self.update?.auto != false {
                    self.core.command("update_apply", ["app_path": Bundle.main.bundlePath])
                }
            }
        }
    }

    // MARK: - core plumbing

    private func apply(_ reply: [String: Any]) {
        if reply["ok"] as? Bool == false {
            notify(reply["error"] as? String ?? "?", error: true)
            return
        }
        if reply["library"] != nil {
            if let s = core.decodeLast(CoreState.self) {
                state = s
            } else {
                // Never fail silently: an empty UI here looks like lost data.
                notify(L("Couldn't read the profile library (%@). Please report this bug.", core.lastDecodeError ?? "?"), error: true)
            }
        }
    }

    func reload() { apply(core.command("get_state")) }

    // MARK: - devices & engine

    private func devicesChanged() {
        devices = CA.outputDevices()
        core.command("set_connected", ["devices": devices.map(Self.deviceJSON)])
        let newCurrent = CA.defaultOutputDevice().flatMap(CA.device)
        let rateChanged = newCurrent.flatMap { CA.nominalSampleRate($0.id) }.map { abs($0 - engine.sampleRate) > 0.5 } ?? false
        if newCurrent?.uid != current?.uid || (engine.isRunning && rateChanged) {
            current = newCurrent
            deviceWatcher?.watchDevice(newCurrent?.id)
            apply(core.command("set_device", ["device": newCurrent.map(Self.deviceJSON) as Any]))
            restartEngine()
        }
        let vol = current.flatMap { CA.volume($0.id) }
        if let vol { core.command("set_volume", ["volume": vol]) }
        if volume != vol { volume = vol }
        let settable = current.map { CA.volumeSettable($0.id) } ?? false
        if volumeSettable != settable { volumeSettable = settable }
        reload()
    }

    private static func deviceJSON(_ d: OutputDevice) -> [String: Any] {
        ["uid": d.uid, "name": d.name, "kind": d.kind.rawValue]
    }

    /// `--screenshots`: render the UI to PNGs (Screenshots.swift). No audio, no updates.
    static let screenshotMode = CommandLine.arguments.contains("--screenshots")

    func restartEngine() {
        engine.stop()
        engineRunning = false
        if Self.screenshotMode {
            engineRunning = true // looks like a normal running app; nothing is captured
            return
        }
        guard state?.enabled ?? true, let dev = current else {
            engineError = nil
            return
        }
        do {
            try engine.start(device: dev, core: core)
            engineRunning = true
            engineError = nil
        } catch {
            engineError = error.localizedDescription
            NSLog("MyOwnEQ engine: \(error.localizedDescription)")
        }
    }

    private func appsChanged(_ apps: [AppWatcher.App]) {
        guard apps != lastApps else { return }
        lastApps = apps
        apply(core.command("set_apps", ["apps": apps.map { ["id": $0.bundleID, "focused": $0.focused] }]))
    }

    // MARK: - user actions

    /// Change the system volume of the current output.
    func setVolume(_ v: Double) {
        guard let dev = current, CA.setVolume(dev.id, v) else { return }
        volume = v
        core.command("set_volume", ["volume": v])
    }

    /// Set the current device's boost (dB). Saved and synced with linked devices.
    func setBoost(_ db: Double) {
        guard var g = state?.currentGroup else { return }
        g.boostDb = min(max(db, 0), 12)
        updateGroup(g)
    }

    /// Rename a profile (presets can't be renamed).
    func rename(_ id: ID, to name: String) {
        let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !n.isEmpty, var p = state?.profile(id), !p.isBuiltin else { return }
        p.name = n
        save(p)
    }

    func setEnabled(_ on: Bool) {
        apply(core.command("set_enabled", ["enabled": on]))
        restartEngine()
    }

    func select(_ profile: ID) {
        guard let g = state?.group else { return }
        apply(core.command("select", ["group": g, "profile": profile]))
    }

    /// Save a profile. Debounced so slider drags don't hit the disk every tick.
    func save(_ profile: Profile, debounce: Bool = false) {
        saveTask?.cancel()
        guard debounce else {
            pendingSave = nil
            apply(core.command("upsert_profile", ["profile": profile]))
            return
        }
        pendingSave = profile
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled, let self, let p = self.pendingSave else { return }
            self.pendingSave = nil
            self.apply(self.core.command("upsert_profile", ["profile": p]))
        }
    }

    /// A debounced save not written yet.
    private(set) var pendingSave: Profile?

    /// Write a pending debounced save now (e.g. when the editor closes).
    func flushSave() {
        if let p = pendingSave { save(p) }
    }

    func delete(_ profile: ID) { apply(core.command("delete_profile", ["id": profile])) }

    /// Live-audition an unsaved profile (`nil` to stop).
    func preview(_ profile: Profile?) {
        core.command("preview", ["profile": profile as Any])
    }

    func updateGroup(_ g: DeviceGroup) { apply(core.command("update_group", ["group": g])) }

    func toggleFavorite(_ profile: ID) {
        guard var g = state?.currentGroup else { return }
        if let i = g.favorites.firstIndex(of: profile) { g.favorites.remove(at: i) } else { g.favorites.append(profile) }
        updateGroup(g)
    }

    func upsertRule(_ r: Rule) { apply(core.command("upsert_rule", ["rule": r])) }
    func deleteRule(_ id: ID) { apply(core.command("delete_rule", ["id": id])) }

    // parity: eqmac-import
    func importEqMac() {
        let r = core.command("import_eqmac")
        apply(r)
        if let n = r["imported"] as? Int { notify(L("Imported %@ eqMac presets", n)) }
    }

    // parity: imports
    func importFile(asCorrection: Bool) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .text]
        panel.allowsMultipleSelection = true
        panel.message = asCorrection ? L("Choose AutoEQ ParametricEQ.txt files") : L("Choose Equalizer APO / Peace config files")
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            var name = url.deletingPathExtension().lastPathComponent
            if asCorrection, name.hasSuffix(" ParametricEQ") { name = String(name.dropLast(" ParametricEQ".count)) }
            let r = core.command("import_apo", ["text": text, "name": name, "as_correction": asCorrection])
            apply(r)
            if let w = r["warnings"] as? [String], !w.isEmpty { notify(L("%@: %@ lines skipped", name, w.count)) }
        }
    }

    // MARK: - AutoEQ headphone search
    // parity: autoeq-search

    func autoEqSearch(query: String, device: String? = nil) -> AutoEqSearchResult {
        var args: [String: Any] = ["query": query]
        if let device { args["device"] = device }
        let r = core.command("autoeq_search", args)
        let index = r["index"] as? [String: Any] ?? [:]
        let models = (r["results"] as? [[String: Any]] ?? []).map { m in
            AutoEqModel(
                name: m["name"] as? String ?? "?",
                kind: m["kind"] as? String ?? "",
                measurements: (m["measurements"] as? [[String: Any]] ?? []).map {
                    AutoEqMeasurement(label: $0["label"] as? String ?? "", path: $0["path"] as? String ?? "")
                })
        }
        return AutoEqSearchResult(
            query: r["query"] as? String ?? query,
            state: index["state"] as? String ?? "error",
            count: index["count"] as? Int ?? 0,
            error: index["error"] as? String ?? (r["ok"] as? Bool == false ? r["error"] as? String : nil),
            results: models)
    }

    func autoEqRefresh() { _ = core.command("autoeq_refresh") }

    func autoEqFetch(path: String, create: Bool) -> AutoEqFetchResult {
        let r = core.command("autoeq_fetch", ["path": path, "create": create])
        switch r["state"] as? String {
        case "loading": return .loading
        case "ready":
            if create, let id = r["imported_id"] as? String {
                apply(r)
                return .created(id)
            }
            guard let bands = decode([Band].self, r["correction"]),
                  let source = decode(CorrectionSource.self, r["source"])
            else { return .failed("bad reply") }
            return .ready(bands, source)
        default:
            return .failed(r["error"] as? String ?? "?")
        }
    }

    // MARK: - control channel (CLI, links, other apps)
    // parity: control

    /// Run requests from the command line / other apps (queued by the core).
    private func controlTick() {
        guard state?.control?.running == true else { return }
        let r = core.command("control_tick")
        if r["changed"] as? Bool == true { reload() }
        if let id = r["theme_id"] as? String { onThemeImported?(id) }
    }

    func setControl(_ on: Bool) { apply(core.command("control_configure", ["enabled": on])) }

    /// `myowneq://select/Gaming`, `myowneq://toggle`, `myowneq://import/MEQ1-…`.
    // parity: links
    func openURL(_ url: URL) {
        let r = core.command("open_url", ["url": url.absoluteString])
        if r["ok"] as? Bool == false {
            notify(L("Couldn't open the link: %@", r["error"] as? String ?? "?"), error: true)
            return
        }
        apply(r)
        let reply = r["control"] as? [String: Any] ?? [:]
        if let id = reply["theme_id"] as? String {
            onThemeImported?(id)
            notify(L("Theme “%@” added", reply["theme"] as? String ?? ""))
        } else if let name = reply["profile"] as? String, reply["imported_id"] != nil {
            notify(L("Imported “%@” from a share code", name))
        } else if let name = reply["profile"] as? String {
            notify(L("Now playing: %@", name))
        } else if let on = reply["enabled"] as? Bool {
            notify(on ? L("EQ on") : L("EQ off"))
        }
    }

    /// A theme code was imported (clipboard, link, command line): reload + select it.
    var onThemeImported: ((String) -> Void)?

    // MARK: - share codes
    // parity: share-codes

    /// Code + QR for a profile (nil if the core refused).
    func share(_ profile: Profile) -> ShareInfo? {
        let r = core.command("share_code", ["profile": profile.id])
        guard let code = r["code"] as? String else { apply(r); return nil }
        return ShareInfo(name: profile.name, code: code, qr: r["qr"] as? [String] ?? [])
    }

    /// Import the first MEQ1-… code on the clipboard. Returns the new profile id.
    @discardableResult
    func importCodeFromClipboard() -> ID? {
        let text = NSPasteboard.general.string(forType: .string) ?? ""
        let r = core.command("import_code", ["text": text])
        if let id = r["theme_id"] as? String {
            onThemeImported?(id)
            notify(L("Theme “%@” added", r["theme_name"] as? String ?? ""))
            return nil
        }
        switch r["reason"] as? String {
        case "not_a_code": notify(L("No MyOwnEQ share code on the clipboard. Copy one (MEQ1-… or MEQT-…) and try again."), error: true); return nil
        case "damaged": notify(L("That share code is damaged or incomplete. Copy it again."), error: true); return nil
        case "newer": notify(L("That share code is from a newer MyOwnEQ. Update the app."), error: true); return nil
        default: apply(r)
        }
        guard let id = r["imported_id"] as? String else { return nil }
        let name = state?.library.profiles.first { $0.id == id }?.name ?? ""
        notify(L("Imported “%@” from a share code", name))
        return id
    }

    func exportAPO(_ profile: ID) -> String? {
        core.command("export_apo", ["profile": profile])["text"] as? String
    }

    func curve(for profile: Profile, points: Int = 256) -> Curve? {
        core.command("curve", ["profile": profile, "points": points])
        return core.decodeLast(Curve.self)
    }

    private var spectrumBuf = [Float](repeating: -110, count: 160)
    func spectrum() -> [Float]? {
        core.spectrum(into: &spectrumBuf) ? spectrumBuf : nil
    }

    // MARK: - sync

    private func syncTick() {
        guard let s = state, s.sync.mode != "off" else { return }
        let r = core.command("sync_tick")
        if r["library"] != nil {
            apply(r) // remote change landed: full state
        } else if let info = r["sync"], let d = try? JSONSerialization.data(withJSONObject: info) {
            let dec = JSONDecoder()
            dec.keyDecodingStrategy = .convertFromSnakeCase
            if let si = try? dec.decode(SyncInfo.self, from: d), si != s.sync { state?.sync = si }
        }
    }

    func configureSync(mode: String, url: String = "", token: String = "", port: Int = 7878) {
        var cfg: [String: Any] = ["mode": mode]
        switch mode {
        case "server": cfg["url"] = url.trimmingCharacters(in: .whitespaces); cfg["token"] = token.trimmingCharacters(in: .whitespaces)
        case "host": cfg["port"] = port; cfg["token"] = token // empty: the core makes a pairing code
        default: break
        }
        apply(core.command("sync_configure", ["config": cfg]))
    }

    func discover() {
        discovering = true
        Task.detached {
            let found = CoreBridge.discover()
            await MainActor.run {
                self.discovered = found
                self.discovering = false
            }
        }
    }

    /// Link the current device with the same headphones on another machine; says so.
    // parity: link-feedback
    func link(_ other: ID) {
        guard let g = state?.group, let o = state?.group(other) else { return }
        let who = "\(o.name) · \(machineName(o.members.first?.key.machine ?? ""))"
        let r = core.command("link_groups", ["into": g, "other": other])
        apply(r)
        if r["ok"] as? Bool != false { notify(L("Linked with %@. Profile and favorites now follow on both.", who)) }
    }

    func unlink(_ key: DeviceKey) {
        let name = state?.currentGroup?.members.first { $0.key == key }?.osName ?? key.uid
        let r = core.command("unlink_device", ["key": ["machine": key.machine, "uid": key.uid]])
        apply(r)
        if r["ok"] as? Bool != false { notify(L("Unlinked %@.", "\(name) · \(machineName(key.machine))")) }
    }

    // MARK: - updates

    static var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    /// "dev 01/11 13:05:22" for local test builds, nil for releases.
    static var devBuild: String? {
        Bundle.main.object(forInfoDictionaryKey: "MyOwnEQBuild") as? String
    }

    /// Version as shown in the UI, with the build time on dev builds.
    static var displayVersion: String {
        devBuild.map { "\(appVersion) (\($0))" } ?? appVersion
    }

    private func updateReply(_ r: [String: Any]) {
        guard let u = r["update"], let d = try? JSONSerialization.data(withJSONObject: u) else { return }
        let dec = JSONDecoder()
        dec.keyDecodingStrategy = .convertFromSnakeCase
        if let info = try? dec.decode(UpdateInfo.self, from: d), info != update { update = info }
    }

    /// Check now: the core answers "checking" right away; poll quickly while it
    /// works so the spinner is live, then say how it went.
    func checkForUpdates() {
        guard Self.devBuild == nil else {
            notify(L("Test build: updates are off. Install a release to get updates."))
            return
        }
        updateReply(core.command("update_check"))
        Task { @MainActor [weak self] in
            for _ in 0..<600 { // up to 3 min (downloads)
                try? await Task.sleep(for: .milliseconds(300))
                guard let self else { return }
                self.updateReply(self.core.command("update_tick"))
                guard let u = self.update, u.isBusy else { break }
            }
            guard let self, let u = self.update else { return }
            switch u.state {
            case "up_to_date": self.notify(L("Up to date"))
            case "ready": self.notify(L("Version %@ is ready. Restart to install.", u.version ?? ""))
            case "error": self.notify(L("Update check failed: %@", u.message ?? ""), error: true)
            default: break
            }
        }
    }

    func setAutoUpdate(_ on: Bool) { updateReply(core.command("update_set_auto", ["auto": on])) }

    /// Launch the swap script and quit; the script relaunches the new version.
    func installUpdateAndQuit() {
        engine.stop()
        let r = core.command("update_apply", ["app_path": Bundle.main.bundlePath])
        if r["ok"] as? Bool == true {
            update = nil // don't apply twice in willTerminate
            NSApp.terminate(nil)
        } else {
            notify(r["error"] as? String ?? "?", error: true)
            restartEngine()
        }
    }

    // MARK: - derived UI info

    var activeProfile: Profile? { state?.activeProfile }

    var reason: String {
        guard let s = state else { return "" }
        switch s.resolution.kind {
        case "rule", "rule_override":
            let app = s.rule(s.resolution.rule)?.app.name ?? L("app")
            return s.resolution.kind == "rule" ? L("because %@ is running", app) : L("manual pick while %@ runs", app)
        case "selected": return L("selected")
        case "default": return L("device default")
        default: return L("no profile (flat)")
        }
    }

    /// Show a toast; it clears itself (errors stay longer).
    func notify(_ text: String, error: Bool = false) {
        let n = Notice(text: text, isError: error)
        notice = n
        noticeTask?.cancel()
        noticeTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(error ? 12 : 6))
            guard !Task.isCancelled, self?.notice?.id == n.id else { return }
            self?.notice = nil
        }
    }

    func dismissNotice() { notice = nil }

    // MARK: - shared editing logic (in the core, same for every app)

    /// Edited preset → new profile "Name (mine)" (presets are read-only). Returns it.
    func forkPreset(_ draft: Profile, originalName: String) -> Profile? {
        let r = core.command("fork_preset", ["draft": draft, "name": L("%@ (mine)", originalName)])
        apply(r)
        guard let p = r["profile"], let d = try? JSONSerialization.data(withJSONObject: p) else { return nil }
        let dec = JSONDecoder()
        dec.keyDecodingStrategy = .convertFromSnakeCase
        return try? dec.decode(Profile.self, from: d)
    }

    /// Switch a profile's EQ layout keeping the sound ("parametric" | "graphic10" | "graphic31").
    func convertEq(_ eq: EqLayer, to layout: String, for profile: ID) -> EqLayer? {
        let r = core.command("convert_eq", ["profile": profile, "eq": eq, "to": layout])
        return decode(EqLayer.self, r["eq"])
    }

    /// Where a new band should go (nil when it doesn't fit).
    func suggestBand(_ eq: EqLayer) -> Band? {
        decode(Band.self, core.command("suggest_band", ["eq": eq])["band"])
    }

    private func decode<T: Decodable>(_ t: T.Type, _ v: Any?) -> T? {
        guard let v, !(v is NSNull), let d = try? JSONSerialization.data(withJSONObject: v, options: .fragmentsAllowed) else { return nil }
        let dec = JSONDecoder()
        dec.keyDecodingStrategy = .convertFromSnakeCase
        return try? dec.decode(T.self, from: d)
    }

    /// Friendly name of a machine id (from the sync's machine list).
    func machineName(_ id: String) -> String {
        if id == state?.machine { return Host.current().localizedName ?? id }
        return state?.sync.peers?.first { $0.machine == id }?.displayName ?? id
    }

    // MARK: - diagnostics

    /// Report for bug reports: the core's shared part + Mac sections (system, audio
    /// tap, recent log lines). The user name is masked; sync secrets never leave the core.
    // parity: diagnostics
    func diagnostics() -> String {
        let user = NSUserName()
        func mask(_ s: String) -> String { user.isEmpty ? s : s.replacingOccurrences(of: user, with: "<user>") }
        let os = ProcessInfo.processInfo.operatingSystemVersionString
        let sys = [
            "os: macOS \(os)",
            "language: \(Locale.preferredLanguages.first ?? "?")",
        ]
        var audio = [
            "tap running: \(engineRunning)\(engine.isRunning ? " @ \(Int(engine.sampleRate)) Hz" : "")",
        ]
        if let e = engineError { audio.append("tap error: \(mask(e))") }
        for d in devices {
            audio.append("output: \(d.name) [\(d.kind.rawValue)]\(d.uid == current?.uid ? " (default)" : "")")
        }
        var log: [String] = []
        if let store = try? OSLogStore(scope: .currentProcessIdentifier) {
            let since = store.position(date: Date().addingTimeInterval(-6 * 3600))
            let entries = (try? store.getEntries(at: since)) ?? AnySequence([])
            log = entries.compactMap { $0 as? OSLogEntryLog }
                .filter { $0.composedMessage.contains("MyOwnEQ") }
                .suffix(40)
                .map { mask("\($0.date.formatted(date: .omitted, time: .standard)) \($0.composedMessage)") }
        }
        let sections: [[String: Any]] = [
            ["title": "mac", "lines": sys],
            ["title": "audio tap", "lines": audio],
            ["title": "log (last 40 lines)", "lines": log],
        ]
        return core.command("diagnostics", ["sections": sections])["text"] as? String ?? ""
    }

    // parity: audio-permission
    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture") {
            NSWorkspace.shared.open(url)
        }
    }
}
