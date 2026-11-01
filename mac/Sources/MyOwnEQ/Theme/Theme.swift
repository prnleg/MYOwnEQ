import AppKit
import UniformTypeIdentifiers
import SwiftUI

/// A color theme, loaded from `themes/*.json` (bundled) or the user's
/// `~/Library/Application Support/MyOwnEQ/Themes`. See themes/README.md.
struct Theme: Codable, Identifiable, Equatable {
    struct Background: Codable, Equatable {
        var base: String
        var blobs: [String]
        var intensity: Double = 0.4
        var speed: Double = 1
    }

    var id: String
    var name: String
    var author: String?
    var dark: Bool
    var accent: String
    var curve: [String]
    var spectrum: [String]
    var background: Background
    var panel: String
    var glow: Double = 0.8

    // MARK: resolved colors

    var accentColor: Color { Color(hex: accent) }
    var curveColors: [Color] { curve.count >= 2 ? curve.map { Color(hex: $0) } : [accentColor, accentColor] }
    var curveGradient: Gradient { Gradient(colors: curveColors) }
    var spectrumColors: [Color] { spectrum.count >= 2 ? spectrum.map { Color(hex: $0) } : [accentColor, accentColor] }
    var baseColor: Color { Color(hex: background.base) }
    var blobColors: [Color] { background.blobs.map { Color(hex: $0) } }
    var panelColor: Color { Color(hex: panel) }
    var ink: Color { dark ? .white : Color(hex: "#1B1423") }
    var colorScheme: ColorScheme { dark ? .dark : .light }

    /// Color along the curve gradient at `t` (0 = lows, 1 = highs).
    func curveColor(at t: Double) -> Color {
        let cs = curve.map { NSColor(hex: $0) }
        guard cs.count > 1 else { return accentColor }
        let x = min(max(t, 0), 1) * Double(cs.count - 1)
        let i = min(Int(x), cs.count - 2)
        return Color(nsColor: cs[i].blended(withFraction: x - Double(i), of: cs[i + 1]) ?? cs[i])
    }

    static let fallback = Theme(
        id: "neon-sunset", name: "Neon Sunset", author: "MyOwnEQ", dark: true, accent: "#FF2E88",
        curve: ["#FF2E88", "#FF7A2E", "#FFD23F"], spectrum: ["#FF2E88", "#7B2CBF"],
        background: .init(base: "#0E0B1A", blobs: ["#FF2E88", "#7B2CBF", "#FF7A2E", "#3A0CA3"], intensity: 0.45, speed: 1),
        panel: "#1A1430", glow: 0.9)

    private enum CodingKeys: String, CodingKey { case id, name, author, dark, accent, curve, spectrum, background, panel, glow }
    init(id: String, name: String, author: String?, dark: Bool, accent: String, curve: [String], spectrum: [String],
         background: Background, panel: String, glow: Double) {
        (self.id, self.name, self.author, self.dark, self.accent) = (id, name, author, dark, accent)
        (self.curve, self.spectrum, self.background, self.panel, self.glow) = (curve, spectrum, background, panel, glow)
    }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? id
        author = try c.decodeIfPresent(String.self, forKey: .author)
        dark = try c.decodeIfPresent(Bool.self, forKey: .dark) ?? true
        accent = try c.decode(String.self, forKey: .accent)
        curve = try c.decodeIfPresent([String].self, forKey: .curve) ?? [accent, accent]
        spectrum = try c.decodeIfPresent([String].self, forKey: .spectrum) ?? curve
        background = try c.decode(Background.self, forKey: .background)
        panel = try c.decodeIfPresent(String.self, forKey: .panel) ?? background.base
        glow = try c.decodeIfPresent(Double.self, forKey: .glow) ?? 0.8
    }
}

extension Theme.Background {
    private enum K: String, CodingKey { case base, blobs, intensity, speed }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        base = try c.decode(String.self, forKey: .base)
        blobs = try c.decodeIfPresent([String].self, forKey: .blobs) ?? []
        intensity = try c.decodeIfPresent(Double.self, forKey: .intensity) ?? 0.4
        speed = try c.decodeIfPresent(Double.self, forKey: .speed) ?? 1
    }
}

enum LogoStyle: String, CaseIterable, Identifiable {
    case wave, finger, custom
    var id: String { rawValue }
    var label: String {
        switch self {
        case .wave: L("Wave")
        case .finger: L("The Finger")
        case .custom: L("Your own")
        }
    }
}

