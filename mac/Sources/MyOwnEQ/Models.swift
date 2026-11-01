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
        case .peak: "Peak"
        case .lowShelf: "Low Shelf"
        case .highShelf: "High Shelf"
        case .lowPass: "Low Pass"
        case .highPass: "High Pass"
        case .notch: "Notch"
        case .bandPass: "Band Pass"
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
    var label: String {
        switch state {
        case "checking": "Checking for updates…"
        case "up_to_date": "Up to date"
        case "downloading": "Downloading \(version ?? "")…"
        case "ready": "Version \(version ?? "") is ready. Restart to install."
        case "error": "Update check failed: \(message ?? "")"
        default: "Updates idle"
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
