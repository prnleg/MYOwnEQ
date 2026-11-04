import CMyOwnEQ
import Foundation
import IOKit

/// Thin wrapper over the Rust C API. Main-thread only.
@MainActor
final class CoreBridge {
    let handle: OpaquePointer
    private let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.keyEncodingStrategy = .convertToSnakeCase
        return e
    }()
    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .convertFromSnakeCase
        return d
    }()

    init(dataDir: URL, machineID: String) {
        try? FileManager.default.createDirectory(at: dataDir, withIntermediateDirectories: true)
        guard let h = meq_core_new(dataDir.path, machineID) else {
            fatalError("MyOwnEQ core failed to start")
        }
        handle = h
    }

    /// Send a command. `args` values may be JSON-compatible types or Encodable models.
    @discardableResult
    func command(_ name: String, _ args: [String: Any] = [:]) -> [String: Any] {
        var obj: [String: Any] = ["cmd": name]
        for (k, v) in args { obj[k] = jsonValue(v) }
        guard let data = try? JSONSerialization.data(withJSONObject: obj),
              let input = String(data: data, encoding: .utf8),
              let out = meq_core_command(handle, input)
        else { return ["ok": false, "error": "encode failed"] }
        defer { meq_string_free(out) }
        let reply = Data(bytes: out, count: strlen(out))
        lastReply = reply
        return (try? JSONSerialization.jsonObject(with: reply)) as? [String: Any] ?? [:]
    }

    /// Raw bytes of the last reply (to decode typed values without re-serializing).
    private(set) var lastReply = Data()

    private(set) var lastDecodeError: String?

    func decodeLast<T: Decodable>(_ type: T.Type) -> T? {
        do { return try decoder.decode(T.self, from: lastReply) } catch {
            lastDecodeError = "\(error)"
            NSLog("MyOwnEQ decode \(T.self) failed: \(error)")
            return nil
        }
    }

    /// Convert Encodable models to JSON objects; pass through plain values.
    private func jsonValue(_ v: Any) -> Any {
        switch v {
        case let e as any Encodable:
            if v is String || v is Bool || v is Int || v is Double { return v }
            guard let d = try? encoder.encode(e),
                  let o = try? JSONSerialization.jsonObject(with: d, options: .fragmentsAllowed)
            else { return NSNull() }
            return o
        case let a as [Any]: return a.map(jsonValue)
        case let m as [String: Any]: return m.mapValues(jsonValue)
        case Optional<Any>.none: return NSNull()
        default: return v
        }
    }

    func makeAudio(sampleRate: Double) -> OpaquePointer? {
        meq_core_make_audio(handle, sampleRate)
    }

    func spectrum(into buf: inout [Float]) -> Bool {
        let n = UInt32(buf.count)
        return buf.withUnsafeMutableBufferPointer { meq_core_spectrum(handle, $0.baseAddress, n) } != 0
    }

    /// LAN discovery; safe off the main thread (doesn't touch the core).
    nonisolated static func discover(timeoutMs: UInt32 = 1500) -> [FoundHost] {
        guard let out = meq_discover(timeoutMs) else { return [] }
        defer { meq_string_free(out) }
        let data = Data(bytes: out, count: strlen(out))
        return (try? JSONDecoder().decode([FoundHost].self, from: data)) ?? []
    }

    // MARK: - machine identity

    /// Stable per-Mac id (IOPlatformUUID), hashed so the raw hardware id never leaves the machine.
    static func machineID() -> String {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"))
        defer { IOObjectRelease(service) }
        let raw = (IORegistryEntryCreateCFProperty(service, kIOPlatformUUIDKey as CFString, kCFAllocatorDefault, 0)?
            .takeRetainedValue() as? String) ?? Host.current().localizedName ?? "mac"
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325 // FNV-1a
        for b in raw.utf8 { hash = (hash ^ UInt64(b)) &* 0x100_0000_01b3 }
        return "mac-" + String(hash, radix: 16)
    }

    /// `MYOWNEQ_DATA_DIR` overrides it (screenshots, tests): a throwaway folder.
    static var dataDir: URL {
        if let d = ProcessInfo.processInfo.environment["MYOWNEQ_DATA_DIR"], !d.isEmpty {
            return URL(fileURLWithPath: d, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MyOwnEQ", isDirectory: true)
    }
}
