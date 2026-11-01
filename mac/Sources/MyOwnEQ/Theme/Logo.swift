import AppKit
import SwiftUI

/// App logo. `.wave`: EQ curve with three band dots. `.finger`: three
/// sliders, the middle one standing tall (a greeting to paid EQs).
struct LogoMark: View {
    var style: LogoStyle
    var colors: [Color]
    var base: Color = Color(hex: "#0E0B1A")
    var custom: NSImage?

    var body: some View {
        if style == .custom {
            if let custom {
                Image(nsImage: custom).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
            } else {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
                    .foregroundStyle(.secondary)
                    .overlay(Image(systemName: "photo.badge.plus").font(.title2).foregroundStyle(.secondary))
                    .aspectRatio(1, contentMode: .fit)
            }
        } else {
            mark
        }
    }

    private var mark: some View {
        GeometryReader { g in
            let s = min(g.size.width, g.size.height)
            let shape = RoundedRectangle(cornerRadius: s * 0.225, style: .continuous)
            ZStack {
                shape.fill(LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing))
                shape.fill(RadialGradient(colors: [.white.opacity(0.35), .clear], center: .topLeading,
                                          startRadius: 0, endRadius: s * 0.9))
                shape.fill(LinearGradient(colors: [.clear, base.opacity(0.12)], startPoint: .center, endPoint: .bottom))
                Canvas { ctx, size in
                    if style == .finger { Self.drawFinger(ctx, size) } else { Self.drawWave(ctx, size, dot: colors.first ?? .pink) }
                }
                shape.strokeBorder(.white.opacity(0.25), lineWidth: max(0.5, s * 0.008))
            }
            .frame(width: s, height: s)
            .shadow(color: .black.opacity(0.25), radius: s * 0.04, y: s * 0.02)
        }
        .aspectRatio(1, contentMode: .fit)
    }

    /// Logo curve shape, x in 0…1 → roughly -0.42…0.55.
    static func waveY(_ x: Double) -> Double {
        0.55 * exp(-pow((x - 0.22) / 0.13, 2)) - 0.45 * exp(-pow((x - 0.52) / 0.12, 2)) + 0.5 * exp(-pow((x - 0.8) / 0.11, 2))
    }

    static func wavePoints(_ size: CGSize) -> [CGPoint] {
        (0...64).map { i in
            let x = Double(i) / 64
            return CGPoint(x: size.width * (0.14 + 0.72 * x), y: size.height * (0.54 - 0.3 * waveY(x)))
        }
    }

    static func drawWave(_ ctx: GraphicsContext, _ size: CGSize, dot: Color) {
        let s = size.width
        let pts = wavePoints(size)
        var p = Path()
        p.addLines(pts)
        var glow = ctx
        glow.addFilter(.blur(radius: s * 0.03))
        glow.stroke(p, with: .color(.white.opacity(0.8)), style: StrokeStyle(lineWidth: s * 0.07, lineCap: .round, lineJoin: .round))
        ctx.stroke(p, with: .color(.white), style: StrokeStyle(lineWidth: s * 0.055, lineCap: .round, lineJoin: .round))
        for i in [14, 33, 51] {
            let c = pts[i]
            let r = s * 0.06
            ctx.fill(Path(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r)), with: .color(.white))
            let r2 = r * 0.45
            ctx.fill(Path(ellipseIn: CGRect(x: c.x - r2, y: c.y - r2, width: 2 * r2, height: 2 * r2)), with: .color(dot))
        }
    }

    static func drawFinger(_ ctx: GraphicsContext, _ size: CGSize) {
        let s = size.width
        let xs: [CGFloat] = [0.3, 0.5, 0.7]
        let knobs: [CGFloat] = [0.62, 0.2, 0.66]
        let top = s * 0.16, bottom = s * 0.84
        for (x, k) in zip(xs, knobs) {
            let cx = s * x
            let track = Path(roundedRect: CGRect(x: cx - s * 0.03, y: top, width: s * 0.06, height: bottom - top), cornerRadius: s * 0.03)
            ctx.fill(track, with: .color(.black.opacity(0.22)))
            let ky = s * k
            let fill = Path(roundedRect: CGRect(x: cx - s * 0.03, y: ky, width: s * 0.06, height: bottom - ky), cornerRadius: s * 0.03)
            ctx.fill(fill, with: .color(.white.opacity(0.9)))
            let knob = Path(roundedRect: CGRect(x: cx - s * 0.085, y: ky - s * 0.045, width: s * 0.17, height: s * 0.09), cornerRadius: s * 0.035)
            var glow = ctx
            glow.addFilter(.blur(radius: s * 0.025))
            glow.fill(knob, with: .color(.white.opacity(0.7)))
            ctx.fill(knob, with: .color(.white))
        }
    }
}

