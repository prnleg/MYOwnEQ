import AppKit
import CoreImage
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
        /// File name in `Themes/images/` (user themes only).
        var image: String?
        var imageOpacity: Double = 1
        var imageBlur: Double = 0

        enum CodingKeys: String, CodingKey {
            case base, blobs, intensity, speed, image
            case imageOpacity = "image_opacity", imageBlur = "image_blur"
        }
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
        background: .init(base: "#0E0B1A", blobs: ["#FF2E88", "#7B2CBF", "#FF7A2E", "#3A0CA3"], intensity: 0.45, speed: 1, image: nil),
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
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        base = try c.decode(String.self, forKey: .base)
        blobs = try c.decodeIfPresent([String].self, forKey: .blobs) ?? []
        intensity = try c.decodeIfPresent(Double.self, forKey: .intensity) ?? 0.4
        speed = try c.decodeIfPresent(Double.self, forKey: .speed) ?? 1
        image = try c.decodeIfPresent(String.self, forKey: .image)
        imageOpacity = try c.decodeIfPresent(Double.self, forKey: .imageOpacity) ?? 1
        imageBlur = try c.decodeIfPresent(Double.self, forKey: .imageBlur) ?? 0
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
// parity: themes
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

    init(core: CoreBridge? = nil) {
        self.core = core
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
    // parity: custom-logo
    func chooseCustomLogo() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic, .svg, .image]
        panel.message = L("Choose an image for your logo (square, transparent PNG works best)")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let img = NSImage(contentsOf: url) else {
            let a = NSAlert()
            a.messageText = L("That image couldn't be read.")
            a.runModal()
            return
        }
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
        previewing = false
    }

    // MARK: theme editor
    // parity: theme-editor

    /// True while the editor shows a draft as the app's theme.
    private var previewing = false

    /// Show a draft everywhere without saving it (theme editor).
    func preview(_ t: Theme) {
        previewing = true
        current = t
    }

    /// Back to the saved choice (editor cancelled).
    func endPreview() {
        guard previewing else { return }
        previewing = false
        current = themes.first { $0.id == d.string(forKey: "theme") } ?? .fallback
    }

    /// Themes in the user folder can be edited/deleted; built-in ones are copied first.
    func isUserTheme(_ t: Theme) -> Bool {
        FileManager.default.fileExists(atPath: Self.userDir.appendingPathComponent("\(t.id).json").path)
    }

    /// Save (new when `t.id` is empty) and switch to it. Returns the id.
    @discardableResult
    func save(_ t: Theme) -> String? {
        guard let core, let data = try? JSONEncoder().encode(t),
              var obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if t.id.isEmpty { obj.removeValue(forKey: "id") }
        let r = core.command("theme_save", ["theme": obj])
        guard let id = r["id"] as? String else {
            loadErrors = [r["error"] as? String ?? "?"]
            return nil
        }
        reload()
        select(id)
        return id
    }

    func delete(_ t: Theme) {
        _ = core?.command("theme_delete", ["id": t.id])
        reload()
        if current.id == t.id { select(Theme.fallback.id) }
    }

    /// `MEQT-…` code + QR (background image not included).
    func shareInfo(_ t: Theme) -> ShareInfo? {
        guard let core, let data = try? JSONEncoder().encode(t),
              let obj = try? JSONSerialization.jsonObject(with: data) else { return nil }
        let r = core.command("theme_code", ["theme": obj])
        guard let code = r["code"] as? String else { return nil }
        return ShareInfo(name: t.name, code: code, qr: r["qr"] as? [String] ?? [], kind: .theme)
    }

    /// Copy an image into `Themes/images` as a JPEG (longest side ≤ 2560 px).
    /// Returns the file name to put in `background.image`.
    func importBackgroundImage(_ url: URL) -> String? {
        guard let img = NSImage(contentsOf: url),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        let maxSide = 2560.0
        let scale = min(1, maxSide / Double(max(cg.width, cg.height)))
        let w = Int(Double(cg.width) * scale), h = Int(Double(cg.height) * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let out = ctx.makeImage(),
              let jpg = NSBitmapImageRep(cgImage: out).representation(using: .jpeg, properties: [.compressionFactor: 0.86])
        else { return nil }
        let name = "bg-\(UUID().uuidString.prefix(8).lowercased()).jpg"
        let dir = Self.userDir.appendingPathComponent("images", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard (try? jpg.write(to: dir.appendingPathComponent(name))) != nil else { return nil }
        return name
    }

    private var imageCache: [String: NSImage] = [:]

    /// A theme's background image, blurred as the theme asks (cached).
    func backgroundImage(_ t: Theme) -> NSImage? {
        guard let file = t.background.image else { return nil }
        let blur = (t.background.imageBlur * 2).rounded() / 2
        let key = "\(file)|\(blur)"
        if let i = imageCache[key] { return i }
        let url = Self.userDir.appendingPathComponent("images").appendingPathComponent(file)
        guard let src = NSImage(contentsOf: url) else { return nil }
        var result = src
        if blur > 0, let tiff = src.tiffRepresentation, let ci = CIImage(data: tiff) {
            // Radius relative to the image size, so it looks the same for any resolution (Windows too).
            let radius = blur * max(ci.extent.width, ci.extent.height) / 1200
            let f = CIFilter(name: "CIGaussianBlur", parameters: [kCIInputImageKey: ci.clampedToExtent(), kCIInputRadiusKey: radius])
            if let o = f?.outputImage?.cropped(to: ci.extent),
               let cg = CIContext().createCGImage(o, from: ci.extent) {
                result = NSImage(cgImage: cg, size: src.size)
            }
        }
        if imageCache.count > 8 { imageCache.removeAll() }
        imageCache[key] = result
        return result
    }

    /// Parsing, validation and override rules live in the core (same for every app).
    private let core: CoreBridge?

    func reload() {
        var themes: [Theme] = [.fallback]
        var errors: [String] = []
        if let core {
            let r = core.command("load_themes", ["dirs": Self.searchDirs().map(\.path)])
            for raw in r["themes"] as? [Any] ?? [] {
                guard let d = try? JSONSerialization.data(withJSONObject: raw) else { continue }
                do {
                    let t = try JSONDecoder().decode(Theme.self, from: d)
                    if let i = themes.firstIndex(where: { $0.id == t.id }) { themes[i] = t } else { themes.append(t) }
                } catch { errors.append("\(error.localizedDescription)") }
            }
            errors += r["errors"] as? [String] ?? []
        }
        self.themes = themes
        loadErrors = errors
        current = themes.first { $0.id == d.string(forKey: "theme") } ?? themes.first { $0.id == current.id } ?? .fallback
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

    /// `#RRGGBB`, or `#RRGGBBAA` when not opaque.
    var hex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .black
        let v = [c.redComponent, c.greenComponent, c.blueComponent, c.alphaComponent].map { Int(($0 * 255).rounded()).clamped(0, 255) }
        return v[3] == 255 ? String(format: "#%02X%02X%02X", v[0], v[1], v[2])
                           : String(format: "#%02X%02X%02X%02X", v[0], v[1], v[2], v[3])
    }
}

private extension Int {
    func clamped(_ lo: Int, _ hi: Int) -> Int { Swift.min(Swift.max(self, lo), hi) }
}
