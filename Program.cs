using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using VirtualMixer;

Console.OutputEncoding = System.Text.Encoding.UTF8;

MediaFoundationApi.Startup(); // required before EncodeToAac
var catalog = new DeviceCatalog();
var engine = new MixerEngine();

PrintBanner();
catalog.Print(Console.Out);

try
{
    Repl(catalog, engine);
}
finally
{
    engine.Dispose();
    catalog.Dispose();
    MediaFoundationApi.Shutdown();
}
return;

// ---------------------------------------------------------------------------

static void PrintBanner()
{
    Console.WriteLine();
    Console.WriteLine("=== virtualMixer — loopback mixer (record / monitor) ===");
    Console.WriteLine("Mix PC playback (loopback) + mic(s), balance volumes live, record to M4A.");
    Console.WriteLine("Type 'help' for commands. NOTE: do not monitor through the same device you");
    Console.WriteLine("loopback-capture (e.g. monitor on headphones, capture the speakers) — feedback.");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine("""
    Commands:
      devices                       list render (R0..) and capture (C0..) devices
      refresh                       re-enumerate devices (after plugging/unplugging)
      add-input loopback [Rn]       add PC-playback loopback input (default = default render)
      add-input mic Cn              add a microphone / line capture input
      inputs                        list current mixer inputs (id / state / volume / device)
      enable <id> on|off            route an input into the mix or mute it out
      vol <id> <0-200>              set input volume (100 = unity, up to 200 = +6 dB boost)
      monitor Rn                    play the mix out to render device Rn (to hear the balance)
      monitor off                   stop monitoring
      rec start [path]              start recording (default: recordings/mix_yyyyMMdd_HHmmss.m4a)
      rec stop                      stop recording -> encodes to M4A (AAC)
      status                        show engine status
      levels                        show per-input peak meters
      complete <text>               show Tab-completion candidates for a partial command
      help                          this help
      quit | exit                   shut down cleanly

    Press <Tab> to auto-complete commands, sub-commands, device ids (R0/C0..) and input ids.
    """);
}

static void Repl(DeviceCatalog catalog, MixerEngine engine)
{
    PrintHelp();
    var editor = new LineEditor(tokens => CompletionCandidates(tokens, catalog, engine));
    while (true)
    {
        string? line = editor.ReadLine("> ");
        if (line is null) break; // EOF (e.g. piped input ended)
        line = line.Replace("﻿", ""); // strip stray BOM from piped/redirected UTF-8 input
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) continue;

        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "devices":
                    catalog.Print(Console.Out);
                    break;

                case "refresh":
                    catalog.Refresh();
                    catalog.Print(Console.Out);
                    break;

                case "add-input":
                    HandleAddInput(catalog, engine, parts);
                    break;

                case "inputs":
                    foreach (var s in engine.Inputs)
                        Console.WriteLine($"  {s.Id}  [{(s.Dead ? "DEAD" : s.Enabled ? "on" : "off")}]  {s.Kind,-8}  vol {s.Volume * 100:0}%  {s.DeviceName}");
                    if (!engine.Inputs.Any()) Console.WriteLine("  (no inputs — use 'add-input')");
                    break;

                case "enable":
                    Require(parts.Length == 3, "usage: enable <id> on|off");
                    engine.Enable(parts[1], parts[2].Equals("on", StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine($"  {parts[1]} -> {parts[2]}");
                    break;

                case "vol":
                    Require(parts.Length == 3 && float.TryParse(parts[2], out _), "usage: vol <id> <0-200>");
                    float pct = float.Parse(parts[2]);
                    engine.SetVolume(parts[1], pct / 100f);
                    Console.WriteLine($"  {parts[1]} volume -> {pct:0}%");
                    break;

                case "monitor":
                    HandleMonitor(catalog, engine, parts);
                    break;

                case "rec":
                    HandleRec(engine, parts);
                    break;

                case "status":
                    engine.PrintStatus(Console.Out);
                    break;

                case "levels":
                    engine.PrintLevels(Console.Out);
                    break;

                case "complete":
                    HandleComplete(catalog, engine, line);
                    break;

                case "help":
                    PrintHelp();
                    break;

                case "quit":
                case "exit":
                    Console.WriteLine("shutting down...");
                    return;

                default:
                    Console.WriteLine($"unknown command '{parts[0]}' (type 'help')");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
        }
    }
}

static void HandleAddInput(DeviceCatalog catalog, MixerEngine engine, string[] parts)
{
    Require(parts.Length >= 2, "usage: add-input loopback [Rn] | add-input mic Cn");
    switch (parts[1].ToLowerInvariant())
    {
        case "loopback":
            MMDevice render = parts.Length >= 3
                ? ResolveRender(catalog, parts[2])
                : catalog.DefaultRender() ?? throw new InvalidOperationException("no default render device");
            var lb = engine.AddInput(SourceKind.Loopback, render);
            Console.WriteLine($"  added {lb.Id} (loopback) <- {lb.DeviceName}");
            break;

        case "mic":
            Require(parts.Length == 3, "usage: add-input mic Cn");
            MMDevice cap = ResolveCapture(catalog, parts[2]);
            var mic = engine.AddInput(SourceKind.Capture, cap);
            Console.WriteLine($"  added {mic.Id} (mic) <- {mic.DeviceName}");
            break;

        default:
            throw new ArgumentException("usage: add-input loopback [Rn] | add-input mic Cn");
    }
}

