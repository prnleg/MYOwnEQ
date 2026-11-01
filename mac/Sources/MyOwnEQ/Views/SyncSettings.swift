import SwiftUI

/// Sync setup: off / connect to a server / host on this Mac.
// parity: sync
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
                        Text(p == 1 ? L("1 change waiting") : L("%@ changes waiting", p)).font(.caption).foregroundStyle(.secondary)
                    }
                }
                if let d = info?.detail, !d.isEmpty {
                    Text(d).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }

            Section(L("Sync")) {
                Picker(L("Sync"), selection: $mode) {
                    Text(L("Off")).tag("off")
                    Text(L("Connect to a server")).tag("server")
                    Text(L("Host on this Mac (LAN)")).tag("host")
                }.pickerStyle(.radioGroup)
            }

            switch mode {
            case "server":
                Section {
                    TextField(L("Address"), text: $url, prompt: Text(L("192.168.0.10:7878 or https://eq.example.com")))
                    SecureField(L("Token / code"), text: $token)
                    HStack {
                        Button(c.discovering ? L("Searching…") : L("Find on my network")) { c.discover() }
                            .disabled(c.discovering)
                        Spacer()
                        Button(L("Connect")) { c.configureSync(mode: "server", url: url, token: token) }
                            .keyboardShortcut(.defaultAction)
                            .disabled(url.isEmpty || token.isEmpty)
                    }
                    ForEach(c.discovered, id: \.self) { h in
                        Button { url = h.addr } label: {
                            Label("\(h.name) — \(h.addr)", systemImage: "desktopcomputer")
                        }.buttonStyle(.link)
                    }
                } header: {
                    Text(L("Server"))
                } footer: {
                    Text(L("Your Docker server (see server/docker-compose.yml), any cloud host running it, or another machine hosting on the LAN."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            case "host":
                Section {
                    Stepper(L("Port %@", port), value: $port, in: 1024...65535)
                    if info?.mode == "host", let code = info?.token {
                        LabeledContent(L("Code")) {
                            Text(code).font(.title2.monospaced().bold()).textSelection(.enabled)
                        }
                        LabeledContent(L("Address")) {
                            VStack(alignment: .trailing) {
                                ForEach(info?.hostAddresses ?? [], id: \.self) { Text($0).monospaced().textSelection(.enabled) }
                            }
                        }
                        Button(L("New code")) { c.configureSync(mode: "host", port: port) }
                    } else {
                        Button(L("Start hosting")) { c.configureSync(mode: "host", port: port) }
                            .keyboardShortcut(.defaultAction)
                    }
                } header: {
                    Text(L("LAN host"))
                } footer: {
                    Text(L("On the other machine choose \"Connect to a server\", click Find on my network (or type the address) and enter the code. Syncs only while this Mac is on."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            default:
                Section {
                    Button(L("Turn sync off")) { c.configureSync(mode: "off") }
                        .disabled(info?.mode == "off")
                }
            }

            Section(L("Updates")) {
                Toggle(L("Download updates automatically, install on restart"),
                       isOn: Binding(get: { c.update?.auto ?? true }, set: { c.setAutoUpdate($0) }))
                HStack {
                    Text(c.update?.label ?? "").font(.callout).foregroundStyle(.secondary)
                    Spacer()
                    // parity: update-check
                    if c.update?.isBusy == true { BusySpinner() }
                    if c.update?.isReady == true {
                        Button(L("Restart now")) { c.installUpdateAndQuit() }
                    } else {
                        Button(L("Check now")) { c.checkForUpdates() }
                            .disabled(c.update?.isBusy == true)
                    }
                }
                LabeledContent(L("Version"), value: AppController.displayVersion)
            }

            if info?.mode != "off" {
                Section {
                    PeerList(peers: info?.peers ?? [], me: c.state?.machine ?? "")
                } header: {
                    Text(L("Connected machines"))
                } footer: {
                    Text(L("Every computer using this sync. “Find on my network” only looks for hosts to connect to."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            Section(L("This Mac")) {
                LabeledContent(L("Name"), value: Host.current().localizedName ?? "")
                LabeledContent(L("Machine id"), value: c.state?.machine ?? "")
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
        case ("off", _): return L("Sync is off")
        case (_, "connected"): return i.mode == "host" ? L("Hosting · connected") : L("Connected")
        case (_, "connecting"): return L("Connecting…")
        case (_, "error"): return L("Problem")
        default: return i.status
        }
    }
}

/// Machines using the same sync, online first (from the server).
// parity: sync-peers
struct PeerList: View {
    let peers: [Peer]
    let me: String

    var body: some View {
        if peers.isEmpty {
            Text(L("No machines yet. They appear here once connected.")).foregroundStyle(.secondary)
        }
        ForEach(peers.sorted { ($0.online ? 0 : 1, $0.displayName) < ($1.online ? 0 : 1, $1.displayName) }, id: \.machine) { p in
            HStack(spacing: 10) {
                Image(systemName: p.platform == "windows" ? "pc" : p.platform == "linux" ? "server.rack" : "laptopcomputer")
                    .frame(width: 22)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(p.displayName).font(.callout.weight(.semibold))
                        if p.machine == me { Tag(text: L("THIS COMPUTER"), color: .secondary) }
                    }
                    Text(p.version.isEmpty ? p.machine : "MyOwnEQ \(p.version) · \(p.machine)")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Circle().fill(p.online ? Color.green : Color.gray).frame(width: 8, height: 8)
                Text(p.online ? L("online") : L("seen %@", Self.ago(p.lastSeen)))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    /// Same wording on every platform (shared translation keys).
    static func ago(_ ms: UInt64) -> String {
        let s = Date().timeIntervalSince1970 - TimeInterval(ms) / 1000
        if s < 60 { return L("just now") }
        if s < 3600 { return L("%@ min ago", Int(s / 60)) }
        if s < 86400 { return L("%@ h ago", Int(s / 3600)) }
        return L("%@ d ago", Int(s / 86400))
    }
}
