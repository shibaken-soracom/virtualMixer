using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VirtualMixer;

/// <summary>
/// Single authority for the whole mix. Owns the MixingSampleProvider, the record tap,
/// the monitor output and the record pump, and guarantees exactly one puller drives the
/// mixer at any time (monitor WasapiOut OR the record pump, never both).
/// </summary>
public sealed class MixerEngine : IDisposable
{
    public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly MixingSampleProvider _mixer;
    private readonly RecordTapSampleProvider _tap;
    private readonly Recorder _recorder;
    private readonly Dictionary<string, InputSource> _inputs = new();
    private int _nextInputNumber;

    private WasapiOut? _monitorOut;
    private string? _monitorDeviceId;
    private string? _monitorDeviceName;

    private Thread? _pumpThread;
    private volatile bool _pumpStop;

    private Thread? _meterThread;
    private volatile bool _meterStop;

    public MixerEngine()
    {
        _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
        _tap = new RecordTapSampleProvider(_mixer);
        _recorder = new Recorder(MixFormat);
    }

    public IReadOnlyCollection<InputSource> Inputs => _inputs.Values;
    public bool IsRecording => _recorder.IsRecording;
    public string? MonitorDeviceName => _monitorDeviceName;

    // ---- inputs ---------------------------------------------------------

    public InputSource AddInput(SourceKind kind, MMDevice device)
    {
        // Feedback guard: cannot loopback-capture the device we are monitoring through.
        if (kind == SourceKind.Loopback && _monitorDeviceId == device.ID)
            throw new InvalidOperationException(
                $"'{device.FriendlyName}' is currently the monitor output — adding it as a " +
                "loopback input would create a feedback loop. Pick a different monitor device first.");

        var id = "i" + _nextInputNumber++;
        var src = new InputSource(id, kind, device, MixFormat);
        _inputs[id] = src;
        Enable(id, true);
        return src;
    }

    public void Enable(string id, bool on)
    {
        var src = Get(id);
        if (on && !src.Enabled)
        {
            src.ClearBuffer(); // avoid replaying stale buffered audio
            _mixer.AddMixerInput(src.MixerNode);
            src.Enabled = true;
        }
        else if (!on && src.Enabled)
        {
            _mixer.RemoveMixerInput(src.MixerNode);
            src.Enabled = false;
        }
    }

    public void SetVolume(string id, float volume) => Get(id).Volume = Math.Clamp(volume, 0f, 2f);

