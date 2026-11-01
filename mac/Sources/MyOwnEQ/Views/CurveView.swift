import AppKit
import SwiftUI

/// Per-frame animation state for the curve. Lives outside SwiftUI's diffing
/// (a reference held in @State) and is advanced from the Canvas draw call.
final class CurveAnim {
    var shape: [Double] = [], shapeV: [Double] = []
    var total: [Double] = [], totalV: [Double] = []
    var band: [Double] = [], bandV: [Double] = []
    var bandIndex: Int?
    var range = 12.0, rangeV = 0.0
    var handleScale: [Double] = [], handleScaleV: [Double] = []
    var ripples: [(pt: CGPoint, t0: Double, color: Color)] = []
    var lastT: Double = 0
    var lastEmptyClick: (t: Double, pt: CGPoint)?
    /// Response curve for `curveFor` at `curveRate`, fetched from the core
    /// inside the draw pass, so it never waits on onAppear/onChange timing.
    var curve: Curve?
    var curveFor: Profile?
    var curveRate: Double?

    /// Damped spring toward `target`. k = stiffness, c = damping.
    static func spring(_ x: inout [Double], _ v: inout [Double], to target: [Double], k: Double, c: Double, dt: Double, snap: Bool) {
        if x.count != target.count || snap {
            x = target
            v = Array(repeating: 0, count: target.count)
            return
        }
        for i in x.indices {
            let a = k * (target[i] - x[i]) - c * v[i]
            v[i] += a * dt
            x[i] += v[i] * dt
        }
    }

    static func spring(_ x: inout Double, _ v: inout Double, to target: Double, k: Double, c: Double, dt: Double, snap: Bool) {
        if snap { x = target; v = 0; return }
        let a = k * (target - x) - c * v
        v += a * dt
        x += v * dt
    }
}

/// Frequency-response curve over the live spectrum. Springs between states,
/// glows with the music, shimmers, and is directly editable:
/// - Pro, parametric: drag band dots (Option-drag = Q), double-click adds a band.
/// - Pro, graphic: drag each slider up/down right on the graph.
/// - Easy: four dots — Bass, Mids, Clarity, Treble — dragged up/down.
struct CurveView: View {
    enum Edit { case none, easy, pro }

    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Binding var profile: Profile
    @Binding var selected: Int?
    var edit: Edit = .pro
    var compact = false

    /// Bumped once after appearing to guarantee a fresh draw.
    @State private var kick = 0
    @State private var dragging: Int?
    @State private var dragStart: (profile: Profile, qMode: Bool, pt: CGPoint)?
    @State private var hover: Int?
    @State private var anim = CurveAnim()

    private static let fMin = 20.0, fMax = 20_000.0

    private struct Handle {
        enum Kind {
            case band(Int)
            case point(Int)
            case macro(WritableKeyPath<Macros, Double>, String)
        }
        var kind: Kind
        var pt: CGPoint
        var enabled = true
        var bandIndex: Int? { if case .band(let i) = kind { return i }; return nil }
    }

    private static let macroSpots: [(WritableKeyPath<Macros, Double>, String, Double)] = [
        (\.bass, "Bass", 105), (\.mids, "Mids", 1000), (\.clarity, "Clarity", 3000), (\.treble, "Treble", 8000),
    ]

    var body: some View {
        GeometryReader { geo in
            TimelineView(.animation(minimumInterval: 1.0 / 60.0)) { tl in
                Canvas { ctx, size in
                    let now = tl.date.timeIntervalSinceReferenceDate
                    _ = kick
                    SpectrumFeed.shared.update(c, now: tl.date)
                    refreshCurve()
                    advance(now: now, size: size)
                    draw(ctx: ctx, size: size, now: now)
                }
            }
            .contentShape(Rectangle())
            .gesture(edit != .none ? drag(size: geo.size) : nil)
            .onContinuousHover { phase in
                guard edit != .none, dragging == nil else { return }
                if case .active(let p) = phase { hover = hit(p, geo.size) } else { hover = nil }
            }
        }
        .onAppear {
            refreshCurve()
            DispatchQueue.main.async { kick += 1 }
        }
    }

    private var curve: Curve? { anim.curve }

