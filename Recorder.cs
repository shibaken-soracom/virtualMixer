using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.MediaFoundation;

namespace VirtualMixer;

/// <summary>
/// Owns the recording file lifecycle: writes a temp WAV in real time (fed by the mixer
/// tap), then on stop finalises the WAV and transcodes it to a compressed M4A (AAC) via
/// the Windows-built-in Media Foundation encoder, deleting the temp WAV afterwards.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly WaveFormat _format;
    private readonly int _bitrate;

    private WaveFileWriter? _writer;
    private string? _tmpPath;

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }
    public DateTime StartedAt { get; private set; }

    /// <summary>Bytes written to the current temp WAV (for the ~4 GB RIFF limit check).</summary>
    public long BytesWritten => _writer?.Length ?? 0;

    public Recorder(WaveFormat format, int bitrate = 192000)
    {
        _format = format;
        _bitrate = bitrate;
    }

    /// <summary>Opens the temp WAV and returns the writer the tap should feed.</summary>
    public WaveFileWriter Start(string outputPath)
    {
        if (IsRecording) throw new InvalidOperationException("already recording");

        OutputPath = Path.GetFullPath(outputPath);
        _tmpPath = Path.ChangeExtension(OutputPath, ".tmp.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(_tmpPath)!);

        _writer = new WaveFileWriter(_tmpPath, _format);
        StartedAt = DateTime.Now;
        IsRecording = true;
        return _writer;
    }

    /// <summary>Finalises the WAV, encodes to AAC/M4A, deletes the temp WAV. Returns the final path.</summary>
    public string Stop()
    {
        if (!IsRecording || _writer is null || _tmpPath is null || OutputPath is null)
            throw new InvalidOperationException("not recording");

        IsRecording = false;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;

        // Encode temp WAV -> AAC (.m4a). The AAC encoder MFT wants 16-bit PCM input,
        // so convert the float WAV down with SampleToWaveProvider16 first.
        using (var reader = new AudioFileReader(_tmpPath))
        {
            var pcm16 = new SampleToWaveProvider16(reader);
            MediaFoundationEncoder.EncodeToAac(pcm16, OutputPath, _bitrate);
        }

        TryDeleteTemp();
        return OutputPath;
    }

    private void TryDeleteTemp()
    {
        try
        {
            if (_tmpPath is not null && File.Exists(_tmpPath))
                File.Delete(_tmpPath);
        }
        catch { /* leave the temp WAV if it is locked; not fatal */ }
    }

    public void Dispose()
    {
        // If we are torn down mid-recording, keep the raw WAV as a salvage copy
        // (don't silently lose audio); just finalise the writer.
        if (_writer is not null)
        {
            try { _writer.Flush(); _writer.Dispose(); } catch { }
            _writer = null;
        }
    }
}
