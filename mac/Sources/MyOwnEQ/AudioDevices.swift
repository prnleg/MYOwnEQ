import AudioToolbox
import CoreAudio
import Foundation

/// An output device as reported by Core Audio.
struct OutputDevice: Equatable {
    let id: AudioObjectID
    let uid: String
    let name: String
    let kind: DeviceKind
}

enum CA {
    static func address(_ selector: AudioObjectPropertySelector,
                        _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                        _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: element)
    }

    static func get<T>(_ obj: AudioObjectID, _ addr: AudioObjectPropertyAddress, _ initial: T) -> T? {
        var a = addr
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        let err = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(obj, &a, 0, nil, &size, $0) }
        return err == noErr ? value : nil
    }

    static func string(_ obj: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var a = address(selector)
        var size = UInt32(MemoryLayout<CFString?>.size)
        var value: Unmanaged<CFString>?
        let err = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(obj, &a, 0, nil, &size, $0) }
        guard err == noErr, let v = value else { return nil }
        return v.takeRetainedValue() as String
    }

    static func array(_ obj: AudioObjectID, _ addr: AudioObjectPropertyAddress) -> [AudioObjectID] {
        var a = addr
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(obj, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    static func defaultOutputDevice() -> AudioObjectID? {
        get(AudioObjectID(kAudioObjectSystemObject), address(kAudioHardwarePropertyDefaultOutputDevice), AudioObjectID(0))
            .flatMap { $0 == kAudioObjectUnknown ? nil : $0 }
    }

    static func hasOutput(_ dev: AudioObjectID) -> Bool {
        !array(dev, address(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput)).isEmpty
    }

    static func nominalSampleRate(_ dev: AudioObjectID) -> Double? {
        get(dev, address(kAudioDevicePropertyNominalSampleRate), Float64(0)).flatMap { $0 > 0 ? $0 : nil }
    }

    static func kind(_ dev: AudioObjectID, uid: String) -> DeviceKind {
        let t = get(dev, address(kAudioDevicePropertyTransportType), UInt32(0)) ?? 0
        switch t {
        case kAudioDeviceTransportTypeBuiltIn:
            return uid.localizedCaseInsensitiveContains("Headphone") ? .analog : .builtIn
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: return .bluetooth
        case kAudioDeviceTransportTypeUSB: return .usb
        case kAudioDeviceTransportTypeHDMI, kAudioDeviceTransportTypeDisplayPort: return .hdmi
        case kAudioDeviceTransportTypeVirtual, kAudioDeviceTransportTypeAggregate: return .virtual
        default: return .other
        }
    }

    static func device(_ id: AudioObjectID) -> OutputDevice? {
        guard let uid = string(id, kAudioDevicePropertyDeviceUID) else { return nil }
        let name = string(id, kAudioObjectPropertyName) ?? uid
        return OutputDevice(id: id, uid: uid, name: name, kind: kind(id, uid: uid))
    }

    /// All real output devices (our own private aggregate excluded).
    static func outputDevices() -> [OutputDevice] {
        array(AudioObjectID(kAudioObjectSystemObject), address(kAudioHardwarePropertyDevices))
            .filter(hasOutput)
            .compactMap(device)
            .filter { !$0.uid.hasPrefix(TapEngine.aggregateUIDPrefix) }
    }

    /// System output volume 0..1 (virtual main volume), if the device exposes one.
    static func volume(_ dev: AudioObjectID) -> Double? {
        get(dev, address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput),
            Float32(0)).map(Double.init)
    }

    /// Whether the device's main volume can be changed from software.
    static func volumeSettable(_ dev: AudioObjectID) -> Bool {
        var a = address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput)
        var settable: DarwinBoolean = false
        return AudioObjectIsPropertySettable(dev, &a, &settable) == noErr && settable.boolValue
    }

    /// Set the device's main volume (0…1), like the keyboard volume keys.
    @discardableResult
    static func setVolume(_ dev: AudioObjectID, _ v: Double) -> Bool {
        var a = address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput)
        var value = Float32(min(max(v, 0), 1))
        return AudioObjectSetPropertyData(dev, &a, 0, nil, UInt32(MemoryLayout<Float32>.size), &value) == noErr
    }

    /// Our process as a Core Audio process object (to exclude from the tap).
    static func ownProcessObject() -> AudioObjectID? {
        var pid = getpid()
        var a = address(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var obj = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let err = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a,
                                             UInt32(MemoryLayout<pid_t>.size), &pid, &size, &obj)
        return err == noErr && obj != kAudioObjectUnknown ? obj : nil
    }
}

/// Fires `onChange` on the main queue when the default output, the device
/// list, or the current device's volume / sample rate changes.
final class DeviceWatcher {
    private var onChange: () -> Void
    private var registered: [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var deviceListeners: [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []

    init(onChange: @escaping () -> Void) {
        self.onChange = onChange
        let sys = AudioObjectID(kAudioObjectSystemObject)
        listen(sys, CA.address(kAudioHardwarePropertyDefaultOutputDevice), into: &registered)
        listen(sys, CA.address(kAudioHardwarePropertyDevices), into: &registered)
    }

    /// Watch volume / sample rate of the active output device.
    func watchDevice(_ dev: AudioObjectID?) {
        for (o, a, b) in deviceListeners { var a = a; AudioObjectRemovePropertyListenerBlock(o, &a, .main, b) }
        deviceListeners.removeAll()
        guard let dev else { return }
        listen(dev, CA.address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput), into: &deviceListeners)
        listen(dev, CA.address(kAudioDevicePropertyNominalSampleRate), into: &deviceListeners)
    }

    private func listen(_ obj: AudioObjectID, _ addr: AudioObjectPropertyAddress,
                        into list: inout [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)]) {
        var a = addr
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.onChange() }
        if AudioObjectAddPropertyListenerBlock(obj, &a, .main, block) == noErr {
            list.append((obj, addr, block))
        }
    }

    deinit {
        watchDevice(nil)
        for (o, a, b) in registered { var a = a; AudioObjectRemovePropertyListenerBlock(o, &a, .main, b) }
    }
}
