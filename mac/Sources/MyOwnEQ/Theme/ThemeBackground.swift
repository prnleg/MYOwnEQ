import SwiftUI

/// Smoothed live spectrum + overall level, shared by every view that draws
/// per frame. Fetches from the core at most once per frame, so the curve and
/// the background can both read it without competing for the buffer.
@MainActor
final class SpectrumFeed {
    static let shared = SpectrumFeed()

    private(set) var smooth: [Float] = []
    private(set) var peaks: [Float] = []
    /// 0…1, how loud things are right now (smoothed).
    private(set) var level: Double = 0
    private var last: Date = .distantPast

    func update(_ c: AppController, now: Date) {
        let dt = now.timeIntervalSince(last)
        guard dt > 1.0 / 90 else { return }
        last = now
        let step = Float(min(dt, 0.1))
        guard let s = c.spectrum(), !s.isEmpty else {
            decay(step)
            return
        }
        if smooth.count != s.count {
            smooth = s
            peaks = s
        }
        var sum: Float = 0
        for i in s.indices {
            let v = max(s[i], -110)
            // Fast attack, slow release.
            let k: Float = v > smooth[i] ? min(1, step * 28) : min(1, step * 6)
            smooth[i] += (v - smooth[i]) * k
            peaks[i] = max(smooth[i], peaks[i] - step * 18)
            sum += max(0, (smooth[i] + 72) / 72)
        }
        let target = Double(sum / Float(s.count))
        level += (min(1, target * 1.8) - level) * min(1, Double(step) * (target > level ? 14 : 4))
    }

    private func decay(_ step: Float) {
        for i in smooth.indices {
            smooth[i] = max(-110, smooth[i] - step * 40)
            peaks[i] = max(smooth[i], peaks[i] - step * 18)
        }
        level = max(0, level - Double(step) * 2)
    }
}

/// Slow drifting color blobs over the theme's base color. Pulses gently with
/// the music when "React to music" is on.
// parity: animated-background
struct ThemeBackground: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    var compact = false

    var body: some View {
        let t = theme.current
        let animate = theme.motionAllowed
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: !animate)) { tl in
            Canvas { ctx, size in
                let time = tl.date.timeIntervalSinceReferenceDate * 0.07 * t.background.speed
                if theme.reactive { SpectrumFeed.shared.update(c, now: tl.date) }
                let pulse = theme.reactive ? SpectrumFeed.shared.level : 0
                ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(t.baseColor))
                let colors = t.blobColors
                let n = max(colors.count, 1)
                let dim = max(size.width, size.height)
                for (i, col) in colors.enumerated() {
                    let ph = Double(i) / Double(n) * .pi * 2
                    let cx = size.width * (0.5 + 0.38 * sin(time * (0.9 + 0.13 * Double(i)) + ph))
                    let cy = size.height * (0.5 + 0.36 * cos(time * (0.7 + 0.17 * Double(i)) + ph * 1.3))
                    let r = dim * (compact ? 0.75 : 0.55) * (1 + 0.12 * sin(time * 1.3 + ph) + 0.18 * pulse)
                    let a = t.background.intensity * (0.85 + 0.35 * pulse)
                    let rect = CGRect(x: cx - r, y: cy - r, width: 2 * r, height: 2 * r)
                    ctx.fill(Path(ellipseIn: rect), with: .radialGradient(
                        Gradient(stops: [.init(color: col.opacity(a), location: 0),
                                         .init(color: col.opacity(a * 0.35), location: 0.45),
                                         .init(color: col.opacity(0), location: 1)]),
                        center: CGPoint(x: cx, y: cy), startRadius: 0, endRadius: r))
                }
                // Soft vignette keeps text readable at the edges.
                ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .radialGradient(
                    Gradient(colors: [.clear, t.baseColor.opacity(t.dark ? 0.55 : 0.25)]),
                    center: CGPoint(x: size.width / 2, y: size.height / 2),
                    startRadius: dim * 0.25, endRadius: dim * 0.85))
            }
        }
        .ignoresSafeArea()
    }
}
