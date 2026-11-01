using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MyOwnEQ.Platform;

public sealed record OutputDevice(string Id, string Name, DeviceKind Kind);

/// <summary>Render endpoints, default device and change notifications (WASAPI / MMDevice).</summary>
public sealed class AudioDevices : IMMNotificationClient, IDisposable
{
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey EnumeratorNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action _onChange;
    private MMDevice? _watched;

    /// <summary><paramref name="onChange"/> is invoked on a COM thread; marshal to the UI thread yourself.</summary>
    public AudioDevices(Action onChange)
    {
        _onChange = onChange;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public event Action<double>? VolumeChanged;

    public List<OutputDevice> Outputs()
    {
        var list = new List<OutputDevice>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try { list.Add(Describe(d)); } catch { /* device vanished mid-enumeration */ }
        }
        return list;
    }

    public OutputDevice? Default()
    {
        try { return Describe(_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)); }
        catch { return null; }
    }

    public MMDevice? DefaultMMDevice()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
    }

    private static OutputDevice Describe(MMDevice d)
    {
        var kind = DeviceKind.Other;
        try
        {
            string enumerator = d.Properties.Contains(EnumeratorNameKey) ? d.Properties[EnumeratorNameKey].Value as string ?? "" : "";
            if (enumerator.Contains("BTH", StringComparison.OrdinalIgnoreCase)) kind = DeviceKind.Bluetooth;
            else if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase)) kind = DeviceKind.Usb;
            else if (d.Properties.Contains(FormFactorKey))
            {
                kind = Convert.ToUInt32(d.Properties[FormFactorKey].Value) switch
                {
                    1 => DeviceKind.BuiltIn,       // Speakers
                    2 or 3 or 5 => DeviceKind.Analog, // LineLevel, Headphones, Headset
                    8 or 9 => DeviceKind.Hdmi,     // SPDIF, DigitalAudioDisplayDevice
                    _ => DeviceKind.Other,
                };
            }
        }
        catch { /* property store can fail on odd drivers */ }
        return new OutputDevice(d.ID, d.FriendlyName, kind);
    }

    /// <summary>Track master volume of the default device (for loudness compensation).</summary>
    public double? WatchVolume(MMDevice? device)
    {
        if (_watched is not null)
        {
            try { _watched.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
        }
        _watched = device;
        if (device is null) return null;
        try
        {
            device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch { return null; }
    }

    /// <summary>Set the default output's master volume (0…1), like the volume keys.</summary>
    public bool SetVolume(double scalar)
    {
        try
        {
            var d = _watched ?? DefaultMMDevice();
            if (d is null) return false;
            d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(scalar, 0, 1);
            if (scalar > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
            return true;
        }
        catch { return false; }
    }

    private void OnVolume(AudioVolumeNotificationData data) => VolumeChanged?.Invoke(data.Muted ? 0 : data.MasterVolume);

    // IMMNotificationClient (COM thread)
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onChange();
    public void OnDeviceAdded(string pwstrDeviceId) => _onChange();
    public void OnDeviceRemoved(string deviceId) => _onChange();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _onChange();
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        WatchVolume(null);
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }
}
