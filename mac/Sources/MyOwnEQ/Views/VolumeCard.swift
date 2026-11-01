import SwiftUI

/// System volume of the current output + per-device boost (up to +12 dB).
/// The boost always runs through the lookahead limiter, so it gets louder
/// but never clips.
// parity: volume
// parity: boost
struct VolumeCard: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    var compact = true
    @State private var boostDraft: Double?
    @State private var lastSent = Date.distantPast

    var body: some View {
        let t = theme.current
        let boost = boostDraft ?? c.state?.currentGroup?.boostDb ?? 0
        VStack(alignment: .leading, spacing: 10) {
            if let v = c.volume, c.volumeSettable {
                HStack(spacing: 10) {
                    Image(systemName: v == 0 ? "speaker.slash.fill" : v < 0.34 ? "speaker.wave.1.fill" : v < 0.67 ? "speaker.wave.2.fill" : "speaker.wave.3.fill")
                        .frame(width: 22)
                        .foregroundStyle(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                        .contentTransition(.symbolEffect(.replace))
                    GlowSlider(value: Binding(get: { v }, set: { c.setVolume($0) }), range: 0...1, bipolar: false)
                    Text("\(Int((v * 100).rounded()))%")
                        .font(.caption.monospacedDigit().weight(.medium))
                        .contentTransition(.numericText(value: v))
                        .frame(width: 38, alignment: .trailing)
                }
            } else {
                Label(L("This output has no software volume. Use the device's own controls."), systemImage: "speaker.badge.exclamationmark")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 10) {
                Image(systemName: boost > 0 ? "bolt.fill" : "bolt")
                    .frame(width: 22)
                    .foregroundStyle(boost > 0 ? AnyShapeStyle(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                                               : AnyShapeStyle(.secondary))
                    .symbolEffect(.bounce, value: boost > 0)
                    .help(L("Boost"))
                GlowSlider(value: Binding(get: { boost }, set: { v in
                    boostDraft = v
                    // Live, but at most ~10 writes per second while dragging.
                    if Date().timeIntervalSince(lastSent) > 0.1 {
                        lastSent = Date()
                        c.setBoost(v)
                    }
                }), range: 0...12, bipolar: false, step: 0.5) { editing in
                    if !editing, let d = boostDraft {
                        c.setBoost(d)
                        boostDraft = nil
                    }
                }
                Text(boost == 0 ? L("off") : String(format: "+%.1f dB", boost))
                    .font(.caption.monospacedDigit().weight(.medium))
                    .frame(width: 52, alignment: .trailing)
            }
            if boost > 0 {
                Label(compact ? L("Boost on · limiter keeps it from clipping")
                              : L("Boost on: the limiter is forced on, so it gets louder without ever clipping. Very high boost squashes loud parts."),
                      systemImage: "checkmark.shield.fill")
                    .font(.caption2).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
        .animation(.spring(response: 0.3, dampingFraction: 0.8), value: boost > 0)
    }
}
