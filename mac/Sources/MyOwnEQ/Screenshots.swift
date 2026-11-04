import AppKit
import SwiftUI

/// Dev helper for the website and README: renders the real UI into PNGs.
///
///     MYOWNEQ_DATA_DIR=$(mktemp -d) MyOwnEQ --screenshots <out dir> [<images dir>]
///
/// Uses a throwaway data folder (set MYOWNEQ_DATA_DIR), no audio, no updates. With an
/// images folder holding `voxel-dusk.jpg` / `synthwave-drive.jpg`, two extra themes
/// with background images are created. The user's UI preferences are restored at the end.
@MainActor
enum Screenshots {
    private struct Shot {
        let name: String
        let theme: String
        var pro = false
        var nav: Nav? = nil   // nil = the demo profile
        var panel = false
    }

    static func run(out: URL, images: URL?, c: AppController, theme: ThemeStore) {
        guard ProcessInfo.processInfo.environment["MYOWNEQ_DATA_DIR"] != nil else {
            print("refusing: set MYOWNEQ_DATA_DIR to a throwaway folder")
            exit(2)
        }
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let d = UserDefaults.standard
        let keep = ["theme", "editorPro", "showPresets", "motion", "reactive", "shimmer"].map { ($0, d.object(forKey: $0)) }
        d.set(false, forKey: "reactive")

        var themes = ["neon-sunset", "cyber-lagoon", "blood-moon", "acid-toxic", "peach-daylight"]
        if let images {
            if let id = makeTheme(theme, images.appendingPathComponent("voxel-dusk.jpg"), Theme(
                id: "", name: "Voxel Dusk", author: "MyOwnEQ", dark: true, accent: "#7BD85A",
                curve: ["#5DBB4A", "#FFE08A", "#FF9A5A"], spectrum: ["#FFE08A", "#3A6FD8"],
                background: .init(base: "#2B1B5A", blobs: ["#FF9A5A", "#5DBB4A"], intensity: 0.18, speed: 0.6,
                                  image: nil, imageOpacity: 0.85, imageBlur: 3),
                panel: "#241A45", glow: 0.9)) { themes.append(id) }
            if let id = makeTheme(theme, images.appendingPathComponent("synthwave-drive.jpg"), Theme(
                id: "", name: "Outrun", author: "MyOwnEQ", dark: true, accent: "#FF2E88",
                curve: ["#00E5FF", "#FF2E88", "#FFD23F"], spectrum: ["#FF2E88", "#00E5FF"],
                background: .init(base: "#12052E", blobs: ["#FF2E88", "#00E5FF"], intensity: 0.15, speed: 0.6,
                                  image: nil, imageOpacity: 0.8, imageBlur: 2),
                panel: "#1B0F33", glow: 1)) { themes.append(id) }
        }

        // A preset with a recognizable curve, playing on the current output.
        let profiles = c.state?.library.profiles ?? []
        let demo = profiles.first { $0.name == "Bass Boost" } ?? profiles.first { $0.name == "FPS" } ?? profiles.first
        if let demo { c.select(demo.id) }

        var shots: [Shot] = themes.map { Shot(name: "editor-easy-\($0)", theme: $0) }
        for t in themes where ["neon-sunset", "cyber-lagoon"].contains(t) || t.hasPrefix("user-") {
            shots.append(Shot(name: "editor-pro-\(t)", theme: t, pro: true))
        }
        if let last = themes.last {
            shots.append(Shot(name: "preferences-\(last)", theme: last, nav: .appearance))
            shots.append(Shot(name: "devices-\(themes[0])", theme: themes[0], nav: .devices))
        }
        for t in [themes[0], themes.last ?? themes[0]] { shots.append(Shot(name: "panel-\(t)", theme: t, panel: true)) }

        let names = Dictionary(uniqueKeysWithValues: themes.map { id in
            (id, theme.themes.first { $0.id == id }?.name.lowercased().replacingOccurrences(of: " ", with: "-") ?? id)
        })

        var windows: [NSWindow] = []
        func host<V: View>(_ v: V, _ size: CGSize) -> NSWindow {
            let w = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.titled, .fullSizeContentView],
                             backing: .buffered, defer: false)
            w.isReleasedWhenClosed = false
            w.titlebarAppearsTransparent = true
            w.titleVisibility = .hidden
            w.contentView = NSHostingView(rootView: v.environmentObject(c).environmentObject(theme).environmentObject(Lang.shared))
            w.setContentSize(size)
            w.center()
            w.orderFrontRegardless()
            windows.append(w)
            return w
        }

        var i = 0
        func next() {
            guard i < shots.count else {
                windows.forEach { $0.close() }
                for (k, v) in keep { if let v { d.set(v, forKey: k) } else { d.removeObject(forKey: k) } }
                print("screenshots: \(shots.count) in \(out.path)")
                exit(0)
            }
            let s = shots[i]
            i += 1
            theme.select(s.theme)
            d.set(s.pro, forKey: "editorPro")
            windows.forEach { $0.close() }
            windows.removeAll()
            let w = s.panel
                ? host(QuickPanel().frame(width: 380), CGSize(width: 380, height: 640))
                : host(EditorWindow(initialNav: s.nav ?? demo.map { .profile($0.id) }), CGSize(width: 1280, height: 800))
            // Let layout, springs and the background settle.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) {
                let file = s.name.replacingOccurrences(of: s.theme, with: names[s.theme] ?? s.theme)
                capture(w, out.appendingPathComponent("\(file).png"))
                next()
            }
        }
        next()
    }

    private static func makeTheme(_ store: ThemeStore, _ image: URL, _ t: Theme) -> String? {
        guard FileManager.default.fileExists(atPath: image.path), let file = store.importBackgroundImage(image) else { return nil }
        var t = t
        t.background.image = file
        return store.save(t)
    }

    private static func capture(_ w: NSWindow, _ url: URL) {
        guard let v = w.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        v.cacheDisplay(in: v.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}
