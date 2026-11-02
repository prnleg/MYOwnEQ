import Foundation

// Swift mirrors of the Rust model JSON (core/model). Keys are snake_case on
// the wire; the bridge's coders convert. Ids stay lowercase strings.

typealias ID = String

func newID() -> ID { UUID().uuidString.lowercased() }

enum FilterKind: String, Codable, CaseIterable, Identifiable {
    case peak, lowShelf = "low_shelf", highShelf = "high_shelf", lowPass = "low_pass",
         highPass = "high_pass", notch, bandPass = "band_pass"
    var id: String { rawValue }
    var label: String {
        switch self {
        case .peak: L("Peak")
        case .lowShelf: L("Low Shelf")
        case .highShelf: L("High Shelf")
        case .lowPass: L("Low Pass")
        case .highPass: L("High Pass")
        case .notch: L("Notch")
        case .bandPass: L("Band Pass")
        }
    }
    var hasGain: Bool { self == .peak || self == .lowShelf || self == .highShelf }
}

struct Band: Codable, Equatable, Identifiable {
    var kind: FilterKind
    var freq: Double
    var gainDb: Double
    var q: Double
    var enabled: Bool = true
    // Local identity for SwiftUI lists; not sent over the wire.
    var id = UUID()
    enum CodingKeys: String, CodingKey { case kind, freq, gainDb, q, enabled }

    static func == (a: Band, b: Band) -> Bool {
        a.kind == b.kind && a.freq == b.freq && a.gainDb == b.gainDb && a.q == b.q && a.enabled == b.enabled
    }
}

struct GraphicPoint: Codable, Equatable {
    var freq: Double
    var gainDb: Double
}

enum EqLayer: Codable, Equatable {
    case parametric([Band])
    case graphic([GraphicPoint])

    private enum K: String, CodingKey { case type, bands, points }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        switch try c.decode(String.self, forKey: .type) {
        case "graphic": self = .graphic(try c.decode([GraphicPoint].self, forKey: .points))
        default: self = .parametric(try c.decodeIfPresent([Band].self, forKey: .bands) ?? [])
        }
    }
    func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: K.self)
        switch self {
        case .parametric(let b): try c.encode("parametric", forKey: .type); try c.encode(b, forKey: .bands)
        case .graphic(let p): try c.encode("graphic", forKey: .type); try c.encode(p, forKey: .points)
        }
    }
}

enum Preamp: Codable, Equatable {
    case auto
    case manual(Double)
    private enum K: String, CodingKey { case mode, db }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        self = try c.decode(String.self, forKey: .mode) == "manual"
            ? .manual(try c.decode(Double.self, forKey: .db)) : .auto
    }
    func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: K.self)
        switch self {
        case .auto: try c.encode("auto", forKey: .mode)
        case .manual(let db): try c.encode("manual", forKey: .mode); try c.encode(db, forKey: .db)
        }
    }
}

struct Macros: Codable, Equatable {
    var bass = 0.0, mids = 0.0, clarity = 0.0, treble = 0.0
    /// Whole-spectrum slope (dB): + brighter, − warmer.
    var tilt = 0.0
}

/// Headphone crossfeed strength (core: myowneq_dsp::Crossfeed).
enum Crossfeed: String, Codable, CaseIterable {
    case off, light, normal, strong
}

struct CorrectionSource: Codable, Equatable {
    var provider: String
    var name: String
}

struct Profile: Codable, Equatable, Identifiable {
    var id: ID
    var name: String
    var correction: [Band] = []
    var correctionSource: CorrectionSource?
    var eq: EqLayer = .parametric([])
    var macros = Macros()
    var preamp: Preamp = .auto
    var balance = 0.0
    var limiter = true
    var loudness = 0.0
    var crossfeed: Crossfeed = .off
    var tags: [String] = []

    var isBuiltin: Bool { tags.contains("builtin") }

    static func new(_ name: String) -> Profile { Profile(id: newID(), name: name) }
}

struct DeviceKey: Codable, Hashable {
    var machine: String
    var uid: String
}

