using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualMixer;

public enum SourceKind
{
    /// <summary>WASAPI loopback capture of a render device ("what you hear" / PC playback).</summary>
    Loopback,
    /// <summary>WASAPI capture of a mic / line input.</summary>
    Capture,
}

/// <summary>
/// One mixer input: a WASAPI capture pushed into a ring buffer, then pulled through a
/// format-normalising chain (mono→stereo, resample) into a volume node and a meter.
/// The <see cref="MixerNode"/> is what gets added to the MixingSampleProvider.
/// </summary>
public sealed class InputSource : IDisposable
{
    public string Id { get; }
    public SourceKind Kind { get; }
    public string DeviceName { get; }
    public string DeviceId { get; }
    public bool Enabled { get; set; }
    public bool Dead { get; private set; }
    public string? DeadReason { get; private set; }

    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private readonly MeteringSampleProvider _meter;
    private float _lastPeak;

    /// <summary>The node added to the mixer (post-volume, metered). Always master format.</summary>
    public ISampleProvider MixerNode => _meter;

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = value;
    }

    /// <summary>Most recent peak level across channels, 0..1 (only updates while routed and pulled).</summary>
    public float LastPeak => _lastPeak;

    public InputSource(string id, SourceKind kind, MMDevice device, WaveFormat mixFormat)
    {
        Id = id;
        Kind = kind;
        DeviceName = device.FriendlyName;
        DeviceId = device.ID;

        _capture = kind == SourceKind.Loopback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device); // shared mode, event sync by default

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Build the pull-side chain and guarantee it ends in mixFormat.
        ISampleProvider chain = ConvertToMixFormat(_buffer, mixFormat);
        _volume = new VolumeSampleProvider(chain) { Volume = 1.0f };
        _meter = new MeteringSampleProvider(_volume, mixFormat.SampleRate / 10); // ~10 reports/sec
        _meter.StreamVolume += (_, e) =>
        {
            float p = 0f;
            foreach (var v in e.MaxSampleValues)
                if (v > p) p = v;
            _lastPeak = p;
        };

        if (_meter.WaveFormat.SampleRate != mixFormat.SampleRate || _meter.WaveFormat.Channels != mixFormat.Channels)
            throw new InvalidOperationException(
                $"input chain format {_meter.WaveFormat} != mix format {mixFormat}");

        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Keep this fast: just enqueue. No logging, no locks held long.
        if (e.BytesRecorded > 0)
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Dead = true;
            DeadReason = e.Exception.Message;
        }
    }

    /// <summary>Drop buffered audio so re-enabling does not replay up to 500 ms of stale sound.</summary>
    public void ClearBuffer() => _buffer.ClearBuffer();

    private static ISampleProvider ConvertToMixFormat(BufferedWaveProvider buffer, WaveFormat mix)
    {
        var src = buffer.WaveFormat;

        // >2 channels (e.g. 5.1 loopback): WDL only handles mono/stereo, so let
        // Media Foundation convert channels + rate straight to the mix format.
        if (src.Channels > 2)
        {
            var mfr = new MediaFoundationResampler(buffer, mix) { ResamplerQuality = 60 };
            return mfr.ToSampleProvider();
        }

        ISampleProvider sp = buffer.ToSampleProvider();

        // Channel count first (WDL preserves channel count).
        if (sp.WaveFormat.Channels == 1 && mix.Channels == 2)
            sp = new MonoToStereoSampleProvider(sp);

        // Then sample rate.
        if (sp.WaveFormat.SampleRate != mix.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);

        return sp;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { /* may already be stopped */ }
        _capture.Dispose();
    }
}
