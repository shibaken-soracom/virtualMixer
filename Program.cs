using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using VirtualMixer;

Console.OutputEncoding = System.Text.Encoding.UTF8;

MediaFoundationApi.Startup(); // required before EncodeToAac
var catalog = new DeviceCatalog();
var engine = new MixerEngine();

PrintBanner();

// Restore the previous session (must run BEFORE subscribing below, so the restore itself does
// not re-save — keeping any temporarily-missing devices in the file for next time).
if (args.Contains("--no-restore"))
    Console.WriteLine("(--no-restore: skipping session restore)");
else
    TryRestoreSession(catalog, engine);

// Print the device list after restore so the '*' marks reflect any restored inputs.
PrintDevices(catalog, engine);

// From here on, auto-save the live state on every change (atomic write; failures are non-fatal).
engine.StateChanged += () =>
{
    try { SessionStore.SaveSession(engine.ExportConfig()); }
    catch (Exception ex) { Console.WriteLine($"WARN: could not save session: {ex.Message}"); }
};

// Graceful Ctrl+C. A raw Ctrl+C hard-kills the process, so the REPL's `finally`
// (engine.Dispose) never runs — abandoning an in-progress recording as an unfinalised
// temp WAV whose RIFF/data sizes are still 0, i.e. unplayable. Intercept it and run the
// same clean shutdown as `quit`: engine.Dispose() finalises the WAV and encodes the M4A.
// A second Ctrl+C while that is in progress falls through to the default (force-quit).
var shuttingDown = 0;
Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Exchange(ref shuttingDown, 1) != 0) return; // 2nd Ctrl+C -> let it terminate
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine(engine.IsRecording
        ? "^C — finishing the recording (encoding to M4A) and shutting down... (Ctrl+C again to force-quit)"
        : "^C — shutting down...");
    try { engine.Dispose(); }
    catch (Exception ex) { Console.WriteLine($"WARN: shutdown: {ex.Message}"); }
    try { catalog.Dispose(); } catch { }
    try { MediaFoundationApi.Shutdown(); } catch { }
    Environment.Exit(0);
};

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

/// <summary>Print the device catalog, marking devices currently added as mixer inputs with '*'.</summary>
static void PrintDevices(DeviceCatalog catalog, MixerEngine engine) =>
    catalog.Print(Console.Out, engine.InputDeviceIds);

static void PrintHelp()
{
    Console.WriteLine("""
    Commands:
      devices                       list render (R0..) and capture (C0..) devices
      refresh                       re-enumerate devices (after plugging/unplugging)
      add-input loopback [Rn]       add PC-playback loopback input (default = default render)
      add-input mic Cn              add a microphone / line capture input
      inputs                        list current mixer inputs (id / state / volume / device)
      mute <id>                     drop an input out of the mix (keeps it; un-mute is instant)
      unmute <id>                   route a muted input back into the mix
      remove-input <id>             remove an input entirely (closes its capture device)
      vol <id> <0-200>              set input volume (100 = unity, up to 200 = +6 dB boost)
      monitor Rn                    play the mix out to render device Rn (to hear the balance)
      monitor off                   stop monitoring
      rec start [filename]          start recording into recordings/ (.m4a added if missing)
      rec stop                      stop recording -> encodes to M4A (AAC)
      explorer                      open the recordings/ folder in Explorer
      status                        show engine status
      levels                        live input meters (Esc/Enter/q to return)
      complete <text>               show Tab-completion candidates for a partial command
      save <name>                   save current inputs + monitor as a preset
      load <name>                   load a preset (replaces current setup)
      presets                       list saved presets
      forget                        clear the auto-saved session (next launch starts empty)
      help                          this help
      quit | exit                   shut down cleanly

    Press <Tab> to auto-complete commands, sub-commands, device ids (R0/C0..) and input ids.
    The mixer auto-saves your setup and restores it on next launch ('forget' to clear,
    run with --no-restore to skip restoring once).
    """);
}