/// The logo the user picked, in the current theme's colors.
struct AppLogo: View {
    @EnvironmentObject var theme: ThemeStore
    var style: LogoStyle?

    var body: some View {
        LogoMark(style: style ?? theme.logo, colors: theme.current.curveColors, base: theme.current.baseColor, custom: theme.customLogo)
    }
}

enum LogoRenderer {
    /// Menu bar icon. Built-in logos are monochrome templates; a custom logo
    /// is shown in color unless `template` is set.
    static func menuBarImage(_ style: LogoStyle, enabled: Bool, custom: NSImage? = nil, template: Bool = false) -> NSImage {
        if style == .custom, let custom {
            let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { r in
                custom.draw(in: r, from: .zero, operation: .sourceOver, fraction: enabled ? 1 : 0.4)
                return true
            }
            img.isTemplate = template
            return img
        }
        return builtInMenuBarImage(style == .custom ? .wave : style, enabled: enabled)
    }

    private static func builtInMenuBarImage(_ style: LogoStyle, enabled: Bool) -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let img = NSImage(size: size, flipped: true) { r in
            NSColor.black.setStroke()
            NSColor.black.setFill()
            let s = r.width
            switch style {
            case .wave, .custom:
                let path = NSBezierPath()
                path.lineWidth = 1.7
                path.lineCapStyle = .round
                path.lineJoinStyle = .round
                // Flipped coords: y grows downward. Off = flat line.
                let pts = (0...64).map { i -> CGPoint in
                    let x = Double(i) / 64
                    let y = enabled ? LogoMark.waveY(x) : 0
                    return CGPoint(x: 1.5 + (s - 3) * x, y: s * 0.55 - (s * 0.62) * y)
                }
                path.move(to: pts[0])
                pts.dropFirst().forEach { path.line(to: $0) }
                path.stroke()
                for i in [14, 33, 51] {
                    let c = pts[i]
                    NSBezierPath(ovalIn: NSRect(x: c.x - 2, y: c.y - 2, width: 4, height: 4)).fill()
                }
            case .finger:
                let knobs: [CGFloat] = enabled ? [0.62, 0.16, 0.66] : [0.6, 0.6, 0.6]
                for (x, k) in zip([0.22, 0.5, 0.78] as [CGFloat], knobs) {
                    let cx = s * x
                    let track = NSBezierPath(roundedRect: NSRect(x: cx - 0.75, y: 2, width: 1.5, height: s - 4), xRadius: 0.75, yRadius: 0.75)
                    NSColor.black.withAlphaComponent(0.45).setFill()
                    track.fill()
                    NSColor.black.setFill()
                    NSBezierPath(roundedRect: NSRect(x: cx - 1.25, y: s * k, width: 2.5, height: s - 2 - s * k), xRadius: 1.2, yRadius: 1.2).fill()
                    NSBezierPath(roundedRect: NSRect(x: cx - 3, y: s * k - 1.6, width: 6, height: 3.2), xRadius: 1.5, yRadius: 1.5).fill()
                }
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    /// Renders the brand icon (Neon Sunset) to a PNG. Used by build-app.sh via
    /// `MyOwnEQ --render-icon <wave|finger> <size> <out.png>`.
    @MainActor
    static func renderPNG(style: LogoStyle, size: CGFloat, to url: URL) -> Bool {
        let t = Theme.fallback
        // macOS icon grid: artwork is ~80% of the canvas.
        let view = LogoMark(style: style, colors: t.curveColors, base: t.baseColor)
            .frame(width: size * 0.8, height: size * 0.8)
            .frame(width: size, height: size)
        let r = ImageRenderer(content: view)
        r.scale = 1
        guard let cg = r.cgImage else { return false }
        let rep = NSBitmapImageRep(cgImage: cg)
        guard let data = rep.representation(using: .png, properties: [:]) else { return false }
        return (try? data.write(to: url)) != nil
    }
}
