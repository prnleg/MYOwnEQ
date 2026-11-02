import ServiceManagement
import SwiftUI

struct AppearanceSettings: View {
    @EnvironmentObject var theme: ThemeStore
    @EnvironmentObject var lang: Lang
    @EnvironmentObject var c: AppController
    @AppStorage("autoSave") private var autoSave = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("Preferences"), subtitle: L("Editing, language, themes, logo and motion"), icon: "gearshape.fill")

                Card(title: L("Editing"), icon: "square.and.pencil") {
                    Toggle(L("Auto-save profile changes"), isOn: $autoSave).toggleStyle(.switch)
                    Text(autoSave ? L("Every change is saved as you go.")
                                  : L("Changes wait for Save (⌘S). You'll be asked before leaving a profile with unsaved changes."))
                        .font(.caption).foregroundStyle(.secondary)
                    Text(L("Editing a preset always creates a copy, so presets never change."))
                        .font(.caption).foregroundStyle(.secondary)
                }

                Card(title: L("Language"), icon: "globe") {
                    HStack {
                        Picker(L("Language"), selection: Binding(get: { lang.choice ?? "" }, set: { lang.select($0.isEmpty ? nil : $0) })) {
                            Text(L("Same as the system")).tag("")
                            Divider()
                            ForEach(lang.packs) { p in Text(p.name).tag(p.code) }
                        }
                        .labelsHidden().frame(maxWidth: 260)
                        Spacer()
                        Button(L("Open Languages Folder")) { lang.openUserFolder() }.buttonStyle(GlowButtonStyle(prominent: false))
                        Button(L("Reload")) { lang.reload() }.buttonStyle(GlowButtonStyle(prominent: false))
                    }
                    HStack {
                        Text(L("Missing your language? Translate one file and send a PR.")).font(.caption).foregroundStyle(.secondary)
                        Link(L("How to translate"), destination: URL(string: "https://github.com/prnleg/MYOwnEQ/tree/main/lang")!)
                            .font(.caption)
                    }
                    ForEach(lang.loadErrors, id: \.self) { e in
                        Label(e, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
                    }
                }

                Card(title: L("Theme"), icon: "swatchpalette") {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 12)], spacing: 12) {
                        ForEach(theme.themes) { t in
                            ThemeCard(t: t, selected: t.id == theme.current.id)
                                .onTapGesture { withAnimation(.easeInOut(duration: 0.45)) { theme.select(t.id) } }
                        }
                    }
                    HStack {
                        Button(L("Open Themes Folder")) { theme.openUserFolder() }.buttonStyle(GlowButtonStyle(prominent: false))
                        Button(L("Reload")) { withAnimation { theme.reload() } }.buttonStyle(GlowButtonStyle(prominent: false))
                        Spacer()
                        Link(L("How to make a theme"), destination: URL(string: "https://github.com/prnleg/MYOwnEQ/tree/main/themes")!)
                            .font(.callout)
                    }
                    ForEach(theme.loadErrors, id: \.self) { e in
                        Label(e, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
                    }
                }

                Card(title: L("Logo"), icon: "app.badge") {
                    HStack(spacing: 18) {
                        ForEach(LogoStyle.allCases) { s in
                            let on = theme.logo == s
                            VStack(spacing: 8) {
                                AppLogo(style: s)
                                    .frame(width: 74, height: 74)
                                    .scaleEffect(on ? 1.06 : 1)
                                    .shadow(color: on ? theme.current.accentColor.opacity(0.6) : .clear, radius: 12)
                                HStack(spacing: 4) {
                                    if s != .custom || theme.customLogo != nil {
                                        Image(nsImage: LogoRenderer.menuBarImage(s, enabled: true, custom: theme.customLogo,
                                                                                 template: theme.customLogoTemplate))
                                    }
                                    Text(s.label).font(.callout.weight(on ? .bold : .regular))
                                }
                            }
                            .padding(10)
                            .background(GlassBackground(radius: 14, highlighted: on).opacity(on ? 1 : 0.6))
                            .onTapGesture {
                                if s == .custom && theme.customLogo == nil { theme.chooseCustomLogo(); return }
                                withAnimation(.spring(response: 0.35, dampingFraction: 0.6)) { theme.logo = s }
                            }
                        }
                        Spacer()
                    }
                    if theme.customLogo != nil {
                        HStack {
                            Button(L("Change Image…")) { theme.chooseCustomLogo() }.buttonStyle(GlowButtonStyle(prominent: false))
                            Button(L("Remove")) { withAnimation { theme.removeCustomLogo() } }.buttonStyle(GlowButtonStyle(prominent: false))
                            Toggle(L("Monochrome in the menu bar"), isOn: $theme.customLogoTemplate).toggleStyle(.switch).controlSize(.small)
                        }
                    }
                    Text(L("Changes the menu bar icon and the logo inside the app. The Finder/Dock icon is the wave."))
                        .font(.caption).foregroundStyle(.secondary)
                }

                // parity: motion-settings
                Card(title: L("Motion"), icon: "sparkles") {
                    Toggle(L("Smooth animations (springy curve, morphing, ripples)"), isOn: $theme.motion)
                    Toggle(L("React to music (glow and background pulse)"), isOn: $theme.reactive)
                    Toggle(L("Shimmer along the curve"), isOn: $theme.shimmer).disabled(!theme.motion)
                    if NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
                        Label(L("Reduce Motion is on in System Settings, so animations stay off."), systemImage: "info.circle")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)

                // parity: start-with-system
                Card(title: L("System"), icon: "power") {
                    LoginItemToggle()
                }

                // parity: diagnostics
                Card(title: L("Help"), icon: "questionmark.circle") {
                    HStack(spacing: 8) {
                        Button {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(c.diagnostics(), forType: .string)
                            c.notify(L("Diagnostics copied. Paste them in the bug report."))
                        } label: { Label(L("Copy diagnostics"), systemImage: "doc.on.doc") }
                        Button(L("Report a bug")) {
                            if let url = URL(string: "https://github.com/prnleg/MYOwnEQ/issues/new?template=bug.yml") {
                                NSWorkspace.shared.open(url)
                            }
                        }
                    }
                    Text(L("Diagnostics has versions, outputs, audio status and recent log lines. No passwords or sync codes. Check it before posting."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(20)
        }
    }
}