/// <summary>
/// Restore the auto-saved session at startup. Never throws: a missing/corrupt/newer file degrades
/// to an empty mix with a clear message. Reuses <see cref="MixerEngine.ApplyConfig"/>, which already
/// skips unavailable devices gracefully.
/// </summary>
static void TryRestoreSession(DeviceCatalog catalog, MixerEngine engine)
{
    switch (SessionStore.TryLoadSession(out var cfg, out var detail))
    {
        case SessionLoadStatus.None:
            return; // first run / cleared — start with an empty mix, nothing to report

        case SessionLoadStatus.Corrupt:
            SessionStore.BackupCorrupt();
            Console.WriteLine($"WARN: previous session is unreadable ({detail}) — ignored, " +
                              $"moved to {SessionStore.Path}.bak. Starting with an empty mix.");
            return;

        case SessionLoadStatus.TooNew:
            Console.WriteLine($"WARN: session file is from a newer version ({detail}) — not loaded " +
                              "to avoid misconfiguration (file kept). Starting with an empty mix.");
            return;

        case SessionLoadStatus.Ok:
            int total = cfg!.Inputs.Count;
            Console.WriteLine($"Restoring previous session ({total} input{(total == 1 ? "" : "s")})...");
            engine.ApplyConfig(cfg, catalog, Console.Out);
            int restored = engine.Inputs.Count;
            if (restored < total)
                Console.WriteLine($"  {restored}/{total} inputs restored " +
                                  $"({total - restored} skipped — device(s) not connected; kept in session for next time)");
            return;
    }
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
                    PrintDevices(catalog, engine);
                    break;

                case "refresh":
                    catalog.Refresh();
                    PrintDevices(catalog, engine);
                    break;

                case "add-input":
                    HandleAddInput(catalog, engine, parts);
                    break;

                case "inputs":
                    foreach (var s in engine.Inputs)
                        Console.WriteLine($"  {s.Id}  [{(s.Dead ? "DEAD" : s.Enabled ? "on" : "muted")}]  {s.Kind,-8}  vol {s.Volume * 100:0}%  {s.DeviceName}");
                    if (!engine.Inputs.Any()) Console.WriteLine("  (no inputs — use 'add-input')");
                    break;

                case "mute":
                    Require(parts.Length == 2, "usage: mute <id>");
                    engine.Enable(parts[1], false);
                    Console.WriteLine($"  {parts[1]} muted");
                    break;

                case "unmute":
                    Require(parts.Length == 2, "usage: unmute <id>");
                    engine.Enable(parts[1], true);
                    Console.WriteLine($"  {parts[1]} unmuted");
                    break;

                case "remove-input":
                    Require(parts.Length == 2, "usage: remove-input <id>");
                    engine.RemoveInput(parts[1]);
                    Console.WriteLine($"  removed {parts[1]}");
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

                case "explorer":
                    OpenRecordingsFolder();
                    break;

                case "status":
                    engine.PrintStatus(Console.Out);
                    break;

                case "levels":
                    LiveLevels(engine);
                    break;

                case "complete":
                    HandleComplete(catalog, engine, line);
                    break;

                case "save":
                    Require(parts.Length == 2, "usage: save <name>");
                    Console.WriteLine($"  saved: {PresetStore.Save(parts[1], engine.ExportConfig())}");
                    break;

                case "load":
                    Require(parts.Length == 2, "usage: load <name>");
                    Console.WriteLine($"  loading preset '{parts[1]}'...");
                    engine.ApplyConfig(PresetStore.Load(parts[1]), catalog, Console.Out);
                    break;

                case "presets":
                    var presetNames = PresetStore.List();
                    Console.WriteLine(presetNames.Count == 0 ? "  (no presets)" : "  " + string.Join("   ", presetNames));
                    break;

                case "forget":
                    SessionStore.ClearSession();
                    Console.WriteLine("  session cleared — next launch starts with an empty mix");
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
    Require(parts.Length >= 2, "usage: rec start [filename] | rec stop");
    switch (parts[1].ToLowerInvariant())
    {
        case "start":
            // The argument is a *filename*, not a path: always save under recordings/,
            // and ensure it carries the .m4a extension.
            string fileName = parts.Length >= 3
                ? EnsureM4aExtension(Path.GetFileName(parts[2]))
                : $"mix_{DateTime.Now:yyyyMMdd_HHmmss}.m4a";
            Require(fileName.Length > 0, "usage: rec start [filename] | rec stop");
            string full = engine.RecStart(Path.Combine("recordings", fileName));
            Console.WriteLine($"  recording -> {full}");
            break;

        case "stop":
            Console.WriteLine("  encoding to M4A...");
            string outPath = engine.RecStop();
            Console.WriteLine($"  saved: {outPath}");
            break;

        default:
            throw new ArgumentException("usage: rec start [filename] | rec stop");
    }
}

/// <summary>Appends ".m4a" unless the name already ends with it (case-insensitive).</summary>
static string EnsureM4aExtension(string fileName) =>
    fileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".m4a";