    private func refreshCurve() {
        let rate = c.state?.sampleRate
        guard anim.curve == nil || anim.curveFor != profile || anim.curveRate != rate else { return }
        if let cv = c.curve(for: profile) {
            anim.curve = cv
            anim.curveFor = profile
            anim.curveRate = rate
        }
    }

    // MARK: - geometry

    private var bands: [Band] {
        if case .parametric(let b) = profile.eq { return b }
        return []
    }

    private var points: [GraphicPoint] {
        if case .graphic(let p) = profile.eq { return p }
        return []
    }

    private var targetRange: Double {
        let peak = (curve?.total.map(abs).max() ?? 0)
        let bandPeak = bands.map { abs($0.gainDb) }.max() ?? 0
        return min(30, max(12, (max(peak, bandPeak) / 6).rounded(.up) * 6))
    }

    private func x(_ f: Double, _ w: CGFloat) -> CGFloat {
        CGFloat(log(f / Self.fMin) / log(Self.fMax / Self.fMin)) * w
    }

    private func freq(_ x: CGFloat, _ w: CGFloat) -> Double {
        Self.fMin * pow(Self.fMax / Self.fMin, Double(min(max(x / w, 0), 1)))
    }

    private var pad: CGFloat { compact ? 6 : 16 }

    private func y(_ db: Double, _ h: CGFloat) -> CGFloat {
        h / 2 - CGFloat(db / anim.range) * (h / 2 - pad)
    }

    private func db(_ y: CGFloat, _ h: CGFloat) -> Double {
        Double((h / 2 - y) / (h / 2 - pad)) * anim.range
    }

    private func handles(_ size: CGSize) -> [Handle] {
        switch edit {
        case .none: return []
        case .easy:
            return Self.macroSpots.map { kp, name, f in
                Handle(kind: .macro(kp, name), pt: CGPoint(x: x(f, size.width), y: y(profile.macros[keyPath: kp], size.height)))
            }
        case .pro:
            switch profile.eq {
            case .parametric(let bs):
                return bs.enumerated().map { i, b in
                    Handle(kind: .band(i), pt: CGPoint(x: x(b.freq, size.width), y: y(b.kind.hasGain ? b.gainDb : 0, size.height)),
                           enabled: b.enabled)
                }
            case .graphic(let ps):
                return ps.enumerated().map { i, p in
                    Handle(kind: .point(i), pt: CGPoint(x: x(p.freq, size.width), y: y(p.gainDb, size.height)))
                }
            }
        }
    }

    private func hit(_ p: CGPoint, _ size: CGSize) -> Int? {
        let hs = handles(size)
        // Graphic sliders are dense: hit by column, not by distance.
        let radius: CGFloat = points.count > 10 && edit == .pro ? 9 : 16
        return hs.enumerated()
            .map { ($0.offset, points.isEmpty || edit != .pro ? hypot($0.element.pt.x - p.x, $0.element.pt.y - p.y) : abs($0.element.pt.x - p.x)) }
            .filter { $0.1 < radius }
            .min { $0.1 < $1.1 }?.0
    }

    // MARK: - animation

    private func advance(now: Double, size: CGSize) {
        let a = anim
        let motion = theme.motionAllowed
        var dt = a.lastT == 0 ? 1.0 / 60 : min(now - a.lastT, 1.0 / 20)
        a.lastT = now
        guard let curve else { return }
        let hs = handles(size)
        let dragBand = dragging.flatMap { $0 < hs.count ? hs[$0].bandIndex : nil }
        let shapeT = curve.total.map { $0 - curve.preamp }
        let sel = edit == .pro ? (selected ?? dragBand) : nil
        let off = profile.correction.count
        let bandT: [Double] = (sel.map { off + $0 }).flatMap { $0 < curve.bands.count ? curve.bands[$0] : nil }
            ?? Array(repeating: 0, count: curve.freqs.count)
        if a.bandIndex != sel {
            // New selection grows out of the zero line.
            a.band = Array(repeating: 0, count: curve.freqs.count)
            a.bandV = a.band
            a.bandIndex = sel
        }
        // Dragging: stiff and critically damped so the line follows the finger.
        // Otherwise: softer with a little overshoot — the "boing".
        let (k, cDamp) = dragging != nil ? (900.0, 60.0) : (170.0, 15.0)
        let steps = dt > 1.0 / 50 ? 2 : 1
        dt /= Double(steps)
        let scaleT = hs.indices.map { i -> Double in
            if i == dragging { return 1.55 }
            if let b = hs[i].bandIndex, b == selected { return 1.3 }
            return i == hover ? 1.2 : 1
        }
        for _ in 0..<steps {
            CurveAnim.spring(&a.shape, &a.shapeV, to: shapeT, k: k, c: cDamp, dt: dt, snap: !motion)
            CurveAnim.spring(&a.total, &a.totalV, to: curve.total, k: k, c: cDamp, dt: dt, snap: !motion)
            CurveAnim.spring(&a.band, &a.bandV, to: bandT, k: 260, c: 20, dt: dt, snap: !motion)
            CurveAnim.spring(&a.range, &a.rangeV, to: targetRange, k: 90, c: 19, dt: dt, snap: !motion)
            CurveAnim.spring(&a.handleScale, &a.handleScaleV, to: scaleT, k: 380, c: 14, dt: dt, snap: !motion)
        }
        a.ripples.removeAll { now - $0.t0 > 0.9 }
    }