/// Preview tile: the theme's background, blobs and a sample curve.
struct ThemeCard: View {
    @EnvironmentObject var theme: ThemeStore
    let t: Theme
    let selected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Canvas { ctx, size in
                ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(t.baseColor))
                for (i, col) in t.blobColors.enumerated() {
                    let cx = size.width * [0.2, 0.8, 0.55, 0.1][i % 4]
                    let cy = size.height * [0.3, 0.2, 0.9, 0.85][i % 4]
                    let r = size.width * 0.55
                    ctx.fill(Path(ellipseIn: CGRect(x: cx - r, y: cy - r, width: 2 * r, height: 2 * r)), with: .radialGradient(
                        Gradient(colors: [col.opacity(t.background.intensity), col.opacity(0)]),
                        center: CGPoint(x: cx, y: cy), startRadius: 0, endRadius: r))
                }
                var p = Path()
                for i in 0...48 {
                    let x = Double(i) / 48
                    let pt = CGPoint(x: size.width * (0.06 + 0.88 * x), y: size.height * (0.55 - 0.32 * LogoMark.waveY(x)))
                    if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
                }
                let g = GraphicsContext.Shading.linearGradient(t.curveGradient, startPoint: .zero, endPoint: CGPoint(x: size.width, y: 0))
                ctx.drawLayer { l in
                    l.addFilter(.blur(radius: 4))
                    l.opacity = t.glow
                    l.stroke(p, with: g, style: StrokeStyle(lineWidth: 6, lineCap: .round))
                }
                ctx.stroke(p, with: g, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
            }
            .frame(height: 74)
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text(t.name).font(.callout.weight(.semibold)).foregroundStyle(t.ink)
                    Text(t.author ?? "").font(.caption2).foregroundStyle(t.ink.opacity(0.55))
                }
                Spacer()
                if selected {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(t.accentColor).transition(.scale.combined(with: .opacity))
                }
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(t.panelColor)
        }
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
            .strokeBorder(selected ? AnyShapeStyle(LinearGradient(gradient: t.curveGradient, startPoint: .leading, endPoint: .trailing))
                                   : AnyShapeStyle(Color.primary.opacity(0.12)), lineWidth: selected ? 2.5 : 1))
        .shadow(color: selected ? t.accentColor.opacity(0.45) : .black.opacity(0.15), radius: selected ? 10 : 4, y: 3)
        .scaleEffect(selected ? 1.02 : 1)
        .contentShape(Rectangle())
    }
}

/// "Open at login" via the system's login items (SMAppService).
struct LoginItemToggle: View {
    @State private var on = SMAppService.mainApp.status == .enabled
    @State private var error: String?

    var body: some View {
        Toggle(L("Open MyOwnEQ at login"), isOn: Binding(get: { on }, set: { want in
            do {
                if want { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                error = nil
            } catch {
                self.error = error.localizedDescription
            }
            on = SMAppService.mainApp.status == .enabled
        }))
        .toggleStyle(.switch)
        if let error {
            Label(error, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
        }
    }
}