/// <summary>Opens the recordings/ folder in Explorer (creating it first if missing).</summary>
static void OpenRecordingsFolder()
{
    string dir = Path.GetFullPath("recordings");
    Directory.CreateDirectory(dir); // make sure it exists so Explorer doesn't error
    // Shell-open the folder path -> opens in the default file manager (Explorer),
    // sidestepping explorer.exe's argument-quoting quirks.
    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    Console.WriteLine($"  opening {dir}");
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

// ---- live levels ------------------------------------------------------------

/// <summary>
/// Live input meters that refresh in place until the user presses Esc / Enter / q.
/// Falls back to a single snapshot when stdin is redirected (no interactive console).
/// </summary>
static void LiveLevels(MixerEngine engine)
{
    if (Console.IsInputRedirected)
    {
        engine.PrintLevels(Console.Out);
        return;
    }

    // Drive the mixer while the view is open so peaks update even when idle
    // (no-op if a monitor or recording is already pulling the mixer).
    bool drove = engine.StartMeterDrive();
    bool cursorWasVisible = true;
    try { cursorWasVisible = Console.CursorVisible; } catch { }
    try { Console.CursorVisible = false; } catch { }

    int width = SafeWidth();
    // Layout: "  " + id(8) + " " + bar + "  " + "-xx.x dB" + "  " + device
    int barWidth = Math.Clamp(width - 32, 12, 48);
    int nameWidth = Math.Max(0, width - (2 + 8 + 1 + barWidth + 2 + 8 + 2));

    // Inputs can't be added/removed while this modal view is open, so the row
    // count is fixed: one row per input (or a single "no inputs" row).
    int rows = Math.Max(1, engine.Inputs.Count);

    Console.WriteLine("Live levels (dBFS) — green ok / yellow hot / red clip — Esc, Enter or q to return");
    // Reserve the block here so the buffer scrolls at most once now; afterwards we
    // only ever overwrite these fixed rows in place (never print newlines into them).
    for (int i = 0; i < rows; i++) Console.WriteLine();
    int top = Console.CursorTop - rows;

    try
    {
        while (true)
        {
            var inputs = engine.Inputs.ToList();
            for (int i = 0; i < rows; i++)
            {
                Console.SetCursorPosition(0, top + i);
                if (inputs.Count == 0)
                    WriteCell("  (no inputs — add one with 'add-input')", width);
                else if (i < inputs.Count)
                    DrawMeterRow(inputs[i], barWidth, nameWidth, width);
                else
                    WriteCell("", width);
            }

            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(intercept: true).Key;
                if (k is ConsoleKey.Escape or ConsoleKey.Enter or ConsoleKey.Q)
                    break;
            }
            Thread.Sleep(100);
        }
    }
    finally
    {
        if (drove) engine.StopMeterDrive();
        try { Console.ResetColor(); } catch { }
        try { Console.SetCursorPosition(0, top + rows); } catch { }
        try { Console.CursorVisible = cursorWasVisible; } catch { }
        Console.WriteLine();
    }
}

/// <summary>Draw one coloured meter row in place: id, dB-scaled bar (green/yellow/red), dBFS, device.</summary>
static void DrawMeterRow(InputSource s, int barWidth, int nameWidth, int totalWidth)
{
    float peak = Math.Clamp(s.LastPeak, 0f, 1f);
    int filled = (int)Math.Round(Meter.Fraction(peak) * barWidth);
    string label = s.Enabled ? s.Id : $"{s.Id}!";   // '!' marks a muted input

    Console.Write($"  {label,-8} ");

    var prev = Console.ForegroundColor;
    for (int i = 0; i < barWidth; i++)
    {
        double f = (i + 1) / (double)barWidth;
        if (i < filled)
        {
            Console.ForegroundColor = f <= 0.6 ? ConsoleColor.Green
                                    : f <= 0.85 ? ConsoleColor.Yellow
                                    : ConsoleColor.Red;
            Console.Write('█');
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write('░');
        }
    }
    Console.ForegroundColor = prev;

    string name = s.DeviceName.Length > nameWidth && nameWidth > 1
        ? s.DeviceName[..(nameWidth - 1)] + "…"
        : s.DeviceName;
    string tail = $"  {Meter.DbText(peak)} dB  {name}";
    // Pad to fill the row (no newline) so a longer previous frame is erased without scrolling.
    int used = 2 + 8 + 1 + barWidth;
    WriteCell(tail, totalWidth - used);
}

static int SafeWidth()
{
    try { return Math.Clamp(Console.BufferWidth - 1, 40, 120); } catch { return 80; }
}

/// <summary>Write text padded/truncated to exactly <paramref name="width"/> cells, WITHOUT a trailing newline.</summary>
static void WriteCell(string text, int width)
{
    if (width <= 0) return;
    if (text.Length < width) text = text.PadRight(width);
    else if (text.Length > width) text = text[..width];
    Console.Write(text);
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

        case "mute":
        case "unmute":
        case "remove-input":
            if (slot == 1) return InputIds(engine);
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

        case "save":
        case "load":
            if (slot == 1) return PresetStore.List();
            return Enumerable.Empty<string>();

        default:
            return Enumerable.Empty<string>();
    }
}

static string[] AllCommands() => new[]
{
    "devices", "refresh", "add-input", "inputs", "mute", "unmute", "remove-input", "vol",
    "monitor", "rec", "explorer", "status", "levels", "complete", "save", "load", "presets", "forget", "help", "quit", "exit",
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
