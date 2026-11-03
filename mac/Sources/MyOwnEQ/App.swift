import AppKit
import SwiftUI

/// Receives `myowneq://` links (SwiftUI's onOpenURL doesn't reach a menu bar app).
final class LinkDelegate: NSObject, NSApplicationDelegate {
    static weak var controller: AppController?
    /// Links that arrive before the controller exists (app launched by a link).
    private static var early: [URL] = []

    func application(_ application: NSApplication, open urls: [URL]) {
        MainActor.assumeIsolated {
            guard let c = Self.controller else { Self.early += urls; return }
            urls.forEach(c.openURL)
        }
    }

    @MainActor static func attach(_ c: AppController) {
        controller = c
        early.forEach(c.openURL)
        early.removeAll()
    }
}

@main
struct MyOwnEQApp: App {
    @NSApplicationDelegateAdaptor(LinkDelegate.self) private var linkDelegate
    @StateObject private var controller: AppController
    @StateObject private var theme: ThemeStore
    @ObservedObject private var lang = Lang.shared

    init() {
        // Build-time helper: `MyOwnEQ --render-icon <wave|finger> <px> <out.png> [fill 0..1]`.
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--render-icon"), args.count > i + 3 {
            let ok = MainActor.assumeIsolated {
                LogoRenderer.renderPNG(style: LogoStyle(rawValue: args[i + 1]) ?? .wave,
                                       size: CGFloat(Double(args[i + 2]) ?? 1024),
                                       to: URL(fileURLWithPath: args[i + 3]),
                                       fill: args.count > i + 4 ? Double(args[i + 4]) ?? 0.8 : 0.8)
            }
            exit(ok ? 0 : 1)
        }
        // Test helper: `MyOwnEQ --check-library <library.json>` decodes it exactly
        // like the app does and exits 0/1 (used by CI with a fixture).
        if let i = args.firstIndex(of: "--check-library"), args.count > i + 1 {
            let d = JSONDecoder()
            d.keyDecodingStrategy = .convertFromSnakeCase
            do {
                let lib = try d.decode(Library.self, from: Data(contentsOf: URL(fileURLWithPath: args[i + 1])))
                print("ok: \(lib.profiles.count) profiles, \(lib.groups.count) groups, \(lib.rules.count) rules")
                exit(0)
            } catch {
                print("FAILED: \(error)")
                exit(1)
            }
        }
        let c = AppController()
        MainActor.assumeIsolated { LinkDelegate.attach(c) }
        _controller = StateObject(wrappedValue: c)
        let ts = MainActor.assumeIsolated { ThemeStore(core: c.core) }
        c.onThemeImported = { [weak ts] id in
            MainActor.assumeIsolated {
                ts?.reload()
                ts?.select(id)
            }
        }
        _theme = StateObject(wrappedValue: ts)
    }

    var body: some Scene {
        MenuBarExtra {
            QuickPanel()
                .environmentObject(controller)
                .environmentObject(theme)
                .environmentObject(lang)
                .id(lang.code)
        } label: {
            Image(nsImage: LogoRenderer.menuBarImage(theme.logo, enabled: controller.state?.enabled != false,
                                                    custom: theme.customLogo, template: theme.customLogoTemplate))
        }
        .menuBarExtraStyle(.window)

        Window("MyOwnEQ", id: "editor") {
            EditorWindow()
                .environmentObject(controller)
                .environmentObject(theme)
                .environmentObject(lang)
                .frame(minWidth: 960, minHeight: 680)
        }
        .defaultSize(width: 1180, height: 780)
    }
}
