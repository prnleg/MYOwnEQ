import AppKit
import SwiftUI

/// A profile as a share code (core: `share_code`).
struct ShareInfo: Identifiable {
    let id = UUID()
    let name: String
    let code: String
    /// QR modules, rows of "1" (dark) / "0".
    let qr: [String]
}

/// Code + QR + Copy. Anyone can paste the code into MyOwnEQ to get the same EQ.
// parity: share-codes
struct ShareSheet: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    let info: ShareInfo

    var body: some View {
        VStack(spacing: 16) {
            VStack(spacing: 4) {
                Text(L("Share “%@”", info.name)).font(.title3.weight(.semibold))
                Text(L("Anyone with MyOwnEQ can paste this code to get the same EQ. No account, no server."))
                    .font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            QRView(rows: info.qr)
                .frame(width: 200, height: 200)
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 12).fill(.white))
            Text(info.code)
                .font(.system(.callout, design: .monospaced))
                .textSelection(.enabled)
                .lineLimit(4)
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 8).fill(theme.current.ink.opacity(0.06)))
            HStack {
                Button(L("Done")) { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(info.code, forType: .string)
                    c.notify(L("Share code copied"))
                    dismiss()
                } label: { Label(L("Copy code"), systemImage: "doc.on.doc") }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(GlowButtonStyle())
            }
        }
        .padding(24)
        .frame(width: 420)
    }
}

/// Draws QR modules (always black on white, so phones can read it in any theme).
struct QRView: View {
    let rows: [String]

    var body: some View {
        Canvas { ctx, size in
            let n = rows.count
            guard n > 0 else { return }
            let cell = min(size.width, size.height) / CGFloat(n)
            for (y, row) in rows.enumerated() {
                for (x, ch) in row.enumerated() where ch == "1" {
                    ctx.fill(Path(CGRect(x: CGFloat(x) * cell, y: CGFloat(y) * cell, width: cell + 0.5, height: cell + 0.5)),
                             with: .color(.black))
                }
            }
        }
        .accessibilityLabel(L("QR code"))
    }
}
