import SwiftUI


// parity: pro-panel
struct ProPanel: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Binding var profile: Profile
    /// Parametric bands kept while trying a graphic layout, so going back
    /// without touching the sliders restores them exactly.
    @Binding var selected: Int?

    private enum Mode: String, CaseIterable {
        case parametric, g10, g31
        var label: String {
            switch self {
            case .parametric: L("Parametric")
            case .g10: L("Graphic 10")
            case .g31: L("Graphic 31")
            }
        }
    }

    private var mode: Mode {
        switch profile.eq {
        case .parametric: .parametric
        case .graphic(let p): p.count > 10 ? .g31 : .g10
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                PillPicker(selection: Binding(get: { mode }, set: switchMode),
                           options: Mode.allCases.map { ($0, $0.label) })
                Spacer()
                if case .parametric = profile.eq {
                    Button { withAnimation(.spring) { addBand() } } label: { Label(L("Add Band"), systemImage: "plus") }
                        .buttonStyle(GlowButtonStyle())
                }
            }

            switch profile.eq {
            case .parametric(let bands):
                VStack(spacing: 6) {
                    ForEach(Array(bands.enumerated()), id: \.element.id) { i, b in
                        BandRow(index: i, band: bandBinding(i), selected: selected == i,
                                color: theme.current.curveColor(at: log(b.freq / 20) / log(1000)),
                                onSelect: { withAnimation(.spring(response: 0.3)) { selected = i } },
                                onDelete: { withAnimation(.spring) { deleteBand(i) } })
                            .transition(.asymmetric(insertion: .scale(scale: 0.95).combined(with: .opacity), removal: .opacity))
                    }
                    if bands.isEmpty {
                        Text(L("No bands yet. Click Add Band or double-click the curve."))
                            .foregroundStyle(.secondary).padding()
                    }
                }
            case .graphic(let points):
                Card {
                    GraphicEditor(points: Binding(get: { points }, set: { profile.eq = .graphic($0) }))
                }
            }

            Card(title: L("Output"), icon: "speaker.wave.3") {
                VStack(alignment: .leading, spacing: 8) {
                    Toggle(L("Auto preamp"), isOn: Binding(
                        get: { profile.preamp == .auto },
                        set: { profile.preamp = $0 ? .auto : .manual(0) }))
                    if case .manual(let db) = profile.preamp {
                        HStack {
                            Text(L("Preamp")).frame(width: 70, alignment: .leading)
                            GlowSlider(value: Binding(get: { db }, set: { profile.preamp = .manual(($0 * 10).rounded() / 10) }), range: -24...6)
                            Text(String(format: "%+.1f dB", db)).font(.callout.monospacedDigit()).frame(width: 64)
                        }
                    }
                    Toggle(L("Limiter (prevents clipping at -0.1 dBFS)"), isOn: $profile.limiter)
                    HStack {
                        Text(L("Balance")).frame(width: 70, alignment: .leading)
                        Text(L("L")).font(.caption)
                        GlowSlider(value: Binding(get: { profile.balance }, set: { profile.balance = abs($0) < 0.03 ? 0 : $0 }), range: -1...1)
                        Text(L("R")).font(.caption)
                    }
                    HStack {
                        Text(L("Loudness")).frame(width: 70, alignment: .leading)
                        GlowSlider(value: $profile.loudness, range: 0...1, bipolar: false)
                        Text(profile.loudness == 0 ? "off" : "\(Int(profile.loudness * 100))%").font(.caption).frame(width: 38)
                    }
                }
            }

            if !profile.correction.isEmpty {
                Card(title: L("Correction · %@ · before your bands", profile.correctionSource?.name ?? L("imported")), icon: "checkmark.seal") {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(profile.correction.enumerated()), id: \.offset) { _, b in
                            Text("\(b.kind.label)  \(fmtHz(b.freq))  \(String(format: "%+.1f dB  Q %.2f", b.gainDb, b.q))")
                                .font(.caption.monospaced()).foregroundStyle(b.enabled ? .primary : .secondary)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    private func bandBinding(_ i: Int) -> Binding<Band> {
        Binding(
            get: { if case .parametric(let b) = profile.eq, i < b.count { return b[i] }; return Band(kind: .peak, freq: 1000, gainDb: 0, q: 1) },
            set: { nb in if case .parametric(var b) = profile.eq, i < b.count { b[i] = nb; profile.eq = .parametric(b) } })
    }

    private func addBand() {
        guard case .parametric(var b) = profile.eq, let band = c.suggestBand(profile.eq) else { return }
        b.append(band)
        profile.eq = .parametric(b)
        selected = b.count - 1
    }

    private func deleteBand(_ i: Int) {
        guard case .parametric(var b) = profile.eq, i < b.count else { return }
        b.remove(at: i)
        profile.eq = .parametric(b)
        selected = nil
    }

    /// Switching layouts keeps the sound: the current EQ curve is sampled at
    /// the graphic frequencies, and graphic sliders become peaking bands.
    /// Switch layout keeping the sound. The core samples the curve and keeps the
    /// way back to the exact original bands (same logic on every platform).
    // parity: eq-layout-convert
    private func switchMode(_ m: Mode) {
        guard m != mode else { return }
        let to = m == .parametric ? "parametric" : m == .g10 ? "graphic10" : "graphic31"
        if let eq = c.convertEq(profile.eq, to: to, for: profile.id) { profile.eq = eq }
        selected = nil
    }
}

struct BandRow: View {
    @EnvironmentObject var theme: ThemeStore
    let index: Int
    @Binding var band: Band
    let selected: Bool
    let color: Color
    let onSelect: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text("\(index + 1)").font(.caption.weight(.heavy)).foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(Circle().fill(band.enabled ? color : .gray))
                .shadow(color: color.opacity(selected ? 0.8 : 0), radius: 6)
            Toggle("", isOn: $band.enabled).labelsHidden()
            Picker("", selection: $band.kind) {
                ForEach(FilterKind.allCases) { Text($0.label).tag($0) }
            }.labelsHidden().frame(width: 110)

            Text(fmtHz(band.freq)).font(.callout.monospacedDigit()).frame(width: 72, alignment: .trailing)
            Slider(value: Binding(get: { log10(band.freq) },
                                  set: { band.freq = (pow(10, $0) * 10).rounded() / 10 }), in: log10(20)...log10(20000))
                .frame(minWidth: 90)

            Text(band.kind.hasGain ? String(format: "%+.1f dB", band.gainDb) : "—")
                .font(.callout.monospacedDigit()).frame(width: 62, alignment: .trailing)
            Slider(value: Binding(get: { band.gainDb }, set: { band.gainDb = ($0 * 10).rounded() / 10 }), in: -24...24)
                .disabled(!band.kind.hasGain).frame(minWidth: 80)

            Text(String(format: "Q %.2f", band.q)).font(.callout.monospacedDigit()).frame(width: 56, alignment: .trailing)
            Slider(value: Binding(get: { log10(band.q) },
                                  set: { band.q = (pow(10, $0) * 100).rounded() / 100 }), in: log10(0.1)...log10(30))
                .frame(minWidth: 60)

            Button(role: .destructive, action: onDelete) { Image(systemName: "trash") }.buttonStyle(.borderless)
        }
        .padding(.vertical, 7).padding(.horizontal, 10)
        .background(GlassBackground(radius: 11, highlighted: selected))
        .scaleEffect(selected ? 1.0 : 0.995)
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
    }
}

struct GraphicEditor: View {
    @Binding var points: [GraphicPoint]

    var body: some View {
        ScrollView(.horizontal) {
            HStack(alignment: .bottom, spacing: points.count > 10 ? 2 : 10) {
                ForEach(points.indices, id: \.self) { i in
                    VStack(spacing: 4) {
                        Text(String(format: "%+.1f", points[i].gainDb)).font(.system(size: 9).monospacedDigit())
                        Slider(value: Binding(get: { points[i].gainDb },
                                              set: { points[i].gainDb = ($0 * 2).rounded() / 2 }), in: -12...12)
                            .rotationEffect(.degrees(-90))
                            .frame(width: 150)
                            .frame(width: 26, height: 150)
                        Text(points[i].freq >= 1000 ? String(format: "%gk", points[i].freq / 1000) : String(format: "%g", points[i].freq))
                            .font(.system(size: 9))
                    }
                    .onTapGesture(count: 2) { points[i].gainDb = 0 }
                }
            }.padding(.vertical, 6)
        }
    }
}
