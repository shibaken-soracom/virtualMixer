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
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

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
