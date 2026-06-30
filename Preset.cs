using System.Text.Json;

namespace VirtualMixer;

/// <summary>A device reference stored in a preset: stable endpoint id plus friendly name (fallback).</summary>
public record DeviceRef(string Id, string Name);

/// <summary>One saved mixer input.</summary>
public record InputConfig(string Kind, string DeviceId, string DeviceName, float Volume, bool Enabled);

/// <summary>A saved favourite mixer configuration: the inputs and the monitor selection.</summary>
public record MixerConfig(int Version, DeviceRef? Monitor, List<InputConfig> Inputs);

/// <summary>Reads/writes named preset JSON files under the <c>presets/</c> folder.</summary>
public static class PresetStore
{
    public const string Dir = "presets";
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Save(string name, MixerConfig cfg)
    {
        string path = PathFor(name);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Options));
        return Path.GetFullPath(path);
    }

    public static MixerConfig Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"preset '{name}' not found ({path})");
        return JsonSerializer.Deserialize<MixerConfig>(File.ReadAllText(path), Options)
               ?? throw new InvalidDataException($"preset '{name}' is empty or invalid");
    }

    public static List<string> List()
    {
        if (!Directory.Exists(Dir)) return new List<string>();
        return Directory.EnumerateFiles(Dir, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("preset name required");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"invalid preset name '{name}'");
        return Path.Combine(Dir, name + ".json");
    }
}

/// <summary>Outcome of trying to load the auto-saved session file at startup.</summary>
public enum SessionLoadStatus
{
    /// <summary>No session file exists (first run, or it was cleared).</summary>
    None,
    /// <summary>A valid session was loaded and is ready to apply.</summary>
    Ok,
    /// <summary>The file exists but could not be read/parsed (truncated, bad JSON, IO error).</summary>
    Corrupt,
    /// <summary>The file is from a newer schema version than this build understands.</summary>
    TooNew,
}

/// <summary>
/// Persists and restores the "last session" — the live mixer state, auto-snapshotted on every
/// change and reloaded on next startup. Stored as a single <c>session.json</c> in the working
/// directory, deliberately outside <c>presets/</c> so it never shows up in the <c>presets</c> list.
/// Reuses <see cref="MixerConfig"/> and <see cref="PresetStore.Options"/> for serialisation.
/// </summary>
public static class SessionStore
{
    public const string Path = "session.json";
    public const int CurrentVersion = 1;
    private static readonly string TmpPath = Path + ".tmp";
    private static readonly string BakPath = Path + ".bak";

    /// <summary>
    /// Atomically write the session: serialise to a temp file, then replace the live file.
    /// A crash mid-write leaves the previous <c>session.json</c> intact rather than truncated.
    /// </summary>
    public static void SaveSession(MixerConfig cfg)
    {
        File.WriteAllText(TmpPath, JsonSerializer.Serialize(cfg, PresetStore.Options));
        File.Move(TmpPath, Path, overwrite: true);
    }

    /// <summary>
    /// Attempt to load the session file. Never throws — a bad file must not block startup.
    /// <paramref name="cfg"/> is non-null only for <see cref="SessionLoadStatus.Ok"/>;
    /// <paramref name="detail"/> carries a human-readable reason for Corrupt/TooNew.
    /// </summary>
    public static SessionLoadStatus TryLoadSession(out MixerConfig? cfg, out string? detail)
    {
        cfg = null;
        detail = null;
        if (!File.Exists(Path))
            return SessionLoadStatus.None;
        try
        {
            var loaded = JsonSerializer.Deserialize<MixerConfig>(File.ReadAllText(Path), PresetStore.Options);
            if (loaded == null)
            {
                detail = "file is empty or invalid";
                return SessionLoadStatus.Corrupt;
            }
            if (loaded.Version > CurrentVersion)
            {
                detail = $"v{loaded.Version}";
                return SessionLoadStatus.TooNew;
            }
            cfg = loaded;
            return SessionLoadStatus.Ok;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return SessionLoadStatus.Corrupt;
        }
    }

    /// <summary>Delete the session file (used by the <c>forget</c> command). No-op if absent.</summary>
    public static void ClearSession()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }

    /// <summary>
    /// Move a corrupt session aside to <c>session.json.bak</c> so it is neither retried on the next
    /// launch nor silently overwritten, and remains available for inspection. Best effort.
    /// </summary>
    public static void BackupCorrupt()
    {
        try
        {
            if (File.Exists(Path)) File.Move(Path, BakPath, overwrite: true);
        }
        catch { /* best effort — a failed backup must not block startup */ }
    }
}