    // MARK: - drawing

    private func draw(ctx: GraphicsContext, size: CGSize, now: Double) {
        let t = theme.current
        let (w, h) = (size.width, size.height)
        let a = anim
        let level = theme.reactive ? SpectrumFeed.shared.level : 0
        let ink = t.ink
        let grid = ink.opacity(t.dark ? 0.07 : 0.09)
        let label = ink.opacity(0.45)
        let horiz = { (g: Gradient) in GraphicsContext.Shading.linearGradient(g, startPoint: .zero, endPoint: CGPoint(x: w, y: 0)) }

        // Grid.
        if !compact {
            for f in [50.0, 100, 200, 500, 1000, 2000, 5000, 10000] {
                let gx = x(f, w)
                ctx.stroke(Path { $0.move(to: .init(x: gx, y: 0)); $0.addLine(to: .init(x: gx, y: h)) }, with: .color(grid))
                let txt = f >= 1000 ? "\(Int(f / 1000))k" : "\(Int(f))"
                ctx.draw(Text(txt).font(.system(size: 9, weight: .medium)).foregroundStyle(label), at: .init(x: gx + 3, y: h - 8), anchor: .leading)
            }
            var d = -(a.range / 6).rounded(.down) * 6
            while d <= a.range {
                let gy = y(d, h)
                if d != 0 {
                    ctx.stroke(Path { $0.move(to: .init(x: 0, y: gy)); $0.addLine(to: .init(x: w, y: gy)) }, with: .color(grid))
                    ctx.draw(Text(String(format: "%+.0f", d)).font(.system(size: 9, weight: .medium)).foregroundStyle(label),
                             at: .init(x: 5, y: gy - 6), anchor: .leading)
                }
                d += 6
            }
        }

        // Live spectrum: smoothed body + peak-hold line.
        let feed = SpectrumFeed.shared
        if feed.smooth.count > 1 {
            let n = feed.smooth.count
            func py(_ v: Float) -> CGFloat { h - CGFloat(max(0, min(1, (Double(v) + 96) / 96))) * h * (compact ? 0.9 : 0.85) }
            var body = Path()
            body.move(to: .init(x: 0, y: h))
            for i in 0..<n { body.addLine(to: .init(x: CGFloat(i) / CGFloat(n - 1) * w, y: py(feed.smooth[i]))) }
            body.addLine(to: .init(x: w, y: h))
            body.closeSubpath()
            let sc = t.spectrumColors
            ctx.fill(body, with: .linearGradient(
                Gradient(colors: [sc[0].opacity(0.28 + 0.15 * level), sc[sc.count - 1].opacity(0.04)]),
                startPoint: .init(x: 0, y: h * 0.2), endPoint: .init(x: 0, y: h)))
            if !compact {
                var peak = Path()
                for i in 0..<n {
                    let pt = CGPoint(x: CGFloat(i) / CGFloat(n - 1) * w, y: py(feed.peaks[i]))
                    if i == 0 { peak.move(to: pt) } else { peak.addLine(to: pt) }
                }
                ctx.stroke(peak, with: .color(sc[0].opacity(0.35)), lineWidth: 1)
            }
        }

        guard let curve, a.shape.count == curve.freqs.count else { return }
        let xs = curve.freqs.map { x($0, w) }
        let zeroY = y(0, h)
        func line(_ vals: [Double]) -> Path {
            var p = Path()
            for (i, v) in vals.enumerated() {
                let pt = CGPoint(x: xs[i], y: y(v, h))
                if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
            }
            return p
        }

        // Zero line.
        ctx.stroke(Path { $0.move(to: .init(x: 0, y: zeroY)); $0.addLine(to: .init(x: w, y: zeroY)) },
                   with: .color(ink.opacity(0.22)), lineWidth: 1)

        // Selected band's own contribution, glowing in its color.
        if let sel = a.bandIndex, sel < bands.count, a.band.count == xs.count {
            let col = t.curveColor(at: Double(x(bands[sel].freq, w) / w))
            var p = line(a.band)
            p.addLine(to: .init(x: w, y: zeroY))
            p.addLine(to: .init(x: 0, y: zeroY))
            p.closeSubpath()
            ctx.fill(p, with: .color(col.opacity(0.22)))
            ctx.stroke(line(a.band), with: .color(col.opacity(0.75)), style: StrokeStyle(lineWidth: 1.2, dash: [3, 3]))
        }

        // Area under the EQ shape.
        let shape = line(a.shape)
        var area = shape
        area.addLine(to: .init(x: w, y: zeroY))
        area.addLine(to: .init(x: 0, y: zeroY))
        area.closeSubpath()
        ctx.drawLayer { l in
            l.opacity = 0.2 + 0.12 * level
            l.fill(area, with: horiz(t.curveGradient))
        }

        // Total incl. preamp, dashed.
        if !compact {
            ctx.stroke(line(a.total), with: .color(ink.opacity(0.3)), style: StrokeStyle(lineWidth: 1, dash: [4, 4]))
        }

        // Glow (breathes with the music), then the line itself.
        let lw: CGFloat = compact ? 2 : 2.6
        ctx.drawLayer { l in
            l.addFilter(.blur(radius: (compact ? 4 : 7) + 6 * level))
            l.opacity = t.glow * (0.55 + 0.45 * level)
            l.stroke(shape, with: horiz(t.curveGradient), style: StrokeStyle(lineWidth: lw * (2.2 + 1.5 * level), lineCap: .round, lineJoin: .round))
        }
        ctx.stroke(shape, with: horiz(t.curveGradient), style: StrokeStyle(lineWidth: lw, lineCap: .round, lineJoin: .round))

        // Shimmer: a highlight sliding along the curve.
        if theme.shimmer && theme.motionAllowed {
            let p = (now * 0.16).truncatingRemainder(dividingBy: 1.6) - 0.3
            let g = Gradient(stops: [
                .init(color: .white.opacity(0), location: max(0, min(1, p - 0.09))),
                .init(color: .white.opacity(t.dark ? 0.85 : 0.7), location: max(0, min(1, p))),
                .init(color: .white.opacity(0), location: max(0, min(1, p + 0.09))),
            ])
            ctx.stroke(shape, with: horiz(g), style: StrokeStyle(lineWidth: lw * 0.9, lineCap: .round, lineJoin: .round))
        }

        guard edit != .none else { return }

        // Ripples from grabbed handles.
        for r in a.ripples {
            let p = (now - r.t0) / 0.9
            for k in 0..<2 {
                let q = p - Double(k) * 0.18
                guard q > 0 else { continue }
                let rad = 8 + 46 * CGFloat(1 - pow(1 - q, 3))
                ctx.stroke(Path(ellipseIn: CGRect(x: r.pt.x - rad, y: r.pt.y - rad, width: 2 * rad, height: 2 * rad)),
                           with: .color(r.color.opacity(0.6 * (1 - q))), lineWidth: 2)
            }
        }

        let hs = handles(size)
        let activeHandle = dragging ?? selected.flatMap { s in hs.firstIndex { $0.bandIndex == s } }

        // Graphic sliders: a faint stem from 0 dB to each point.
        if case .graphic = profile.eq, edit == .pro {
            for (i, hd) in hs.enumerated() {
                let col = t.curveColor(at: Double(hd.pt.x / w))
                ctx.stroke(Path { $0.move(to: .init(x: hd.pt.x, y: zeroY)); $0.addLine(to: hd.pt) },
                           with: .color(col.opacity(i == activeHandle || i == hover ? 0.9 : 0.35)), lineWidth: points.count > 10 ? 2 : 3)
            }
        }

        // Handles.
        for (i, hd) in hs.enumerated() {
            let pt = hd.pt
            let col = hd.enabled ? t.curveColor(at: Double(pt.x / w)) : .gray
            let sc = i < a.handleScale.count ? a.handleScale[i] : 1
            let base: CGFloat = { if case .point = hd.kind { return points.count > 10 ? 3.5 : 5 }; if case .macro = hd.kind { return 7.5 }; return 6 }()
            let r = base * CGFloat(sc)
            if i == activeHandle || i == hover {
                var halo = ctx
                halo.addFilter(.blur(radius: 6))
                halo.fill(Path(ellipseIn: CGRect(x: pt.x - r * 1.9, y: pt.y - r * 1.9, width: r * 3.8, height: r * 3.8)),
                          with: .color(col.opacity(i == dragging ? 0.75 : 0.45)))
            }
            let rect = CGRect(x: pt.x - r, y: pt.y - r, width: 2 * r, height: 2 * r)
            ctx.fill(Path(ellipseIn: rect), with: .color(col))
            ctx.stroke(Path(ellipseIn: rect), with: .color(.white.opacity(0.95)), lineWidth: 1.6)
            switch hd.kind {
            case .band(let b):
                ctx.draw(Text("\(b + 1)").font(.system(size: 8 * CGFloat(min(sc, 1.3)), weight: .heavy)).foregroundStyle(.white), at: pt)
            case .macro(_, let name):
                let below = pt.y < h - 28
                ctx.draw(Text(L(name)).font(.system(size: 10, weight: .bold)).foregroundStyle(ink.opacity(0.8)),
                         at: CGPoint(x: pt.x, y: pt.y + (below ? r + 10 : -(r + 10))))
            case .point: break
            }
        }

        // Readout bubble for the active handle.
        if let i = activeHandle ?? (edit == .easy || !points.isEmpty ? hover : nil), i < hs.count {
            let pt = hs[i].pt
            var txt = ""
            switch hs[i].kind {
            case .band(let bi) where bi < bands.count:
                let b = bands[bi]
                txt = fmtHz(b.freq)
                if b.kind.hasGain { txt += String(format: "  %+.1f dB", b.gainDb) }
                txt += String(format: "  Q %.2f", b.q)
                if let s = dragStart, dragging == i, case .parametric(let sb) = s.profile.eq, bi < sb.count {
                    let df = b.freq / sb[bi].freq
                    txt += String(format: "   Δ %@%.0f%%", df >= 1 ? "+" : "", (df - 1) * 100)
                    if b.kind.hasGain { txt += String(format: " %+.1f dB", b.gainDb - sb[bi].gainDb) }
                    if s.qMode { txt += String(format: "  ×%.2f Q", b.q / max(sb[bi].q, 0.01)) }
                }
            case .point(let pi) where pi < points.count:
                txt = fmtHz(points[pi].freq) + String(format: "  %+.1f dB", points[pi].gainDb)
            case .macro(let kp, let name):
                txt = L(name) + String(format: "  %+.1f dB", profile.macros[keyPath: kp])
            default: break
            }
            if !txt.isEmpty {
                let resolved = ctx.resolve(Text(txt).font(.system(size: 11, weight: .semibold).monospacedDigit()).foregroundStyle(.white))
                let sz = resolved.measure(in: size)
                let ay = pt.y < 40 ? pt.y + 30 : pt.y - 28
                let ax = min(max(pt.x, sz.width / 2 + 10), w - sz.width / 2 - 10)
                let bg = CGRect(x: ax - sz.width / 2 - 8, y: ay - sz.height / 2 - 4, width: sz.width + 16, height: sz.height + 8)
                let col = t.curveColor(at: Double(pt.x / w))
                var sh = ctx
                sh.addFilter(.shadow(color: col.opacity(0.6), radius: 8))
                sh.fill(Path(roundedRect: bg, cornerRadius: bg.height / 2), with: .color(Color.black.opacity(0.78)))
                ctx.stroke(Path(roundedRect: bg, cornerRadius: bg.height / 2), with: .color(col.opacity(0.9)), lineWidth: 1)
                ctx.draw(resolved, at: .init(x: ax, y: ay))
            }
        }
    }

