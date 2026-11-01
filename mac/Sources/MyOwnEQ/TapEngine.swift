import AudioToolbox
import CMyOwnEQ
import CoreAudio
import Foundation

struct EngineError: LocalizedError {
    let message: String
    let status: OSStatus
    var errorDescription: String? { "\(message) (OSStatus \(status))" }
}

/// System-wide EQ via Core Audio Process Taps (macOS 14.2+):
///
///   all apps ──► [global tap, original muted] ──► aggregate device input
///   aggregate IOProc: copy tap → Rust DSP → output device
///
/// If this process dies, Core Audio removes the tap and audio un-mutes on its
/// own, so a crash can never leave the user without sound.
final class TapEngine {
    static let aggregateUIDPrefix = "com.prnleg.myowneq.aggregate"

    private static let maxFrames = 8192
    private static let maxChannels = 8

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var audio: OpaquePointer?
    private var scratch: UnsafeMutablePointer<Float>?

    private(set) var device: OutputDevice?
    private(set) var sampleRate: Double = 0
    var isRunning: Bool { procID != nil }

    @MainActor
    func start(device: OutputDevice, core: CoreBridge) throws {
        stop()
        do {
            try startThrowing(device: device, core: core)
        } catch {
            stop()
            throw error
        }
    }

    @MainActor
    private func startThrowing(device: OutputDevice, core: CoreBridge) throws {
        // 1. Tap everything except ourselves, muting the original path.
        let exclude = CA.ownProcessObject().map { [$0] } ?? []
        let tap = CATapDescription(stereoGlobalTapButExcludeProcesses: exclude)
        tap.uuid = UUID()
        tap.name = "MyOwnEQ"
        tap.isPrivate = true
        tap.muteBehavior = .mutedWhenTapped
        var err = AudioHardwareCreateProcessTap(tap, &tapID)
        guard err == noErr else {
            throw EngineError(message: "Could not create the system audio tap. Allow MyOwnEQ under System Settings › Privacy & Security › Screen & System Audio Recording.", status: err)
        }

        // 2. Private aggregate: output device (clock source) + our tap as input.
        let desc: [String: Any] = [
            kAudioAggregateDeviceNameKey: "MyOwnEQ",
            kAudioAggregateDeviceUIDKey: "\(Self.aggregateUIDPrefix).\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: device.uid,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: device.uid]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapUIDKey: tap.uuid.uuidString,
                kAudioSubTapDriftCompensationKey: true,
            ]],
        ]
        err = AudioHardwareCreateAggregateDevice(desc as CFDictionary, &aggregateID)
        guard err == noErr else { throw EngineError(message: "Could not create aggregate device", status: err) }

        sampleRate = CA.nominalSampleRate(aggregateID) ?? CA.nominalSampleRate(device.id) ?? 48_000
        guard let audio = core.makeAudio(sampleRate: sampleRate) else {
            throw EngineError(message: "DSP init failed", status: -1)
        }
        self.audio = audio

        let scratch = UnsafeMutablePointer<Float>.allocate(capacity: Self.maxFrames * Self.maxChannels)
        scratch.initialize(repeating: 0, count: Self.maxFrames * Self.maxChannels)
        self.scratch = scratch

        // Input buffers of the aggregate = the output device's own inputs
        // (e.g. a headset mic) followed by the tap. Skip the device inputs.
        let tapBufferStart = CA.array(device.id, CA.address(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput)).count

        // 3. IOProc. Captures only value types / raw pointers: no ARC, no
        //    allocation, no locks on the real-time thread.
        let maxFrames = Self.maxFrames
        let maxChannels = Self.maxChannels
        let block: AudioDeviceIOBlock = { _, inInput, _, outOutput, _ in
            let inList = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inInput))
            let outList = UnsafeMutableAudioBufferListPointer(outOutput)

            var outCh = 0
            var frames = maxFrames
            for b in outList where b.mNumberChannels > 0 {
                outCh += Int(b.mNumberChannels)
                frames = min(frames, Int(b.mDataByteSize) / (4 * Int(b.mNumberChannels)))
            }
            guard outCh > 0 else { return }

            // Gather tap channels into interleaved scratch.
            var ch = 0
            var inFrames = maxFrames
            if tapBufferStart < inList.count {
                for i in tapBufferStart..<inList.count {
                    let b = inList[i]
                    let c = Int(b.mNumberChannels)
                    guard c > 0 else { continue }
                    ch += c
                    inFrames = min(inFrames, Int(b.mDataByteSize) / (4 * c))
                }
            }
            ch = min(ch, maxChannels)
            frames = min(frames, inFrames)
            guard ch > 0, frames > 0 else {
                for b in outList { if let p = b.mData { memset(p, 0, Int(b.mDataByteSize)) } }
                return
            }

            var base = 0
            for i in tapBufferStart..<inList.count {
                let b = inList[i]
                let c = Int(b.mNumberChannels)
                if let p = b.mData?.assumingMemoryBound(to: Float.self) {
                    for f in 0..<frames {
                        for k in 0..<c where base + k < ch {
                            scratch[f * ch + base + k] = p[f * c + k]
                        }
                    }
                }
                base += c
                if base >= ch { break }
            }

            meq_audio_process(audio, scratch, UInt32(frames), UInt32(ch))

            // Scatter to the device's output buffers.
            var obase = 0
            for b in outList {
                let c = Int(b.mNumberChannels)
                guard c > 0, let p = b.mData?.assumingMemoryBound(to: Float.self) else { obase += c; continue }
                let bufFrames = Int(b.mDataByteSize) / (4 * c)
                for f in 0..<frames {
                    for k in 0..<c {
                        let oc = obase + k
                        let v: Float
                        if ch == 1 {
                            v = scratch[f]
                        } else if outCh == 1 {
                            v = (scratch[f * ch] + scratch[f * ch + 1]) * 0.5
                        } else if oc < ch {
                            v = scratch[f * ch + oc]
                        } else {
                            v = 0
                        }
                        p[f * c + k] = v
                    }
                }
                if bufFrames > frames {
                    memset(p + frames * c, 0, (bufFrames - frames) * c * 4)
                }
                obase += c
            }
        }

        var proc: AudioDeviceIOProcID?
        err = AudioDeviceCreateIOProcIDWithBlock(&proc, aggregateID, nil, block)
        guard err == noErr, let proc else { throw EngineError(message: "Could not create IOProc", status: err) }
        procID = proc
        err = AudioDeviceStart(aggregateID, proc)
        guard err == noErr else { throw EngineError(message: "Could not start audio", status: err) }
        self.device = device
        NSLog("MyOwnEQ: engine running on \(device.name) @ \(Int(sampleRate)) Hz")
    }

    func stop() {
        if let proc = procID {
            AudioDeviceStop(aggregateID, proc)
            AudioDeviceDestroyIOProcID(aggregateID, proc)
            procID = nil
        }
        if aggregateID != kAudioObjectUnknown {
            AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = AudioObjectID(kAudioObjectUnknown)
        }
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
        if let a = audio {
            meq_audio_free(a)
            audio = nil
        }
        if let s = scratch {
            s.deallocate()
            scratch = nil
        }
        device = nil
    }

    deinit { stop() }
}
