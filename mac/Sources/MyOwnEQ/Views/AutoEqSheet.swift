import SwiftUI

/// Search the AutoEQ headphone list (core: `autoeq_search` / `autoeq_fetch`).
/// `target` set: the picked correction goes into that profile (editor).
/// `target` nil: a new profile is made with it and `onCreated` gets its id.
// parity: autoeq-search
struct AutoEqSheet: View {
    @EnvironmentObject var c: AppController
    @EnvironmentObject var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    var target: Binding<Profile>?
    var onCreated: (ID) -> Void = { _ in }

    @State private var query = ""
    @State private var result = AutoEqSearchResult.empty
    @State private var picked: [String: String] = [:]  // model name → measurement path
    @State private var busy: String?                   // path being downloaded
    @State private var started = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text(L("Find your headphones")).font(.title3.weight(.semibold))
                Text(L("AutoEQ corrections make headphones sound neutral. Your tone goes on top."))
                    .font(.callout).foregroundStyle(.secondary)
            }
            HStack {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(L("Headphone model, e.g. HD 600"), text: $query)
                    .textFieldStyle(.plain)
                    .onSubmit { if let m = result.results.first { use(m) } }
            }
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 8).fill(theme.current.ink.opacity(0.06)))
            status
            List {
                ForEach(result.results) { m in row(m) }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
            .frame(minHeight: 280)
            .overlay {
                if result.results.isEmpty, result.state == "ready" || result.state == "updating", !query.trimmingCharacters(in: .whitespaces).isEmpty {
                    Text(L("No headphones match “%@”", query)).foregroundStyle(.secondary)
                }
            }
            HStack {
                Link(L("Data: AutoEQ project"), destination: URL(string: "https://github.com/jaakkopasanen/AutoEq")!)
                    .font(.caption)
                Spacer()
                Button(L("From a file…")) {
                    dismiss()
                    c.importFile(asCorrection: true)
                }
                Button(L("Cancel")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(22)
        .frame(width: 520, height: 520)
        .task(id: query) { await search() }
    }

    @ViewBuilder private var status: some View {
        switch result.state {
        case "loading":
            HStack(spacing: 8) { ProgressView().controlSize(.small); Text(L("Downloading the headphone list…")) }
                .font(.caption).foregroundStyle(.secondary)
        case "error":
            HStack(spacing: 8) {
                Label(result.error ?? L("Couldn't download the headphone list."), systemImage: "wifi.exclamationmark")
                    .foregroundStyle(.orange)
                Button(L("Try again")) { c.autoEqRefresh(); Task { await search() } }
            }.font(.caption)
        default:
            Text(L("%@ headphones", result.count.formatted()))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func row(_ m: AutoEqModel) -> some View {
        let path = picked[m.name] ?? m.measurements.first?.path ?? ""
        return HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(m.name).font(.callout.weight(.semibold))
                HStack(spacing: 6) {
                    Tag(text: m.kindLabel.uppercased(), color: .secondary)
                    if m.measurements.count > 1 {
                        Picker("", selection: Binding(get: { path }, set: { picked[m.name] = $0 })) {
                            ForEach(m.measurements, id: \.path) { Text($0.label).tag($0.path) }
                        }
                        .labelsHidden().pickerStyle(.menu).controlSize(.small).fixedSize()
                        .help(L("Who measured it. The first one is usually the most reliable."))
                    } else if let only = m.measurements.first {
                        Text(only.label).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            Spacer()
            if busy == path {
                ProgressView().controlSize(.small)
            } else {
                Button(L("Use")) { use(m) }
                    .buttonStyle(GlowButtonStyle(prominent: false))
                    .disabled(busy != nil)
            }
        }
        .padding(.vertical, 4)
        .listRowBackground(Color.clear)
    }

    /// Debounced search; keeps asking while the list downloads.
    private func search() async {
        if started { try? await Task.sleep(for: .milliseconds(180)) }
        guard !Task.isCancelled else { return }
        var r: AutoEqSearchResult
        if !started {
            started = true
            // First open: start from the current output's name ("Momentum 4 Headphones").
            r = c.autoEqSearch(query: "", device: c.current?.name)
            if !r.query.isEmpty { query = r.query }
        } else {
            r = c.autoEqSearch(query: query)
        }
        result = r
        while ["loading", "updating"].contains(result.state), !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            result = c.autoEqSearch(query: query)
        }
    }

    private func use(_ m: AutoEqModel) {
        guard busy == nil, let path = picked[m.name] ?? m.measurements.first?.path else { return }
        busy = path
        Task {
            defer { busy = nil }
            while true {
                switch c.autoEqFetch(path: path, create: target == nil) {
                case .loading:
                    try? await Task.sleep(for: .milliseconds(300))
                    continue
                case .failed(let e):
                    c.notify(L("Couldn't get the correction: %@", e), error: true)
                case .ready(let bands, let source):
                    if let target {
                        target.wrappedValue.correction = bands
                        target.wrappedValue.correctionSource = source
                    }
                    dismiss()
                case .created(let id):
                    c.notify(L("Added “%@”", m.name))
                    onCreated(id)
                    dismiss()
                }
                return
            }
        }
    }
}

struct AutoEqMeasurement: Hashable {
    let label: String
    let path: String
}

struct AutoEqModel: Identifiable {
    var id: String { name }
    let name: String
    let kind: String
    let measurements: [AutoEqMeasurement]

    var kindLabel: String {
        switch kind {
        case "over-ear": L("Over-ear")
        case "in-ear": L("In-ear")
        case "earbud": L("Earbud")
        default: L("Headphones")
        }
    }
}

struct AutoEqSearchResult {
    var query = ""
    var state = "loading"
    var count = 0
    var error: String?
    var results: [AutoEqModel] = []
    static let empty = AutoEqSearchResult()
}

enum AutoEqFetchResult {
    case loading
    case failed(String)
    case ready([Band], CorrectionSource)
    case created(ID)
}
