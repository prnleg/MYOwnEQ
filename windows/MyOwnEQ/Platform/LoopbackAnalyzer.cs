using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyOwnEQ.Platform;

/// <summary>
/// Captures what the default device is playing (post Equalizer APO) via WASAPI
/// loopback and feeds it to the core's spectrum analyzer. Never touches the
/// audio path itself.
/// </summary>
public sealed class LoopbackAnalyzer : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private IntPtr _audio;
    private readonly int _channels;
    private readonly bool _isFloat;

    public double SampleRate { get; }

    public LoopbackAnalyzer(MMDevice device, CoreBridge core)
    {
        _capture = new WasapiLoopbackCapture(device);
        var fmt = _capture.WaveFormat;
        SampleRate = fmt.SampleRate;
        _channels = fmt.Channels;
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                   || (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        _isFloat &= fmt.BitsPerSample == 32;
        _audio = core.MakeAudio(SampleRate);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private unsafe void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_isFloat || _audio == IntPtr.Zero || _channels == 0) return;
        int frames = e.BytesRecorded / (4 * _channels);
        if (frames == 0) return;
        fixed (byte* p = e.Buffer)
        {
            Native.meq_audio_analyze(_audio, (float*)p, (uint)frames, (uint)_channels);
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose(); // joins the capture thread
        if (_audio != IntPtr.Zero) { Native.meq_audio_free(_audio); _audio = IntPtr.Zero; }
    }
}