enum DeviceKind: String, Codable {
    case builtIn = "built_in", bluetooth, usb, hdmi, analog, virtual, other
    var symbol: String {
        switch self {
        case .builtIn: "laptopcomputer"
        case .bluetooth: "headphones"
        case .usb: "cable.connector"
        case .hdmi: "tv"
        case .analog: "headphones"
        case .virtual: "waveform"
        case .other: "speaker.wave.2"
        }
    }
}

struct DeviceMember: Codable, Equatable {
    var key: DeviceKey
    var osName: String
    var kind: DeviceKind
}

struct DeviceGroup: Codable, Equatable, Identifiable {
    var id: ID
    var name: String
    var members: [DeviceMember]
    var defaultProfile: ID?
    var favorites: [ID] = []
    var enabled = true
    /// Extra gain in dB (0…12); the core forces the limiter on while > 0.
    var boostDb = 0.0
}

enum Trigger: String, Codable, CaseIterable { case running, focused }

struct AppMatch: Codable, Equatable {
    var name: String
    var macBundleId: String?
    var winExe: String?
}

struct Rule: Codable, Equatable, Identifiable {
    var id: ID
    var group: ID
    var app: AppMatch
    var trigger: Trigger = .running
    var profile: ID
    var priority = 0
    var enabled = true
}

struct Library: Codable, Equatable {
    var schema: Int
    var profiles: [Profile]
    var groups: [DeviceGroup]
    var rules: [Rule]
    var selected: [String: ID] = [:]
}

struct ResolutionInfo: Codable, Equatable {
    var kind: String
    var profile: ID?
    var rule: ID?
}

struct CoreState: Codable, Equatable {
    var machine: String
    var enabled: Bool
    var device: DeviceKey?
    var group: ID?
    var connectedGroups: [ID]
    var resolution: ResolutionInfo
    var preampDb: Double
    var sampleRate: Double
    var library: Library
    var warnings: [String]
    var sync: SyncInfo
    var update: UpdateInfo?

    func profile(_ id: ID?) -> Profile? { id.flatMap { i in library.profiles.first { $0.id == i } } }
    func group(_ id: ID?) -> DeviceGroup? { id.flatMap { i in library.groups.first { $0.id == i } } }
    func rule(_ id: ID?) -> Rule? { id.flatMap { i in library.rules.first { $0.id == i } } }
    var activeProfile: Profile? { profile(resolution.profile) }
    var currentGroup: DeviceGroup? { group(group) }
}

struct SyncInfo: Codable, Equatable {
    var mode: String          // off | server | host
    var status: String        // off | connecting | connected | error
    var detail: String
    var url: String?
    var token: String?
    var hostPort: Int?
    var hostAddresses: [String]?
    var pending: Int
    /// Machines using this sync (from the server), online or seen before.
    var peers: [Peer]?
}

/// A machine in the sync, as the server reports it.
struct Peer: Codable, Equatable, Hashable {
    var machine: String
    var name: String = ""
    var platform: String = ""
    var version: String = ""
    var online: Bool
    var lastSeen: UInt64

    enum CodingKeys: String, CodingKey { case machine, name, platform, version, online, lastSeen }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        machine = try c.decode(String.self, forKey: .machine)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        platform = try c.decodeIfPresent(String.self, forKey: .platform) ?? ""
        version = try c.decodeIfPresent(String.self, forKey: .version) ?? ""
        online = try c.decodeIfPresent(Bool.self, forKey: .online) ?? false
        lastSeen = try c.decodeIfPresent(UInt64.self, forKey: .lastSeen) ?? 0
    }

    var displayName: String { name.isEmpty ? machine : name }
}

/// Shared constants from the core (`ui_constants`).
struct UiConstants: Codable {
    struct Spot: Codable { var name: String; var freq: Double }
    var graphic10: [Double]
    var graphic31: [Double]
    var macros: [Spot]
    var maxBands: Int
}

/// A short message shown as a toast; disappears by itself.
struct Notice: Equatable, Identifiable {
    let id = UUID()
    var text: String
    var isError = false
}

