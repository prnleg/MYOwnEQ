import SwiftUI
import UniformTypeIdentifiers

/// Make or change a theme inside the app. The draft is shown live everywhere
/// (ThemeStore.preview); Cancel puts the saved theme back.
// parity: theme-editor
struct ThemeEditor: View {
    @EnvironmentObject var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    @State var draft: Theme
    /// Editing an existing user theme (else: a new one is created on Save).
    let existing: Bool
    @State private var sharing: ShareInfo?
    @State private var confirmDelete = false

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text(existing ? L("Edit theme") : L("New theme")).font(.title3.weight(.semibold))
                    ThemeCard(t: draft, selected: false).frame(height: 116)

                    HStack(spacing: 12) {
                        TextField(L("Name"), text: $draft.name).textFieldStyle(.roundedBorder)
                        TextField(L("Author (optional)"), text: Binding(get: { draft.author ?? "" },
                                                                         set: { draft.author = $0.isEmpty ? nil : $0 }))
                            .textFieldStyle(.roundedBorder)
                    }
                    PillPicker(selection: $draft.dark, options: [(true, L("Dark")), (false, L("Light"))])

                    section(L("Colors")) {
                        HStack(spacing: 18) {
                            swatch(L("Accent"), \.accent)
                            swatch(L("Cards"), Binding(get: { draft.panel }, set: { draft.panel = $0 }))
                            swatch(L("Background"), Binding(get: { draft.background.base }, set: { draft.background.base = $0 }))
                            Spacer()
                        }
                        colorList(L("EQ curve (lows → highs)"), $draft.curve, min: 2, max: 6)
                        colorList(L("Live spectrum (top → bottom)"), $draft.spectrum, min: 2, max: 2)
                    }

                    section(L("Background")) {
                        colorList(L("Floating color spots"), $draft.background.blobs, min: 0, max: 6)
                        slider(L("Spots strength"), $draft.background.intensity, 0...1)
                        slider(L("Movement speed"), $draft.background.speed, 0...3)
                        slider(L("Glow around the curve"), $draft.glow, 0...1)
                    }

                    section(L("Background image")) {
                        HStack {
                            Button(draft.background.image == nil ? L("Choose image…") : L("Change image…")) { chooseImage() }
                            if draft.background.image != nil {
                                Button(L("Remove")) { draft.background.image = nil }
                            }
                            Spacer()
                        }
                        if draft.background.image != nil {
                            slider(L("Image strength"), $draft.background.imageOpacity, 0...1)
                            slider(L("Blur"), $draft.background.imageBlur, 0...40)
                            Text(L("Lower strength or more blur keeps text readable. The image isn't included in share codes."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                .padding(22)
            }
            Divider()
            HStack {
                if existing {
                    Button(L("Delete"), role: .destructive) { confirmDelete = true }
                    Button(L("Share…")) { sharing = theme.shareInfo(draft) }
                }
                Spacer()
                Button(L("Cancel")) {
                    theme.endPreview()
                    dismiss()
                }.keyboardShortcut(.cancelAction)
                Button(L("Save")) {
                    var t = draft
                    if t.name.trimmingCharacters(in: .whitespaces).isEmpty { t.name = L("My theme") }
                    if theme.save(t) != nil { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(GlowButtonStyle())
            }
            .padding(14)
        }
        .frame(width: 600, height: 680)
        .onAppear { theme.preview(draft) }
        .onDisappear { theme.endPreview() }
        .onChange(of: draft) { _, t in theme.preview(t) }
        .sheet(item: $sharing) { ShareSheet(info: $0) }
        .confirmationDialog(L("Delete “%@”?", draft.name), isPresented: $confirmDelete) {
            Button(L("Delete"), role: .destructive) {
                theme.delete(draft)
                dismiss()
            }
        }
    }

    private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title.uppercased()).font(.caption.weight(.bold)).foregroundStyle(.secondary)
            content()
        }
    }

    private func swatch(_ label: String, _ kp: WritableKeyPath<Theme, String>) -> some View {
        swatch(label, Binding(get: { draft[keyPath: kp] }, set: { draft[keyPath: kp] = $0 }))
    }

    private func swatch(_ label: String, _ hex: Binding<String>) -> some View {
        HStack(spacing: 6) {
            ColorPicker("", selection: Binding(get: { Color(hex: hex.wrappedValue) }, set: { hex.wrappedValue = $0.hex }))
                .labelsHidden()
            Text(label).font(.callout)
        }
    }

    private func colorList(_ label: String, _ list: Binding<[String]>, min: Int, max: Int) -> some View {
        HStack(spacing: 8) {
            Text(label).font(.callout).frame(width: 190, alignment: .leading)
            ForEach(list.wrappedValue.indices, id: \.self) { i in
                ColorPicker("", selection: Binding(
                    get: { i < list.wrappedValue.count ? Color(hex: list.wrappedValue[i]) : .clear },
                    set: { if i < list.wrappedValue.count { list.wrappedValue[i] = $0.hex } }))
                    .labelsHidden()
            }
            if max > min {
                Button { list.wrappedValue.removeLast() } label: { Image(systemName: "minus") }
                    .disabled(list.wrappedValue.count <= min)
                    .help(L("Remove a color"))
                Button { list.wrappedValue.append(list.wrappedValue.last ?? draft.accent) } label: { Image(systemName: "plus") }
                    .disabled(list.wrappedValue.count >= max)
                    .help(L("Add a color"))
            }
            Spacer()
        }
        .buttonStyle(.borderless)
    }

    private func slider(_ label: String, _ v: Binding<Double>, _ range: ClosedRange<Double>) -> some View {
        HStack {
            Text(label).font(.callout).frame(width: 190, alignment: .leading)
            Slider(value: v, in: range)
            Text(String(format: range.upperBound > 3 ? "%.0f" : "%.2f", v.wrappedValue))
                .font(.caption.monospacedDigit()).frame(width: 40, alignment: .trailing)
        }
    }

    private func chooseImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .heic, .tiff, .image]
        panel.message = L("Choose a background image")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if let name = theme.importBackgroundImage(url) {
            draft.background.image = name
            if draft.background.imageOpacity >= 0.99 { draft.background.imageOpacity = draft.dark ? 0.55 : 0.7 }
        }
    }
}
