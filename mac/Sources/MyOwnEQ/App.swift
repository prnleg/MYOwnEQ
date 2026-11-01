import AppKit
import SwiftUI

@main
struct MyOwnEQApp: App {
    @StateObject private var controller: AppController
    @StateObject private var theme: ThemeStore
    @ObservedObject private var lang = Lang.shared

    init() {
        // Build-time helper: `MyOwnEQ --render-icon <wave|finger> <px> <out.png>`.
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--render-icon"), args.count > i + 3 {
            let ok = MainActor.assumeIsolated {
                LogoRenderer.renderPNG(style: LogoStyle(rawValue: args[i + 1]) ?? .wave,
                                       size: CGFloat(Double(args[i + 2]) ?? 1024),
                                       to: URL(fileURLWithPath: args[i + 3]))
            }
            exit(ok ? 0 : 1)
        }
        _controller = StateObject(wrappedValue: AppController())
        _theme = StateObject(wrappedValue: ThemeStore())
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