    public InputSource Get(string id) =>
        _inputs.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"no input '{id}'");

    // ---- monitor --------------------------------------------------------

    public void SetMonitor(MMDevice device)
    {
        // Feedback guard against any active loopback input on the same endpoint.
        foreach (var src in _inputs.Values)
        {
            if (src.Kind == SourceKind.Loopback && src.DeviceId == device.ID)
                throw new InvalidOperationException(
                    $"monitor device '{device.FriendlyName}' is also loopback input {src.Id} — " +
                    "this would create a feedback loop. Choose a different output (e.g. headphones) " +
                    "or remove that loopback input.");
        }

        StopMonitorInternal();

        // Handing the mixer to WasapiOut means the pump must not also pull.
        StopPump();

        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.Init(_tap.ToWaveProvider());
        output.Play();

        _monitorOut = output;
        _monitorDeviceId = device.ID;
        _monitorDeviceName = device.FriendlyName;
    }

    public void MonitorOff()
    {
        StopMonitorInternal();
        // If we are still recording, the pump must take over pulling the mixer.
        if (_recorder.IsRecording)
            StartPump();
    }

    private void StopMonitorInternal()
    {
        if (_monitorOut != null)
        {
            try { _monitorOut.Stop(); } catch { }
            _monitorOut.Dispose();
            _monitorOut = null;
            _monitorDeviceId = null;
            _monitorDeviceName = null;
        }
    }

    // ---- recording ------------------------------------------------------

    public string RecStart(string outputPath)
    {
        var writer = _recorder.Start(outputPath);
        _tap.SetWriter(writer);
        // If nobody is monitoring, we need our own puller to drive the mixer in real time.
        if (_monitorOut == null)
            StartPump();
        return _recorder.OutputPath!;
    }

    public string RecStop()
    {
        _tap.SetWriter(null); // tap stops touching the writer (lock-guarded inside)
        StopPump();
        return _recorder.Stop();
    }

    // ---- pull pumps -----------------------------------------------------
    // Exactly one puller drives the mixer at a time. The monitor WasapiOut is one;
    // when there is no monitor, a background thread pulls instead: the record pump
    // (while recording) or the meter-drive pump (while the live levels view is open).

    private void StartPump()
    {
        if (_pumpThread != null) return;
        _pumpStop = false;
        _pumpThread = new Thread(() => DrivePull(() => _pumpStop)) { IsBackground = true, Name = "RecordPump" };
        _pumpThread.Start();
    }

    private void StopPump()
    {
        if (_pumpThread == null) return;
        _pumpStop = true;
        _pumpThread.Join(2000);
        _pumpThread = null;
    }

    /// <summary>
    /// Drives the mixer for the live levels view so input peaks update even while idle.
    /// Returns false (no-op) when a monitor or the record pump is already pulling — metering
    /// is driven by them in that case, and two pullers must never run at once.
    /// </summary>
    public bool StartMeterDrive()
    {
        if (_monitorOut != null || _pumpThread != null) return false;
        if (_meterThread != null) return true;
        _meterStop = false;
        _meterThread = new Thread(() => DrivePull(() => _meterStop)) { IsBackground = true, Name = "MeterDrive" };
        _meterThread.Start();
        return true;
    }

    public void StopMeterDrive()
    {
        if (_meterThread == null) return;
        _meterStop = true;
        _meterThread.Join(2000);
        _meterThread = null;
    }

    /// <summary>
    /// Pull the tap at wall-clock rate (driving mixing + metering, plus the WAV write when a
    /// record writer is set), discarding the audio buffer, until <paramref name="stop"/> is set.
    /// </summary>
    private void DrivePull(Func<bool> stop)
    {
        int frames = MixFormat.SampleRate / 50;          // ~20 ms blocks
        int samples = frames * MixFormat.Channels;
        var buf = new float[samples];
        var sw = Stopwatch.StartNew();
        long producedFrames = 0;

        while (!stop())
        {
            int n = _tap.Read(buf, 0, samples);          // tap writes to the WAV as a side effect (if recording)
            producedFrames += n / MixFormat.Channels;

            // Throttle to wall-clock: ReadFully means the mixer never blocks, so without this
            // we would spin and (when recording) write silence far faster than real time.
            double targetMs = producedFrames * 1000.0 / MixFormat.SampleRate;
            double sleepMs = targetMs - sw.Elapsed.TotalMilliseconds;
            if (sleepMs > 1) Thread.Sleep((int)sleepMs);
        }
    }

    // ---- status ---------------------------------------------------------

    public void PrintStatus(TextWriter w)
    {
        w.WriteLine($"Mix format : {MixFormat.SampleRate} Hz, {MixFormat.Channels} ch, 32-bit float");
        w.WriteLine($"Monitor    : {(_monitorDeviceName ?? "off")}");
        if (_recorder.IsRecording)
        {
            var elapsed = DateTime.Now - _recorder.StartedAt;
            w.WriteLine($"Recording  : ON -> {_recorder.OutputPath}  ({elapsed:hh\\:mm\\:ss}, {_recorder.BytesWritten / (1024 * 1024)} MB temp WAV)");
        }
        else
        {
            w.WriteLine("Recording  : off");
        }
        w.WriteLine($"Inputs     : {_inputs.Count}");
        foreach (var s in _inputs.Values)
        {
            string state = s.Dead ? "DEAD" : s.Enabled ? "on " : "off";
            w.WriteLine($"  {s.Id}  [{state}]  {s.Kind,-8}  vol {s.Volume * 100,3:0}%  {s.DeviceName}");
        }
    }

    public void PrintLevels(TextWriter w)
    {
        if (_inputs.Count == 0) { w.WriteLine("(no inputs)"); return; }
        foreach (var s in _inputs.Values)
        {
            int bars = (int)Math.Round(Math.Clamp(s.LastPeak, 0f, 1f) * 30);
            w.WriteLine($"  {s.Id}  {new string('#', bars).PadRight(30)} {s.LastPeak * 100,5:0.0}%");
        }
    }

    // ---- teardown -------------------------------------------------------

    public void Dispose()
    {
        // Strict order: stop recording (finalise file) -> stop pullers -> stop captures.
        if (_recorder.IsRecording)
        {
            try { RecStop(); } catch { /* fall through to dispose */ }
        }
        StopMeterDrive();
        StopPump();
        StopMonitorInternal();

        foreach (var s in _inputs.Values)
            s.Dispose();
        _inputs.Clear();

        _recorder.Dispose();
    }
}

/// <summary>
/// Pass-through sample provider sitting between the mixer and its single puller.
/// While a writer is set, every block pulled through is also written to the WAV,
/// guaranteeing the recording is bit-identical to what the monitor hears.
/// </summary>
public sealed class RecordTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _writerLock = new();
    private WaveFileWriter? _writer;

    public RecordTapSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void SetWriter(WaveFileWriter? writer)
    {
        lock (_writerLock) _writer = writer;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            lock (_writerLock)
                _writer?.WriteSamples(buffer, offset, read);
        }
        return read;
    }
}
