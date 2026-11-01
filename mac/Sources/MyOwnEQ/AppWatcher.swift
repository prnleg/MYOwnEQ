import AppKit

/// Tracks running apps and the frontmost app for app rules.
final class AppWatcher {
    struct App: Equatable { let bundleID: String; let name: String; let focused: Bool }

    private var tokens: [NSObjectProtocol] = []
    private let onChange: ([App]) -> Void

    init(onChange: @escaping ([App]) -> Void) {
        self.onChange = onChange
        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification,
                     NSWorkspace.didTerminateApplicationNotification,
                     NSWorkspace.didActivateApplicationNotification] {
            tokens.append(nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.fire()
            })
        }
    }

    func fire() { onChange(Self.snapshot()) }

    static func snapshot() -> [App] {
        let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
        return NSWorkspace.shared.runningApplications.compactMap { app in
            guard let id = app.bundleIdentifier else { return nil }
            return App(bundleID: id, name: app.localizedName ?? id, focused: app.processIdentifier == front)
        }
    }

    /// Regular (Dock) apps, for the rule editor's app picker.
    static func pickableApps() -> [App] {
        var seen = Set<String>()
        return NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular }
            .compactMap { a in a.bundleIdentifier.map { App(bundleID: $0, name: a.localizedName ?? $0, focused: false) } }
            .filter { seen.insert($0.bundleID).inserted }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    deinit { tokens.forEach(NSWorkspace.shared.notificationCenter.removeObserver) }
}