    // MARK: - interaction

    private func drag(size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { g in
                let hs = handles(size)
                if dragging == nil {
                    guard let i = hit(g.startLocation, size) else {
                        if selected != nil { selected = nil }
                        return
                    }
                    dragging = i
                    if let b = hs[i].bandIndex { selected = b }
                    dragStart = (profile, NSEvent.modifierFlags.contains(.option), hs[i].pt)
                    if theme.motionAllowed {
                        anim.ripples.append((hs[i].pt, Date().timeIntervalSinceReferenceDate,
                                             theme.current.curveColor(at: Double(hs[i].pt.x / size.width))))
                    }
                }
                guard let i = dragging, i < hs.count, let start = dragStart else { return }
                // A plain click selects; nothing changes until the mouse really moves.
                guard hypot(g.translation.width, g.translation.height) > 2 else { return }
                // Move relative to where the dot was grabbed (no jump to the cursor).
                let target = CGPoint(x: start.pt.x + g.translation.width, y: start.pt.y + g.translation.height)
                let dB = (min(max(db(target.y, size.height), -24), 24) * 10).rounded() / 10
                switch hs[i].kind {
                case .band(let bi):
                    guard case .parametric(var bs) = profile.eq, bi < bs.count,
                          case .parametric(let sb) = start.profile.eq, bi < sb.count else { return }
                    if start.qMode {
                        // Option-drag: up = narrower (higher Q), down = wider.
                        let q = sb[bi].q * pow(2, Double(-g.translation.height) / 70)
                        bs[bi].q = (min(max(q, 0.1), 30) * 100).rounded() / 100
                    } else {
                        var f = freq(target.x, size.width)
                        let mag = pow(10, floor(log10(f)) - 2) // 3 significant digits
                        f = (f / mag).rounded() * mag
                        bs[bi].freq = min(max(f, 20), 20_000)
                        if bs[bi].kind.hasGain { bs[bi].gainDb = dB }
                    }
                    profile.eq = .parametric(bs)
                case .point(let pi):
                    guard case .graphic(var ps) = profile.eq, pi < ps.count else { return }
                    ps[pi].gainDb = (min(max(dB, -12), 12) * 2).rounded() / 2
                    profile.eq = .graphic(ps)
                case .macro(let kp, _):
                    profile.macros[keyPath: kp] = (min(max(dB, -12), 12) * 2).rounded() / 2
                }
            }
            .onEnded { g in
                defer { dragging = nil; dragStart = nil }
                guard dragging == nil, hypot(g.translation.width, g.translation.height) < 3 else { return }
                guard edit == .pro, case .parametric = profile.eq else { return }
                // Double-click on empty space adds a band there.
                let now = Date().timeIntervalSinceReferenceDate
                if let last = anim.lastEmptyClick, now - last.t < 0.4, hypot(last.pt.x - g.location.x, last.pt.y - g.location.y) < 8 {
                    anim.lastEmptyClick = nil
                    addBand(at: g.location, size: size)
                } else {
                    anim.lastEmptyClick = (now, g.location)
                }
            }
    }

    private func addBand(at p: CGPoint, size: CGSize) {
        guard case .parametric(var bs) = profile.eq, bs.count < 64 else { return }
        let f = freq(p.x, size.width)
        let g = (min(max(db(p.y, size.height), -24), 24) * 10).rounded() / 10
        bs.append(Band(kind: .peak, freq: (f * 10).rounded() / 10, gainDb: g, q: 1.0))
        profile.eq = .parametric(bs)
        selected = bs.count - 1
        if theme.motionAllowed {
            anim.ripples.append((p, Date().timeIntervalSinceReferenceDate, theme.current.curveColor(at: Double(p.x / size.width))))
        }
    }
}

func fmtHz(_ f: Double) -> String {
    f >= 1000 ? String(format: "%.2f kHz", f / 1000) : String(format: "%.0f Hz", f)
}
