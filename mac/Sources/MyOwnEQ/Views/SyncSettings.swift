import SwiftUI

/// Sync setup: off / connect to a server / host on this Mac.
struct SyncSettings: View {
    @EnvironmentObject var c: AppController
    @State private var mode = "off"
    @State private var url = ""
    @State private var token = ""
    @State private var port = 7878

    var body: some View {
        let info = c.state?.sync
        Form {
            Section {
                HStack {
                    Circle().fill(color(info?.status)).frame(width: 9, height: 9)
                    Text(label(info)).font(.headline)
                    Spacer()
                    if let p = info?.pending, p > 0 {
                        Text("\(p) change\(p == 1 ? "" : "s") waiting").font(.caption).foregroundStyle(.secondary)
                    }
                }
                if let d = info?.detail, !d.isEmpty {
                    Text(d).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }

            Section("Mode") {
                Picker("Sync", selection: $mode) {
                    Text("Off").tag("off")
                    Text("Connect to a server").tag("server")
                    Text("Host on this Mac (LAN)").tag("host")
                }.pickerStyle(.radioGroup)
            }

            switch mode {
            case "server":
                Section {
                    TextField("Address", text: $url, prompt: Text("192.168.0.10:7878 or https://eq.example.com"))
                    SecureField("Token / code", text: $token)
                    HStack {
                        Button(c.discovering ? "Searching…" : "Find on my network") { c.discover() }
                            .disabled(c.discovering)
                        Spacer()
                        Button("Connect") { c.configureSync(mode: "server", url: url, token: token) }
                            .keyboardShortcut(.defaultAction)
                            .disabled(url.isEmpty || token.isEmpty)
                    }
                    ForEach(c.discovered, id: \.self) { h in
                        Button { url = h.addr } label: {
                            Label("\(h.name) — \(h.addr)", systemImage: "desktopcomputer")
                        }.buttonStyle(.link)
                    }
                } header: {
                    Text("Server")
                } footer: {
                    Text("Your Docker server (see server/docker-compose.yml), any cloud host running it, or another machine hosting on the LAN.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            case "host":
                Section {
                    Stepper("Port \(port)", value: $port, in: 1024...65535)
                    if info?.mode == "host", let code = info?.token {
                        LabeledContent("Code") {
                            Text(code).font(.title2.monospaced().bold()).textSelection(.enabled)
                        }
                        LabeledContent("Address") {
                            VStack(alignment: .trailing) {
                                ForEach(info?.hostAddresses ?? [], id: \.self) { Text($0).monospaced().textSelection(.enabled) }
                            }
                        }
                        Button("New code") { c.configureSync(mode: "host", port: port) }
                    } else {
                        Button("Start hosting") { c.configureSync(mode: "host", port: port) }
                            .keyboardShortcut(.defaultAction)
                    }
                } header: {
                    Text("LAN host")
                } footer: {
                    Text("On the other machine choose \"Connect to a server\", click Find on my network (or type the address) and enter the code. Syncs only while this Mac is on.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            default:
                Section {
                    Button("Turn sync off") { c.configureSync(mode: "off") }
                        .disabled(info?.mode == "off")
                }
            }

            Section("Updates") {
                Toggle("Download updates automatically, install on restart",
                       isOn: Binding(get: { c.update?.auto ?? true }, set: { c.setAutoUpdate($0) }))
                HStack {
                    Text(c.update?.label ?? "").font(.callout).foregroundStyle(.secondary)
                    Spacer()
                    if c.update?.isReady == true {
                        Button("Restart now") { c.installUpdateAndQuit() }
                    } else {
                        Button("Check now") { c.checkForUpdates() }
                            .disabled(c.update?.state == "checking" || c.update?.state == "downloading")
                    }
                }
                LabeledContent("Version", value: AppController.appVersion)
            }

            Section("This Mac") {
                LabeledContent("Machine id", value: c.state?.machine ?? "")
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: loadFromState)
    }

    private func loadFromState() {
        guard let s = c.state?.sync else { return }
        mode = s.mode
        url = s.url ?? ""
        token = s.mode == "server" ? (s.token ?? "") : ""
        port = s.hostPort ?? 7878
    }

    private func color(_ s: String?) -> Color {
        switch s {
        case "connected": .green
        case "connecting": .yellow
        case "error": .red
        default: .gray
        }
    }

    private func label(_ i: SyncInfo?) -> String {
        guard let i else { return "" }
        switch (i.mode, i.status) {
        case ("off", _): return "Sync is off"
        case (_, "connected"): return i.mode == "host" ? "Hosting · connected" : "Connected"
        case (_, "connecting"): return "Connecting…"
        case (_, "error"): return "Problem"
        default: return i.status
        }
    }
}
