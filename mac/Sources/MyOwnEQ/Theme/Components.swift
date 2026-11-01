import SwiftUI

/// Glass card: blurred material tinted with the theme's panel color and a
/// faint gradient border.
struct Card<Content: View>: View {
    @EnvironmentObject var theme: ThemeStore
    var title: String?
    var icon: String?
    var padding: CGFloat = 14
    @ViewBuilder var content: Content

    var body: some View {
        let t = theme.current
        VStack(alignment: .leading, spacing: 10) {
            if let title {
                HStack(spacing: 6) {
                    if let icon {
                        Image(systemName: icon).foregroundStyle(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                    }
                    Text(title.uppercased()).font(.caption.weight(.bold)).tracking(0.8).foregroundStyle(.secondary)
                }
            }
            content
        }
        .padding(padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(GlassBackground(radius: 14))
    }
}

struct GlassBackground: View {
    @EnvironmentObject var theme: ThemeStore
    var radius: CGFloat = 14
    var highlighted = false

    var body: some View {
        let t = theme.current
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        shape.fill(.ultraThinMaterial)
            .overlay(shape.fill(t.panelColor.opacity(t.dark ? 0.55 : 0.6)))
            .overlay(shape.strokeBorder(
                LinearGradient(colors: highlighted ? t.curveColors : [t.ink.opacity(0.16), t.ink.opacity(0.04)],
                               startPoint: .topLeading, endPoint: .bottomTrailing),
                lineWidth: highlighted ? 1.5 : 1))
            .shadow(color: .black.opacity(t.dark ? 0.35 : 0.08), radius: 12, y: 6)
    }
}

/// Slider whose track fills from a center (bipolar, for dB) or from the left,
/// painted with the theme gradient; the thumb glows while dragged.
struct GlowSlider: View {
    @EnvironmentObject var theme: ThemeStore
    @Binding var value: Double
    var range: ClosedRange<Double>
    var bipolar = true
    var step: Double = 0
    var onEditingChanged: (Bool) -> Void = { _ in }

    @State private var dragging = false
    @State private var hover = false

    var body: some View {
        let t = theme.current
        GeometryReader { g in
            let w = g.size.width
            let frac = (value - range.lowerBound) / (range.upperBound - range.lowerBound)
            let x = CGFloat(min(max(frac, 0), 1)) * w
            let zero = bipolar ? CGFloat((0 - range.lowerBound) / (range.upperBound - range.lowerBound)) * w : 0
            ZStack(alignment: .leading) {
                Capsule().fill(t.ink.opacity(0.1)).frame(height: 5)
                Capsule()
                    .fill(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                    .frame(width: abs(x - zero), height: 5)
                    .offset(x: min(x, zero))
                    .shadow(color: t.accentColor.opacity(dragging ? 0.8 : 0.35), radius: dragging ? 8 : 4)
                if bipolar {
                    Rectangle().fill(t.ink.opacity(0.35)).frame(width: 1.5, height: 11).offset(x: zero - 0.75)
                }
                Circle()
                    .fill(.white)
                    .frame(width: dragging ? 18 : (hover ? 16 : 14), height: dragging ? 18 : (hover ? 16 : 14))
                    .shadow(color: t.curveColor(at: frac).opacity(dragging ? 0.95 : 0.5), radius: dragging ? 10 : 4)
                    .overlay(Circle().stroke(t.curveColor(at: frac), lineWidth: 2.5))
                    .offset(x: x - (dragging ? 9 : 7))
            }
            .frame(height: g.size.height)
            .contentShape(Rectangle())
            .onHover { h in withAnimation(.easeOut(duration: 0.15)) { hover = h } }
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { gv in
                    if !dragging {
                        withAnimation(.spring(response: 0.25, dampingFraction: 0.6)) { dragging = true }
                        onEditingChanged(true)
                    }
                    var v = range.lowerBound + Double(min(max(gv.location.x / w, 0), 1)) * (range.upperBound - range.lowerBound)
                    if step > 0 { v = (v / step).rounded() * step }
                    if bipolar, abs(v) < (range.upperBound - range.lowerBound) * 0.012 { v = 0 } // snap to 0
                    value = v
                }
                .onEnded { _ in
                    withAnimation(.spring(response: 0.35, dampingFraction: 0.55)) { dragging = false }
                    onEditingChanged(false)
                })
        }
        .frame(height: 22)
    }
}

/// Segmented switch with a sliding gradient pill.
struct PillPicker<T: Hashable>: View {
    @EnvironmentObject var theme: ThemeStore
    @Binding var selection: T
    let options: [(T, String)]
    @Namespace private var ns

    var body: some View {
        let t = theme.current
        HStack(spacing: 2) {
            ForEach(Array(options.enumerated()), id: \.offset) { _, opt in
                let on = opt.0 == selection
                Text(opt.1)
                    .font(.callout.weight(on ? .semibold : .regular))
                    .foregroundStyle(on ? .white : .secondary)
                    .padding(.horizontal, 14).padding(.vertical, 6)
                    .background {
                        if on {
                            Capsule().fill(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                                .shadow(color: t.accentColor.opacity(0.5), radius: 6)
                                .matchedGeometryEffect(id: "pill", in: ns)
                        }
                    }
                    .contentShape(Capsule())
                    .onTapGesture {
                        withAnimation(.spring(response: 0.35, dampingFraction: 0.75)) { selection = opt.0 }
                    }
            }
        }
        .padding(3)
        .background(Capsule().fill(t.ink.opacity(0.08)))
        .overlay(Capsule().strokeBorder(t.ink.opacity(0.1)))
    }
}

/// Prominent gradient button.
struct GlowButtonStyle: ButtonStyle {
    @EnvironmentObject var theme: ThemeStore
    var prominent = true

    func makeBody(configuration: Configuration) -> some View {
        let t = theme.current
        configuration.label
            .font(.callout.weight(.semibold))
            .foregroundStyle(prominent ? .white : t.ink)
            .padding(.horizontal, 14).padding(.vertical, 7)
            .background {
                if prominent {
                    Capsule().fill(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                        .shadow(color: t.accentColor.opacity(configuration.isPressed ? 0.2 : 0.5), radius: 8, y: 2)
                } else {
                    Capsule().fill(t.ink.opacity(configuration.isPressed ? 0.16 : 0.08))
                        .overlay(Capsule().strokeBorder(t.ink.opacity(0.12)))
                }
            }
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.6), value: configuration.isPressed)
    }
}

/// Title block at the top of a settings page.
struct PageHeader: View {
    @EnvironmentObject var theme: ThemeStore
    let title: String
    let subtitle: String
    let icon: String

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .font(.title2)
                .foregroundStyle(.white)
                .frame(width: 42, height: 42)
                .background(RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(LinearGradient(gradient: theme.current.curveGradient, startPoint: .topLeading, endPoint: .bottomTrailing)))
                .shadow(color: theme.current.accentColor.opacity(0.45), radius: 8)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.title2.weight(.bold))
                Text(subtitle).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
        }
    }
}

/// Tiny frequency-response line for lists and chips.
struct MiniCurve: View {
    @EnvironmentObject var theme: ThemeStore
    let values: [Double]
    var lineWidth: CGFloat = 1.6

    var body: some View {
        Canvas { ctx, size in
            guard values.count > 1 else { return }
            let range = max(6, values.map(abs).max() ?? 0)
            var p = Path()
            for (i, v) in values.enumerated() {
                let pt = CGPoint(x: CGFloat(i) / CGFloat(values.count - 1) * size.width,
                                 y: size.height / 2 - CGFloat(v / range) * (size.height / 2 - 1.5))
                if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
            }
            ctx.stroke(p, with: .linearGradient(theme.current.curveGradient, startPoint: .zero, endPoint: CGPoint(x: size.width, y: 0)),
                       style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round))
        }
    }
}
