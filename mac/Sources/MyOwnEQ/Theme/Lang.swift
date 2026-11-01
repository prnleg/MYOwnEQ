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

    func reload() {
        var byCode: [String: LangPack] = ["en-US": LangPack(code: "en-US", name: "English (US)", author: "MyOwnEQ", strings: [:])]
        var errors: [String] = []
        for dir in Self.searchDirs() {
            let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
            for f in files where f.pathExtension == "json" {
                do {
                    let p = try JSONDecoder().decode(LangPack.self, from: Data(contentsOf: f))
                    // User packs extend/override bundled ones key by key.
                    if var existing = byCode[p.code] {
                        existing.strings.merge(p.strings) { _, new in new }
                        existing.name = p.name
                        byCode[p.code] = existing
                    } else {
                        byCode[p.code] = p
                    }
                } catch {
                    errors.append("\(f.lastPathComponent): \(error.localizedDescription)")
                }
            }
        }
        packs = byCode.values.sorted { $0.name < $1.name }
        loadErrors = errors
        apply()
    }

    private func apply() {
        let wanted = choice ?? Self.systemMatch(packs.map(\.code)) ?? "en-US"
        let pack = packs.first { $0.code == wanted } ?? packs.first { $0.code == "en-US" }
        table = pack?.strings ?? [:]
        code = pack?.code ?? "en-US"
    }

    /// First system preferred language we have a pack for (exact, then by language).
    private static func systemMatch(_ codes: [String]) -> String? {
        for pref in Locale.preferredLanguages {
            if let c = codes.first(where: { $0.caseInsensitiveCompare(pref) == .orderedSame }) { return c }
            let lang = pref.split(separator: "-").first.map(String.init) ?? pref
            if let c = codes.first(where: { $0.lowercased().hasPrefix(lang.lowercased() + "-") || $0.lowercased() == lang.lowercased() }) { return c }
        }
        return nil
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
