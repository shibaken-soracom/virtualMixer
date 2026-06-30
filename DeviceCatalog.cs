using NAudio.CoreAudioApi;

namespace VirtualMixer;

/// <summary>
/// Thin wrapper over MMDeviceEnumerator that snapshots the active render/capture
/// endpoints and lets the CLI reference them by a stable index (R0/R1.., C0/C1..).
/// </summary>
public sealed class DeviceCatalog : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<MMDevice> RenderDevices { get; private set; } = Array.Empty<MMDevice>();
    public IReadOnlyList<MMDevice> CaptureDevices { get; private set; } = Array.Empty<MMDevice>();
    public string? DefaultRenderId { get; private set; }

    public DeviceCatalog() => Refresh();

    public void Refresh()
    {
        RenderDevices = _enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();
        CaptureDevices = _enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();
        DefaultRenderId = TryGetDefaultId(DataFlow.Render);
    }

    private string? TryGetDefaultId(DataFlow flow)
    {
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return dev.ID;
        }
        catch
        {
            return null; // no default endpoint of this kind
        }
    }

    public MMDevice? Render(int index) =>
        index >= 0 && index < RenderDevices.Count ? RenderDevices[index] : null;

    public MMDevice? Capture(int index) =>
        index >= 0 && index < CaptureDevices.Count ? CaptureDevices[index] : null;

    /// <summary>Default render endpoint resolved to a concrete device (for loopback + feedback guard).</summary>
    public MMDevice? DefaultRender() =>
        DefaultRenderId is null ? null : RenderDevices.FirstOrDefault(d => d.ID == DefaultRenderId);

    /// <summary>Resolve a render device by stable endpoint id, falling back to friendly name.</summary>
    public MMDevice? FindRender(string id, string name) => FindIn(RenderDevices, id, name);

    /// <summary>Resolve a capture device by stable endpoint id, falling back to friendly name.</summary>
    public MMDevice? FindCapture(string id, string name) => FindIn(CaptureDevices, id, name);

    private static MMDevice? FindIn(IReadOnlyList<MMDevice> list, string id, string name) =>
        list.FirstOrDefault(d => d.ID == id) ?? list.FirstOrDefault(d => d.FriendlyName == name);

    /// <summary>
    /// Print the render/capture device lists. Devices whose endpoint id is in
    /// <paramref name="addedInputIds"/> (i.e. currently added to the mixer via add-input) are
    /// marked with '*'. Loopback inputs carry a render id and mic inputs a capture id, so a single
    /// set marks each device in the correct section.
    /// </summary>
    public void Print(TextWriter w, IReadOnlySet<string> addedInputIds)
    {
        w.WriteLine("Render devices (use for loopback source / monitor output):");
        for (int i = 0; i < RenderDevices.Count; i++)
        {
            var d = RenderDevices[i];
            string mark = addedInputIds.Contains(d.ID) ? " *" : "  ";
            w.WriteLine($"  R{i}{mark} {d.FriendlyName}");
        }
        w.WriteLine("Capture devices (use for mic / line input):");
        for (int i = 0; i < CaptureDevices.Count; i++)
        {
            var d = CaptureDevices[i];
            string mark = addedInputIds.Contains(d.ID) ? " *" : "  ";
            w.WriteLine($"  C{i}{mark} {d.FriendlyName}");
        }
        w.WriteLine("  (* = added as input)");
    }

    public void Dispose() => _enumerator.Dispose();
}
