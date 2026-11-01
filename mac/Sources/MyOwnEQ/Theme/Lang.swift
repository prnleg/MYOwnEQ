import AppKit
import Foundation

/// UI language, loaded from `lang/*.json` (bundled) or the user's
/// `~/Library/Application Support/MyOwnEQ/Lang`. Keys are the English text,
/// so a missing translation simply shows English. See lang/README.md.
struct LangPack: Codable, Identifiable, Equatable {
    var code: String
    var name: String
    var author: String?
    var strings: [String: String]
    var id: String { code }
}

/// Translate. `%@` placeholders are filled from `args` in order.
func L(_ key: String, _ args: CustomStringConvertible...) -> String {
    var s = Lang.shared.table[key].flatMap { $0.isEmpty ? nil : $0 } ?? key
    for a in args {
        guard let r = s.range(of: "%@") else { break }
        s.replaceSubrange(r, with: a.description)
    }
    return s
}

// parity: languages
final class Lang: ObservableObject {
    static let shared = Lang()

    @Published private(set) var packs: [LangPack] = []
    /// Chosen language code, or nil to follow the system.
    @Published private(set) var choice: String?
    @Published private(set) var code = "en-US"
    @Published private(set) var loadErrors: [String] = []
    fileprivate var table: [String: String] = [:]

    private init() {
        choice = UserDefaults.standard.string(forKey: "language")
        reload()
    }

    func select(_ code: String?) {
        choice = code
        UserDefaults.standard.set(code, forKey: "language")
        apply()
    }

    /// Parsing, validation and merge rules live in the core (same for every app).
    private var core: CoreBridge?

    @MainActor
    func attach(_ core: CoreBridge) {
        self.core = core
        reload()
    }

    func reload() {
        guard let core else { return } // before the core exists: English keys
        let dirs = Self.searchDirs().map(\.path)
        let r = MainActor.assumeIsolated {
            core.command("load_langs", ["dirs": dirs, "choice": choice as Any, "preferred": Locale.preferredLanguages])
        }
        let dec = JSONDecoder()
        if let raw = r["packs"], let d = try? JSONSerialization.data(withJSONObject: raw),
           let p = try? dec.decode([LangPack].self, from: d) {
            packs = p
        }
        loadErrors = r["errors"] as? [String] ?? []
        resolved = r["code"] as? String ?? "en-US"
        apply()
    }

    private var resolved = "en-US"

    private func apply() {
        let wanted = choice ?? resolved
        let pack = packs.first { $0.code == wanted } ?? packs.first { $0.code == "en-US" }
        table = pack?.strings ?? [:]
        code = pack?.code ?? "en-US"
    }

    static var userDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MyOwnEQ/Lang", isDirectory: true)
    }

    func openUserFolder() {
        try? FileManager.default.createDirectory(at: Self.userDir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(Self.userDir)
    }

    private static func searchDirs() -> [URL] {
        var dirs: [URL] = []
        if let r = Bundle.main.resourceURL { dirs.append(r.appendingPathComponent("Lang")) }
        var u = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().deletingLastPathComponent()
        for _ in 0..<6 {
            let t = u.appendingPathComponent("lang")
            if FileManager.default.fileExists(atPath: t.appendingPathComponent("pt-BR.json").path) {
                dirs.append(t)
                break
            }
            u = u.deletingLastPathComponent()
        }
        dirs.append(userDir)
        return dirs
    }
}
