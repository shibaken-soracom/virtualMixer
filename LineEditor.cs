using System.Text;

namespace VirtualMixer;

/// <summary>
/// Minimal interactive line reader with Tab completion for the REPL.
///
/// Completion is context-aware: the caller supplies a function that, given the current
/// tokens (the last element being the partial token under the cursor), returns the
/// candidate strings valid in that slot. Tab then either completes a unique match,
/// extends to the longest common prefix, or lists the candidates.
///
/// When stdin is redirected (piped/scripted input) there is no console to read keys
/// from, so ReadLine falls back to <see cref="Console.ReadLine()"/> — completion is an
/// interactive-only nicety and never breaks automation.
/// </summary>
public sealed class LineEditor
{
    private readonly Func<IReadOnlyList<string>, IEnumerable<string>> _complete;

    public LineEditor(Func<IReadOnlyList<string>, IEnumerable<string>> completer)
        => _complete = completer;

    public string? ReadLine(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        var buf = new StringBuilder();
        int cursor = 0;   // insertion index within buf
        int lastLen = 0;  // length of buf at previous render (to erase leftovers)

        void Render()
        {
            Console.CursorLeft = 0;
            string text = prompt + buf;
            int pad = Math.Max(0, prompt.Length + lastLen - text.Length);
            Console.Write(text + new string(' ', pad));
            lastLen = buf.Length;
            Console.CursorLeft = Math.Min(prompt.Length + cursor, Math.Max(0, Console.BufferWidth - 1));
        }

        void Complete()
        {
            var tokens = Tokenize(buf.ToString());
            string partial = tokens[^1];
            var matches = Match(_complete(tokens), partial);

            if (matches.Count == 0)
                return; // nothing to offer

            if (matches.Count == 1)
            {
                ReplaceTail(buf, ref cursor, partial, matches[0] + " ");
                return;
            }

            string lcp = LongestCommonPrefix(matches);
            if (lcp.Length > partial.Length)
            {
                ReplaceTail(buf, ref cursor, partial, lcp);
            }
            else
            {
                // Ambiguous and no further common prefix: list the options.
                Console.WriteLine();
                Console.WriteLine("  " + string.Join("   ", matches));
                lastLen = 0; // we are on a fresh line; nothing to erase
            }
        }

        Render();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buf.ToString();

                case ConsoleKey.Backspace:
                    if (cursor > 0) { buf.Remove(--cursor, 1); Render(); }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buf.Length) { buf.Remove(cursor, 1); Render(); }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0) { cursor--; Console.CursorLeft = prompt.Length + cursor; }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buf.Length) { cursor++; Console.CursorLeft = prompt.Length + cursor; }
                    break;

                case ConsoleKey.Home:
                    cursor = 0; Console.CursorLeft = prompt.Length;
                    break;

                case ConsoleKey.End:
                    cursor = buf.Length; Console.CursorLeft = prompt.Length + cursor;
                    break;

                case ConsoleKey.Tab:
                    Complete();
                    Render();
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Insert(cursor++, key.KeyChar);
                        Render();
                    }
                    break;
            }
        }
    }

    // ---- completion helpers (static + testable) -------------------------

    /// <summary>
    /// Split a command line into tokens, where the LAST element is the token currently
    /// being completed (empty string when the line is empty or ends with a space).
    /// </summary>
    public static List<string> Tokenize(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (line.Length == 0 || line.EndsWith(' '))
            tokens.Add(string.Empty); // a fresh, empty slot to complete
        if (tokens.Count == 0)
            tokens.Add(string.Empty);
        return tokens;
    }

    /// <summary>Candidates that start with <paramref name="partial"/> (case-insensitive), de-duplicated.</summary>
    public static List<string> Match(IEnumerable<string> candidates, string partial) =>
        candidates
            .Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

    public static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return string.Empty;
        string prefix = values[0];
        foreach (var v in values.Skip(1))
        {
            int i = 0;
            int max = Math.Min(prefix.Length, v.Length);
            while (i < max && char.ToLowerInvariant(prefix[i]) == char.ToLowerInvariant(v[i]))
                i++;
            prefix = prefix[..i];
            if (prefix.Length == 0) break;
        }
        return prefix;
    }

    private static void ReplaceTail(StringBuilder buf, ref int cursor, string partial, string replacement)
    {
        if (partial.Length > 0)
            buf.Remove(buf.Length - partial.Length, partial.Length);
        buf.Append(replacement);
        cursor = buf.Length;
    }
}
