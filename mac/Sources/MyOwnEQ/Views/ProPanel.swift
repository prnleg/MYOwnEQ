import SwiftUI

private let graphic10: [Double] = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]
private let graphic31: [Double] = [20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
                                   1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000]

struct ProPanel: View {
    @Binding var profile: Profile
    @Binding var selected: Int?

    private enum Mode: String, CaseIterable { case parametric = "Parametric", g10 = "Graphic 10", g31 = "Graphic 31" }

    private var mode: Mode {
        switch profile.eq {
        case .parametric: .parametric
        case .graphic(let p): p.count > 10 ? .g31 : .g10
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Picker("EQ", selection: Binding(get: { mode }, set: switchMode)) {
                    ForEach(Mode.allCases, id: \.self) { Text($0.rawValue) }
                }.pickerStyle(.segmented).frame(maxWidth: 360)
                Spacer()
                if case .parametric = profile.eq {
                    Button { addBand() } label: { Label("Add Band", systemImage: "plus") }
                }
            }

            switch profile.eq {
            case .parametric(let bands):
                VStack(spacing: 4) {
                    ForEach(Array(bands.enumerated()), id: \.element.id) { i, _ in
                        BandRow(index: i, band: bandBinding(i), selected: selected == i,
                                onSelect: { selected = i }, onDelete: { deleteBand(i) })
                    }
                    if bands.isEmpty {
                        Text("No bands yet. Click Add Band, then drag the dots on the curve.")
                            .foregroundStyle(.secondary).padding()
                    }
                }
            case .graphic(let points):
                GraphicEditor(points: Binding(get: { points }, set: { profile.eq = .graphic($0) }))
            }

            GroupBox("Output") {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Toggle("Auto preamp", isOn: Binding(
                            get: { profile.preamp == .auto },
                            set: { profile.preamp = $0 ? .auto : .manual(0) }))
                        if case .manual(let db) = profile.preamp {
                            Slider(value: Binding(get: { db }, set: { profile.preamp = .manual(($0 * 10).rounded() / 10) }), in: -24...6)
                            Text(String(format: "%+.1f dB", db)).font(.callout.monospacedDigit()).frame(width: 64)
                        } else {
                            Spacer()
                        }
                    }
                    Toggle("Limiter (prevents clipping at -0.1 dBFS)", isOn: $profile.limiter)
                    HStack {
                        Text("Balance").frame(width: 70, alignment: .leading)
                        Text("L").font(.caption)
                        Slider(value: Binding(get: { profile.balance }, set: { profile.balance = abs($0) < 0.03 ? 0 : $0 }), in: -1...1)
                        Text("R").font(.caption)
                    }
                    HStack {
                        Text("Loudness").frame(width: 70, alignment: .leading)
                        Slider(value: $profile.loudness, in: 0...1)
                        Text(profile.loudness == 0 ? "off" : "\(Int(profile.loudness * 100))%").font(.caption).frame(width: 38)
                    }
                }.padding(6)
            }

            if !profile.correction.isEmpty {
                GroupBox("Correction (\(profile.correctionSource?.name ?? "imported")), applied before your bands") {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(profile.correction.enumerated()), id: \.offset) { _, b in
                            Text("\(b.kind.label)  \(fmtHz(b.freq))  \(String(format: "%+.1f dB  Q %.2f", b.gainDb, b.q))")
                                .font(.caption.monospaced()).foregroundStyle(b.enabled ? .primary : .secondary)
                        }
                    }.padding(6).frame(maxWidth: .infinity, alignment: .leading)
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
        guard case .parametric(var b) = profile.eq else { return }
        // Put the new band in the widest gap between existing ones.
        let fs = ([20.0] + b.map(\.freq).sorted() + [20000.0])
        var best = 1000.0, gap = 0.0
        for (a, c) in zip(fs, fs.dropFirst()) where log(c / a) > gap { gap = log(c / a); best = sqrt(a * c) }
        b.append(Band(kind: .peak, freq: best.rounded(), gainDb: 0, q: 1.0))
        profile.eq = .parametric(b)
        selected = b.count - 1
    }

    private func deleteBand(_ i: Int) {
        guard case .parametric(var b) = profile.eq, i < b.count else { return }
        b.remove(at: i)
        profile.eq = .parametric(b)
        selected = nil
    }

    private func switchMode(_ m: Mode) {
        guard m != mode else { return }
        switch m {
        case .parametric:
            // Keep the sound: graphic points become peaking bands.
            if case .graphic(let pts) = profile.eq {
                profile.eq = .parametric(pts.filter { $0.gainDb != 0 }.map { Band(kind: .peak, freq: $0.freq, gainDb: $0.gainDb, q: pts.count > 10 ? 4.3 : 1.41) })
            }
        case .g10: profile.eq = .graphic(graphic10.map { GraphicPoint(freq: $0, gainDb: 0) })
        case .g31: profile.eq = .graphic(graphic31.map { GraphicPoint(freq: $0, gainDb: 0) })
        }
        selected = nil
    }
}

struct BandRow: View {
    let index: Int
    @Binding var band: Band
    let selected: Bool
    let onSelect: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text("\(index + 1)").font(.caption.bold()).frame(width: 18)
                .foregroundStyle(selected ? .orange : .secondary)
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
        .padding(.vertical, 4).padding(.horizontal, 6)
        .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Color.orange.opacity(0.12) : .clear))
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
