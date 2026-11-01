import SwiftUI

/// Settings for the current output device's group: name, default profile,
/// favorites and app rules ("CS2 running → FPS").
struct DeviceSettings: View {
    @EnvironmentObject var c: AppController
    @State private var apps: [AppWatcher.App] = []

    var body: some View {
        if let g = c.state?.currentGroup, let s = c.state {
            Form {
                Section("Device") {
                    TextField("Name", text: Binding(get: { g.name }, set: { var n = g; n.name = $0; c.updateGroup(n) }))
                    Toggle("EQ enabled for this device", isOn: Binding(get: { g.enabled }, set: { var n = g; n.enabled = $0; c.updateGroup(n) }))
                    Picker("Default profile", selection: Binding(get: { g.defaultProfile }, set: { var n = g; n.defaultProfile = $0; c.updateGroup(n) })) {
                        Text("None (flat)").tag(ID?.none)
                        ForEach(s.library.profiles) { Text($0.name).tag(Optional($0.id)) }
                    }
                }

                Section {
                    ForEach(g.members, id: \.key) { m in
                        HStack {
                            Image(systemName: m.key.machine == s.machine ? "laptopcomputer" : "desktopcomputer")
                            Text(m.osName)
                            Text(m.key.machine == s.machine ? "this Mac" : m.key.machine)
                                .font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            if g.members.count > 1 {
                                Button("Unlink") { c.unlink(m.key) }.controlSize(.small)
                            }
                        }
                    }
                    let others = s.library.groups.filter { o in
                        o.id != g.id && !o.members.isEmpty && o.members.allSatisfy { $0.key.machine != s.machine }
                    }
                    if !others.isEmpty {
                        Menu("Link with a device from another machine…") {
                            ForEach(others) { o in
                                Button("\(o.name) (\(o.members.first?.key.machine ?? ""))") { c.link(o.id) }
                            }
                        }
                    }
                } header: {
                    Text("Same headphones on other machines")
                } footer: {
                    Text("Linked devices share default profile, favorites, rules and the selected profile. Pick a profile here and the other machine switches too.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section("Favorites (shown in the menu bar while this device is connected)") {
                    ForEach(s.library.profiles) { p in
                        Toggle(p.name, isOn: Binding(get: { g.favorites.contains(p.id) }, set: { _ in c.toggleFavorite(p.id) }))
                    }
                }

                Section {
                    ForEach(s.library.rules.filter { $0.group == g.id }) { r in
                        RuleRow(rule: r, profiles: s.library.profiles)
                    }
                    Menu("Add rule for app…") {
                        ForEach(apps, id: \.bundleID) { a in
                            Button(a.name) { addRule(a, group: g.id, fallback: s.library.profiles.first?.id) }
                        }
                    }
                } header: {
                    Text("App rules")
                } footer: {
                    Text("When the app runs (or is focused) and this device is the output, its profile takes over. When it quits, the device goes back to its selected/default profile.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)
            .onAppear { apps = AppWatcher.pickableApps() }
        } else {
            ContentUnavailableView("No output device", systemImage: "speaker.slash")
        }
    }

    private func addRule(_ a: AppWatcher.App, group: ID, fallback: ID?) {
        guard let p = fallback else { return }
        c.upsertRule(Rule(id: newID(), group: group, app: AppMatch(name: a.name, macBundleId: a.bundleID), profile: p))
    }
}

struct RuleRow: View {
    @EnvironmentObject var c: AppController
    let rule: Rule
    let profiles: [Profile]

    var body: some View {
        HStack {
            Toggle("", isOn: bind(\.enabled)).labelsHidden()
            VStack(alignment: .leading, spacing: 0) {
                Text(rule.app.name)
                Text(rule.app.macBundleId ?? "").font(.caption2).foregroundStyle(.secondary)
            }
            Spacer()
            Picker("", selection: bind(\.trigger)) {
                Text("while running").tag(Trigger.running)
                Text("while focused").tag(Trigger.focused)
            }.labelsHidden().fixedSize()
            Image(systemName: "arrow.right").foregroundStyle(.secondary)
            Picker("", selection: bind(\.profile)) {
                ForEach(profiles) { Text($0.name).tag($0.id) }
            }.labelsHidden().frame(width: 150)
            Button(role: .destructive) { c.deleteRule(rule.id) } label: { Image(systemName: "trash") }
                .buttonStyle(.borderless)
        }
    }

    private func bind<T>(_ kp: WritableKeyPath<Rule, T>) -> Binding<T> {
        Binding(get: { rule[keyPath: kp] }, set: { var r = rule; r[keyPath: kp] = $0; c.upsertRule(r) })
    }
}