static void HandleMonitor(DeviceCatalog catalog, MixerEngine engine, string[] parts)
{
    Require(parts.Length == 2, "usage: monitor Rn | monitor off");
    if (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))
    {
        engine.MonitorOff();
        Console.WriteLine("  monitor off");
        return;
    }
    MMDevice render = ResolveRender(catalog, parts[1]);
    engine.SetMonitor(render);
    Console.WriteLine($"  monitoring -> {render.FriendlyName}");
}

static void HandleRec(MixerEngine engine, string[] parts)
{
    Require(parts.Length >= 2, "usage: rec start [path] | rec stop");
    switch (parts[1].ToLowerInvariant())
    {
        case "start":
            string path = parts.Length >= 3
                ? parts[2]
                : Path.Combine("recordings", $"mix_{DateTime.Now:yyyyMMdd_HHmmss}.m4a");
            string full = engine.RecStart(path);
            Console.WriteLine($"  recording -> {full}");
            break;

        case "stop":
            Console.WriteLine("  encoding to M4A...");
            string outPath = engine.RecStop();
            Console.WriteLine($"  saved: {outPath}");
            break;

        default:
            throw new ArgumentException("usage: rec start [path] | rec stop");
    }
}

static MMDevice ResolveRender(DeviceCatalog catalog, string token)
{
    int idx = ParseIndex(token, 'R');
    return catalog.Render(idx) ?? throw new ArgumentException($"no render device R{idx} (try 'devices')");
}

static MMDevice ResolveCapture(DeviceCatalog catalog, string token)
{
    int idx = ParseIndex(token, 'C');
    return catalog.Capture(idx) ?? throw new ArgumentException($"no capture device C{idx} (try 'devices')");
}

static int ParseIndex(string token, char prefix)
{
    string t = token.Trim();
    if (t.Length >= 2 && char.ToUpperInvariant(t[0]) == prefix && int.TryParse(t[1..], out int i))
        return i;
    if (int.TryParse(t, out int j)) return j; // also accept a bare number
    throw new ArgumentException($"expected a device like '{prefix}0', got '{token}'");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new ArgumentException(message);
}

// ---- Tab completion ---------------------------------------------------------

/// <summary>
/// Returns the candidate strings valid for the token currently being completed
/// (the last element of <paramref name="tokens"/>), based on the command context.
/// </summary>
static IEnumerable<string> CompletionCandidates(IReadOnlyList<string> tokens, DeviceCatalog catalog, MixerEngine engine)
{
    int slot = tokens.Count - 1; // index of the token being completed
    if (slot == 0)
        return AllCommands();

    switch (tokens[0].ToLowerInvariant())
    {
        case "add-input":
            if (slot == 1) return new[] { "loopback", "mic" };
            if (slot == 2)
                return tokens[1].ToLowerInvariant() switch
                {
                    "loopback" => RenderTokens(catalog),
                    "mic" => CaptureTokens(catalog),
                    _ => Enumerable.Empty<string>(),
                };
            return Enumerable.Empty<string>();

        case "enable":
            if (slot == 1) return InputIds(engine);
            if (slot == 2) return new[] { "on", "off" };
            return Enumerable.Empty<string>();

        case "vol":
            if (slot == 1) return InputIds(engine);
            return Enumerable.Empty<string>();

        case "monitor":
            if (slot == 1) return RenderTokens(catalog).Append("off");
            return Enumerable.Empty<string>();

        case "rec":
            if (slot == 1) return new[] { "start", "stop" };
            return Enumerable.Empty<string>();

        default:
            return Enumerable.Empty<string>();
    }
}

static string[] AllCommands() => new[]
{
    "devices", "refresh", "add-input", "inputs", "enable", "vol",
    "monitor", "rec", "status", "levels", "complete", "help", "quit", "exit",
};

static IEnumerable<string> RenderTokens(DeviceCatalog c) =>
    Enumerable.Range(0, c.RenderDevices.Count).Select(i => "R" + i);

static IEnumerable<string> CaptureTokens(DeviceCatalog c) =>
    Enumerable.Range(0, c.CaptureDevices.Count).Select(i => "C" + i);

static IEnumerable<string> InputIds(MixerEngine e) =>
    e.Inputs.Select(s => s.Id);

/// <summary>Implements the `complete &lt;text&gt;` command: prints what Tab would offer for the rest of the line.</summary>
static void HandleComplete(DeviceCatalog catalog, MixerEngine engine, string line)
{
    // Strip the leading "complete" verb but keep the remainder verbatim (a trailing
    // space matters — it opens a fresh argument slot).
    string rest = line.Length > "complete".Length ? line["complete".Length..] : "";
    if (rest.StartsWith(' ')) rest = rest[1..];

    var tokens = LineEditor.Tokenize(rest);
    var matches = LineEditor.Match(CompletionCandidates(tokens, catalog, engine), tokens[^1]);
    Console.WriteLine(matches.Count == 0 ? "  (no candidates)" : "  " + string.Join("   ", matches));
}
