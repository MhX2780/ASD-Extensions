using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AppStore;

internal sealed record AppRow(string Name, string Id, string Version, string Available, string Source);

/// <param name="Fraction">0..1, or -1 when the amount is not known</param>
internal readonly record struct WingetProgress(double Fraction, string Text);

/// <summary>Runs winget (Windows' package manager) and reads what it prints: tables and the download progress.</summary>
internal static class Winget
{
    public const string Quiet = "--accept-source-agreements --disable-interactivity";

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (code, _) = await RunAsync("--version", null, CancellationToken.None);
            return code == 0;
        }
        catch { return false; }
    }

    /// <summary>Runs winget with the arguments. <paramref name="onLine"/> gets every piece of output (winget redraws its progress bar with carriage returns, so each redraw is one piece). Code -2 = cancelled.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string arguments, Action<string>? onLine, CancellationToken ct)
    {
        var start = new ProcessStartInfo("winget.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });

        var all = new StringBuilder();
        async Task Pump(StreamReader reader)
        {
            var line = new StringBuilder();
            var buffer = new char[512];
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = buffer[i];
                    lock (all) all.Append(c);
                    if (c == '\r' || c == '\n')
                    {
                        if (line.Length > 0) { onLine?.Invoke(line.ToString()); line.Clear(); }
                    }
                    else line.Append(c);
                }
            }
            if (line.Length > 0) onLine?.Invoke(line.ToString());
        }

        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync(CancellationToken.None));
        if (ct.IsCancellationRequested) return (-2, all.ToString());
        return (process.ExitCode, all.ToString());
    }

    // ───────── tables (search, list, upgrade) ─────────

    /// <summary>Reads the first table of winget's output: the header, a line of dashes, then one line per app.</summary>
    public static List<AppRow> ParseTable(string output)
    {
        var rows = new List<AppRow>();
        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var dash = Array.FindIndex(lines, l => l.Length >= 8 && l.All(c => c == '-'));
        if (dash < 1) return rows;

        var cols = Regex.Matches(lines[dash - 1], @"\S+").Select(m => (Name: m.Value, m.Index)).ToList();
        string Cell(string line, string name)
        {
            var i = cols.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "";
            var from = cols[i].Index;
            if (from >= line.Length) return "";
            var to = i + 1 < cols.Count ? Math.Min(cols[i + 1].Index, line.Length) : line.Length;
            return line[from..to].Trim();
        }

        for (var i = dash + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            var id = Cell(line, "Id");
            if (id.Length == 0 || id.Contains(' ')) continue;   // a note such as "3 upgrades available.", not an app
            rows.Add(new AppRow(Cell(line, "Name"), id, Cell(line, "Version"), Cell(line, "Available"), Cell(line, "Source")));
        }
        return rows;
    }

    // ───────── progress ─────────

    private static readonly Regex Sizes = new(@"(\d+(?:[.,]\d+)?)\s*([KMG]?i?B)\s*/\s*(\d+(?:[.,]\d+)?)\s*([KMG]?i?B)", RegexOptions.IgnoreCase);
    private static readonly Regex Percent = new(@"(?<!\d)(\d{1,3})\s*%");

    private static double Bytes(string number, string unit)
    {
        var value = double.Parse(number.Replace(',', '.'), CultureInfo.InvariantCulture);
        return char.ToUpperInvariant(unit[0]) switch { 'K' => value * 1024, 'M' => value * 1024 * 1024, 'G' => value * 1024 * 1024 * 1024, _ => value };
    }

    /// <summary>The download line "██████▒▒▒  12.3 MB / 45.0 MB" (or just a percent) as a fraction; null when the line is not a progress line.</summary>
    public static WingetProgress? ParseProgress(string line)
    {
        var sizes = Sizes.Match(line);
        if (sizes.Success)
        {
            var done = Bytes(sizes.Groups[1].Value, sizes.Groups[2].Value);
            var total = Bytes(sizes.Groups[3].Value, sizes.Groups[4].Value);
            if (total > 0)
                return new WingetProgress(Math.Clamp(done / total, 0, 1), $"{sizes.Groups[1].Value} {sizes.Groups[2].Value} of {sizes.Groups[3].Value} {sizes.Groups[4].Value}");
        }
        var bare = Regex.Match(line.Trim(), @"^(\d{1,3})\s*%$");   // a line that is only "37 %"
        if (bare.Success) return new WingetProgress(Math.Clamp(int.Parse(bare.Groups[1].Value) / 100.0, 0, 1), bare.Groups[1].Value + " %");
        if (line.Contains('█') || line.Contains('▒'))
        {
            var percent = Percent.Match(line);
            if (percent.Success) return new WingetProgress(Math.Clamp(int.Parse(percent.Groups[1].Value) / 100.0, 0, 1), percent.Groups[1].Value + " %");
        }
        return null;
    }

    /// <summary>True for the little spinner winget draws (- \ | /) and for empty lines.</summary>
    public static bool IsNoise(string line) => line.Trim().All(c => c is '-' or '\\' or '|' or '/');
}
