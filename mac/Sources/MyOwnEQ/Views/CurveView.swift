import SwiftUI

/// Frequency-response curve over a live spectrum. Parametric bands get
/// draggable handles; the band being edited is highlighted with its own
/// curve and a readout of exactly what is moving and by how much.
struct CurveView: View {
    @EnvironmentObject var c: AppController
    @Binding var profile: Profile
    @Binding var selected: Int?
    var interactive = true

    @State private var curve: Curve?
    @State private var dragging: Int?
    @State private var dragStart: Band?

    private static let fMin = 20.0, fMax = 20_000.0

    var body: some View {
        GeometryReader { geo in
            TimelineView(.animation(minimumInterval: 1.0 / 30.0)) { _ in
                let spectrum = c.spectrum()
                Canvas { ctx, size in
                    draw(ctx: ctx, size: size, spectrum: spectrum)
                }
            }
            .contentShape(Rectangle())
            .gesture(interactive ? drag(size: geo.size) : nil)
        }
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor).opacity(0.6)))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .onAppear { curve = c.curve(for: profile) }
        .onChange(of: profile) { _, p in curve = c.curve(for: p) }
        .onChange(of: c.state?.sampleRate) { _, _ in curve = c.curve(for: profile) }
    }

    // MARK: - geometry

    private var bands: [Band] {
        if case .parametric(let b) = profile.eq { return b }
        return []
    }

    private var dbRange: Double {
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

    private func y(_ db: Double, _ h: CGFloat) -> CGFloat {
        h / 2 - CGFloat(db / dbRange) * (h / 2 - 12)
    }

    private func db(_ y: CGFloat, _ h: CGFloat) -> Double {
        Double((h / 2 - y) / (h / 2 - 12)) * dbRange
    }

    private func handle(_ b: Band, _ size: CGSize) -> CGPoint {
        CGPoint(x: x(b.freq, size.width), y: y(b.kind.hasGain ? b.gainDb : 0, size.height))
    }

    // MARK: - drawing

    private func draw(ctx: GraphicsContext, size: CGSize, spectrum: [Float]?) {
        let (w, h) = (size.width, size.height)
        let grid = Color.secondary.opacity(0.18)

        // Grid: decades + 2/5 marks, dB lines every 6 dB.
        for f in [50.0, 100, 200, 500, 1000, 2000, 5000, 10000] {
            let gx = x(f, w)
            ctx.stroke(Path { $0.move(to: .init(x: gx, y: 0)); $0.addLine(to: .init(x: gx, y: h)) }, with: .color(grid))
            let label = f >= 1000 ? "\(Int(f / 1000))k" : "\(Int(f))"
            ctx.draw(Text(label).font(.system(size: 9)).foregroundStyle(.secondary), at: .init(x: gx + 2, y: h - 8), anchor: .leading)
        }
        var d = -dbRange
        while d <= dbRange {
            let gy = y(d, h)
            ctx.stroke(Path { $0.move(to: .init(x: 0, y: gy)); $0.addLine(to: .init(x: w, y: gy)) },
                       with: .color(d == 0 ? Color.secondary.opacity(0.45) : grid))
            if d != 0 {
                ctx.draw(Text(String(format: "%+.0f", d)).font(.system(size: 9)).foregroundStyle(.secondary),
                         at: .init(x: 4, y: gy - 6), anchor: .leading)
            }
            d += 6
        }

        // Live spectrum (post-EQ), -96..0 dBFS mapped to full height.
        if let s = spectrum, s.count > 1 {
            var p = Path()
            p.move(to: .init(x: 0, y: h))
            for (i, v) in s.enumerated() {
                let px = CGFloat(i) / CGFloat(s.count - 1) * w
                let py = h - CGFloat(max(0, min(1, (Double(v) + 96) / 96))) * h
                p.addLine(to: .init(x: px, y: py))
            }
            p.addLine(to: .init(x: w, y: h))
            p.closeSubpath()
            ctx.fill(p, with: .linearGradient(Gradient(colors: [Color.accentColor.opacity(0.25), Color.accentColor.opacity(0.03)]),
                                              startPoint: .init(x: 0, y: 0), endPoint: .init(x: 0, y: h)))
        }

        guard let curve else { return }

        // Selected band's own contribution, filled.
        let offset = profile.correction.count
        if let sel = selected ?? dragging, sel < bands.count, offset + sel < curve.bands.count {
            let bc = curve.bands[offset + sel]
            var p = Path()
            p.move(to: .init(x: 0, y: y(0, h)))
            for (f, v) in zip(curve.freqs, bc) { p.addLine(to: .init(x: x(f, w), y: y(v, h))) }
            p.addLine(to: .init(x: w, y: y(0, h)))
            ctx.fill(p, with: .color(.orange.opacity(0.22)))
            ctx.stroke(p, with: .color(.orange.opacity(0.7)), lineWidth: 1)
        }

        // Total response (incl. preamp) dashed, EQ shape (without preamp) solid.
        var total = Path(), shape = Path()
        for (i, (f, v)) in zip(curve.freqs, curve.total).enumerated() {
            let pt = CGPoint(x: x(f, w), y: y(v, h))
            let pt2 = CGPoint(x: pt.x, y: y(v - curve.preamp, h))
            if i == 0 { total.move(to: pt); shape.move(to: pt2) } else { total.addLine(to: pt); shape.addLine(to: pt2) }
        }
        ctx.stroke(total, with: .color(.secondary.opacity(0.6)), style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
        ctx.stroke(shape, with: .color(.accentColor), lineWidth: 2.2)

        // Handles + readout.
        guard interactive else { return }
        for (i, b) in bands.enumerated() {
            let pt = handle(b, size)
            let isSel = i == (selected ?? dragging)
            let r: CGFloat = isSel ? 7 : 5.5
            let rect = CGRect(x: pt.x - r, y: pt.y - r, width: 2 * r, height: 2 * r)
            ctx.fill(Path(ellipseIn: rect), with: .color(b.enabled ? (isSel ? .orange : .accentColor) : .gray))
            ctx.stroke(Path(ellipseIn: rect), with: .color(.white), lineWidth: 1.5)
            ctx.draw(Text("\(i + 1)").font(.system(size: 8, weight: .bold)).foregroundStyle(.white), at: pt)
            if isSel {
                var label = "\(fmtHz(b.freq))"
                if b.kind.hasGain { label += String(format: "  %+.1f dB", b.gainDb) }
                label += String(format: "  Q %.2f", b.q)
                if let s = dragStart, dragging == i {
                    let df = b.freq / s.freq
                    let dg = b.gainDb - s.gainDb
                    label += String(format: "   (Δ %@%.0f%%%@)", df >= 1 ? "+" : "", (df - 1) * 100,
                                    b.kind.hasGain ? String(format: ", %+.1f dB", dg) : "")
                }
                let anchorY = pt.y < 30 ? pt.y + 22 : pt.y - 20
                let ax = min(max(pt.x, 110), w - 110)
                let text = Text(label).font(.system(size: 11, weight: .medium).monospacedDigit())
                let resolved = ctx.resolve(text)
                let sz = resolved.measure(in: size)
                let bg = CGRect(x: ax - sz.width / 2 - 6, y: anchorY - sz.height / 2 - 3, width: sz.width + 12, height: sz.height + 6)
                ctx.fill(Path(roundedRect: bg, cornerRadius: 5), with: .color(Color(nsColor: .windowBackgroundColor).opacity(0.92)))
                ctx.draw(resolved, at: .init(x: ax, y: anchorY))
            }
        }
    }

    // MARK: - interaction

    private func drag(size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { g in
                if dragging == nil {
                    let hit = bands.enumerated()
                        .map { ($0.offset, hypot(handle($0.element, size).x - g.startLocation.x, handle($0.element, size).y - g.startLocation.y)) }
                        .filter { $0.1 < 16 }
                        .min { $0.1 < $1.1 }
                    guard let (i, _) = hit else { selected = nil; return }
                    dragging = i
                    selected = i
                    dragStart = bands[i]
                }
                guard let i = dragging, case .parametric(var bs) = profile.eq, i < bs.count else { return }
                var f = freq(g.location.x, size.width)
                let mag = pow(10, floor(log10(f)) - 2) // 3 significant digits
                f = (f / mag).rounded() * mag
                bs[i].freq = min(max(f, 20), 20_000)
                if bs[i].kind.hasGain {
                    bs[i].gainDb = (min(max(db(g.location.y, size.height), -24), 24) * 10).rounded() / 10
                }
                profile.eq = .parametric(bs)
            }
            .onEnded { _ in
                dragging = nil
                dragStart = nil
            }
    }
}

func fmtHz(_ f: Double) -> String {
    f >= 1000 ? String(format: "%.2f kHz", f / 1000) : String(format: "%.0f Hz", f)
}
