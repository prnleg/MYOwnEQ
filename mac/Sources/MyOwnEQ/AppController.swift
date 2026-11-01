import AppKit
import Combine
import Foundation

@MainActor
final class AppController: ObservableObject {
    @Published private(set) var state: CoreState?
    @Published private(set) var devices: [OutputDevice] = []
    @Published private(set) var current: OutputDevice?
    @Published private(set) var engineError: String?
    @Published private(set) var engineRunning = false
    @Published var lastMessage: String?

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
        updateReply(core.command("update_configure", ["version": Self.appVersion, "platform": "mac"]))
        if update?.isReady == true, update?.auto != false {
            installUpdateAndQuit()
            return
        }
        updateTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.updateReply(self?.core.command("update_tick") ?? [:]) }
        }
        // Poll sync a few times per second (cheap no-op when sync is off).
        syncTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.syncTick() }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.engine.stop()
                // "Install on restart": quitting with a ready update installs it.
                if self.update?.isReady == true, self.update?.auto != false {
                    self.core.command("update_apply", ["app_path": Bundle.main.bundlePath])
                }
            }
        }
    }

    // MARK: - core plumbing

    private func apply(_ reply: [String: Any]) {
        if reply["ok"] as? Bool == false {
            lastMessage = reply["error"] as? String
            return
        }
        if reply["library"] != nil, let s = core.decodeLast(CoreState.self) {
            state = s
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
        if let dev = current, let vol = CA.volume(dev.id) {
            core.command("set_volume", ["volume": vol])
        }
        reload()
    }

    private static func deviceJSON(_ d: OutputDevice) -> [String: Any] {
        ["uid": d.uid, "name": d.name, "kind": d.kind.rawValue]
    }

    func restartEngine() {
        engine.stop()
        engineRunning = false
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
            apply(core.command("upsert_profile", ["profile": profile]))
            return
        }
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            self?.apply(self?.core.command("upsert_profile", ["profile": profile]) ?? [:])
        }
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

    func importEqMac() {
        let r = core.command("import_eqmac")
        apply(r)
        if let n = r["imported"] as? Int { lastMessage = "Imported \(n) eqMac preset\(n == 1 ? "" : "s")" }
    }

    func importFile(asCorrection: Bool) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .text]
        panel.allowsMultipleSelection = true
        panel.message = asCorrection ? "Choose AutoEQ ParametricEQ.txt files" : "Choose Equalizer APO / Peace config files"
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            var name = url.deletingPathExtension().lastPathComponent
            if asCorrection, name.hasSuffix(" ParametricEQ") { name = String(name.dropLast(" ParametricEQ".count)) }
            let r = core.command("import_apo", ["text": text, "name": name, "as_correction": asCorrection])
            apply(r)
            if let w = r["warnings"] as? [String], !w.isEmpty { lastMessage = "\(name): \(w.count) line(s) skipped" }
        }
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
        case "host": cfg["port"] = port; cfg["token"] = token.isEmpty ? Self.pairingCode() : token
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

    static func pairingCode() -> String {
        let alphabet = Array("23456789ABCDEFGHJKLMNPQRSTUVWXYZ")
        let chars = (0..<8).map { _ in alphabet[Int.random(in: 0..<alphabet.count)] }
        return String(chars[0..<4]) + "-" + String(chars[4..<8])
    }

    func link(_ other: ID) {
        guard let g = state?.group else { return }
        apply(core.command("link_groups", ["into": g, "other": other]))
    }

    func unlink(_ key: DeviceKey) {
        apply(core.command("unlink_device", ["key": ["machine": key.machine, "uid": key.uid]]))
    }

    // MARK: - updates

    static var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    private func updateReply(_ r: [String: Any]) {
        guard let u = r["update"], let d = try? JSONSerialization.data(withJSONObject: u) else { return }
        let dec = JSONDecoder()
        dec.keyDecodingStrategy = .convertFromSnakeCase
        if let info = try? dec.decode(UpdateInfo.self, from: d), info != update { update = info }
    }

    func checkForUpdates() {
        updateReply(core.command("update_check"))
        // Status flips to ready asynchronously; poll briefly.
        for delay in [2.0, 6.0, 15.0, 40.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.updateReply(self?.core.command("update_tick") ?? [:])
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
            lastMessage = r["error"] as? String
            restartEngine()
        }
    }

    // MARK: - derived UI info

    var activeProfile: Profile? { state?.activeProfile }

    var reason: String {
        guard let s = state else { return "" }
        switch s.resolution.kind {
        case "rule", "rule_override":
            let app = s.rule(s.resolution.rule)?.app.name ?? "app"
            return s.resolution.kind == "rule" ? "because \(app) is running" : "manual pick while \(app) runs"
        case "selected": return "selected"
        case "default": return "device default"
        default: return "no profile (flat)"
        }
    }

    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture") {
            NSWorkspace.shared.open(url)
        }
    }
}