/// Updater status (core/update). `state`: off | idle | checking | up_to_date |
/// downloading | ready | error.
struct UpdateInfo: Codable, Equatable {
    var state: String
    var version: String?
    var notesUrl: String?
    var message: String?
    var current: String?
    var auto: Bool?
    var checkedAt: Int?

    var isReady: Bool { state == "ready" }
    /// Checking or downloading right now (show a spinner).
    var isBusy: Bool { state == "checking" || state == "downloading" }
    var label: String {
        switch state {
        case "checking": L("Checking for updates…")
        case "up_to_date": L("Up to date")
        case "downloading": L("Downloading %@…", version ?? "")
        case "ready": L("Version %@ is ready. Restart to install.", version ?? "")
        case "error": L("Update check failed: %@", message ?? "")
        default: L("Updates idle")
        }
    }
}

struct FoundHost: Codable, Hashable {
    var name: String
    var addr: String
}

struct Curve: Codable {
    var freqs: [Double]
    var total: [Double]
    var preamp: Double
    var bands: [[Double]]
}

// MARK: - lenient decoding
//
// The Rust core fills in serde defaults and skips some empty fields (e.g.
// `tags` when empty). Swift's synthesized Decodable would reject a missing key
// and fail the whole state, so every field Rust treats as optional is decoded
// with the same default here. Defined in extensions to keep memberwise inits.

private extension KeyedDecodingContainer {
    func get<T: Decodable>(_ key: K, _ fallback: T) throws -> T {
        try decodeIfPresent(T.self, forKey: key) ?? fallback
    }
}

extension Band {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(kind: try c.decode(FilterKind.self, forKey: .kind),
                  freq: try c.decode(Double.self, forKey: .freq),
                  gainDb: try c.get(.gainDb, 0),
                  q: try c.get(.q, 0.707),
                  enabled: try c.get(.enabled, true))
    }
}

extension Macros {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(bass: try c.get(.bass, 0), mids: try c.get(.mids, 0),
                  clarity: try c.get(.clarity, 0), treble: try c.get(.treble, 0),
                  tilt: try c.get(.tilt, 0))
    }
}

extension Profile {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(id: try c.decode(ID.self, forKey: .id),
                  name: try c.get(.name, ""),
                  correction: try c.get(.correction, []),
                  correctionSource: try c.decodeIfPresent(CorrectionSource.self, forKey: .correctionSource),
                  eq: try c.get(.eq, .parametric([])),
                  macros: try c.get(.macros, Macros()),
                  preamp: try c.get(.preamp, .auto),
                  balance: try c.get(.balance, 0),
                  limiter: try c.get(.limiter, true),
                  loudness: try c.get(.loudness, 0),
                  crossfeed: (try? c.decodeIfPresent(Crossfeed.self, forKey: .crossfeed)) ?? .off,
                  tags: try c.get(.tags, []))
    }
}

extension DeviceMember {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(key: try c.decode(DeviceKey.self, forKey: .key),
                  osName: try c.get(.osName, ""),
                  kind: (try? c.decodeIfPresent(DeviceKind.self, forKey: .kind)) ?? .other)
    }
}

extension DeviceGroup {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(id: try c.decode(ID.self, forKey: .id),
                  name: try c.get(.name, ""),
                  members: try c.get(.members, []),
                  defaultProfile: try c.decodeIfPresent(ID.self, forKey: .defaultProfile),
                  favorites: try c.get(.favorites, []),
                  enabled: try c.get(.enabled, true),
                  boostDb: try c.get(.boostDb, 0))
    }
}

extension Rule {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(id: try c.decode(ID.self, forKey: .id),
                  group: try c.decode(ID.self, forKey: .group),
                  app: try c.decode(AppMatch.self, forKey: .app),
                  trigger: try c.get(.trigger, .running),
                  profile: try c.decode(ID.self, forKey: .profile),
                  priority: try c.get(.priority, 0),
                  enabled: try c.get(.enabled, true))
    }
}

extension Library {
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        self.init(schema: try c.get(.schema, 1),
                  profiles: try c.get(.profiles, []),
                  groups: try c.get(.groups, []),
                  rules: try c.get(.rules, []),
                  selected: try c.get(.selected, [:]))
    }
}