/// Owns the theme list, the current theme and the motion preferences.
@MainActor
final class ThemeStore: ObservableObject {
    @Published private(set) var themes: [Theme] = []
    @Published private(set) var current: Theme = .fallback
    @Published var logo: LogoStyle { didSet { UserDefaults.standard.set(logo.rawValue, forKey: "logo") } }
    /// Springs, morphing and ripples.
    @Published var motion: Bool { didSet { UserDefaults.standard.set(motion, forKey: "motion") } }
    /// Glow and background pulse with the music.
    @Published var reactive: Bool { didSet { UserDefaults.standard.set(reactive, forKey: "reactive") } }
    /// Light running along the curve.
    @Published var shimmer: Bool { didSet { UserDefaults.standard.set(shimmer, forKey: "shimmer") } }
    @Published private(set) var loadErrors: [String] = []
    /// User-supplied logo (Application Support/MyOwnEQ/Logo/custom.png).
    @Published private(set) var customLogo: NSImage?
    /// Draw the custom logo monochrome in the menu bar (best for transparent logos).
    @Published var customLogoTemplate: Bool { didSet { UserDefaults.standard.set(customLogoTemplate, forKey: "customLogoTemplate") } }

    private let d = UserDefaults.standard

    init() {
        logo = LogoStyle(rawValue: d.string(forKey: "logo") ?? "") ?? .wave
        motion = d.object(forKey: "motion") as? Bool ?? true
        reactive = d.object(forKey: "reactive") as? Bool ?? true
        shimmer = d.object(forKey: "shimmer") as? Bool ?? true
        customLogoTemplate = d.object(forKey: "customLogoTemplate") as? Bool ?? false
        customLogo = NSImage(contentsOf: Self.customLogoURL)
        if logo == .custom, customLogo == nil { logo = .wave }
        reload()
    }

    static var customLogoURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MyOwnEQ/Logo/custom.png")
    }

    /// Ask for an image, store it as a 512 px PNG and switch to it.
    func chooseCustomLogo() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic, .svg, .image]
        panel.message = L("Choose an image for your logo (square, transparent PNG works best)")
        guard panel.runModal() == .OK, let url = panel.url, let img = NSImage(contentsOf: url) else { return }
        let side: CGFloat = 512
        let scale = min(side / max(img.size.width, 1), side / max(img.size.height, 1))
        let w = img.size.width * scale, h = img.size.height * scale
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(side), pixelsHigh: Int(side), bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0) else { return }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: NSRect(x: (side - w) / 2, y: (side - h) / 2, width: w, height: h))
        NSGraphicsContext.restoreGraphicsState()
        guard let png = rep.representation(using: .png, properties: [:]) else { return }
        try? FileManager.default.createDirectory(at: Self.customLogoURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard (try? png.write(to: Self.customLogoURL)) != nil else { return }
        customLogo = NSImage(data: png)
        logo = .custom
    }

    func removeCustomLogo() {
        try? FileManager.default.removeItem(at: Self.customLogoURL)
        customLogo = nil
        if logo == .custom { logo = .wave }
    }

    var motionAllowed: Bool { motion && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }

    func select(_ id: String) {
        guard let t = themes.first(where: { $0.id == id }) else { return }
        current = t
        d.set(id, forKey: "theme")
    }

    func reload() {
        var byId: [String: Theme] = [Theme.fallback.id: .fallback]
        var order: [String] = [Theme.fallback.id]
        var errors: [String] = []
        for dir in Self.searchDirs() {
            let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
            for f in files.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) where f.pathExtension == "json" {
                do {
                    let t = try JSONDecoder().decode(Theme.self, from: Data(contentsOf: f))
                    if byId[t.id] == nil { order.append(t.id) }
                    byId[t.id] = t
                } catch {
                    errors.append("\(f.lastPathComponent): \(error.localizedDescription)")
                }
            }
        }
        themes = order.compactMap { byId[$0] }
        loadErrors = errors
        current = byId[d.string(forKey: "theme") ?? ""] ?? byId[current.id] ?? .fallback
    }

    static var userDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MyOwnEQ/Themes", isDirectory: true)
    }

    func openUserFolder() {
        try? FileManager.default.createDirectory(at: Self.userDir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(Self.userDir)
    }

    /// Bundled themes, then a repo checkout's `themes/` (dev builds), then user themes (win ties).
    private static func searchDirs() -> [URL] {
        var dirs: [URL] = []
        if let r = Bundle.main.resourceURL { dirs.append(r.appendingPathComponent("Themes")) }
        var u = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().deletingLastPathComponent()
        for _ in 0..<6 {
            let t = u.appendingPathComponent("themes")
            if FileManager.default.fileExists(atPath: t.appendingPathComponent("neon-sunset.json").path) {
                dirs.append(t)
                break
            }
            u = u.deletingLastPathComponent()
        }
        dirs.append(userDir)
        return dirs
    }
}

// MARK: - hex colors

extension NSColor {
    convenience init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        var v: UInt64 = 0
        Scanner(string: s).scanHexInt64(&v)
        let (r, g, b, a): (UInt64, UInt64, UInt64, UInt64) = s.count == 8
            ? (v >> 24 & 0xFF, v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF)
            : (v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF, 0xFF)
        self.init(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
    }
}

extension Color {
    init(hex: String) { self.init(nsColor: NSColor(hex: hex)) }
}
