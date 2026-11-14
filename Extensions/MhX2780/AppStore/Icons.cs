using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AppStore;

/// <summary>
/// The picture of an app, in this order: a picture saved before; the icon of the installed program (found through Windows' list of
/// installed programs); the favicon of the app's website (the site is read from "winget show"; the picture is saved in the extension's folder).
/// Call <see cref="GetAsync"/> on the UI thread. null = no picture, the page keeps its generic icon.
/// </summary>
internal static class Icons
{
    private sealed record Installed(string Name, string Icon, string Location);

    private static readonly Dictionary<string, Task<ImageSource?>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(2);   // at most 2 lookups on the internet at once
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly object Lock = new();
    private static List<Installed>? _installed;
    private static string _folder = "";

    // the featured apps: no need to ask winget for their site
    private static readonly Dictionary<string, string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualStudio.2022.Community"] = "visualstudio.microsoft.com",
        ["Microsoft.VisualStudioCode"] = "code.visualstudio.com",
        ["Git.Git"] = "git-scm.com",
        ["GitHub.cli"] = "cli.github.com",
        ["Python.Python.3.13"] = "python.org",
        ["OpenJS.NodeJS.LTS"] = "nodejs.org",
        ["Microsoft.DotNet.SDK.8"] = "dotnet.microsoft.com",
        ["Microsoft.WindowsTerminal"] = "microsoft.com",
        ["Microsoft.PowerToys"] = "microsoft.com",
        ["7zip.7zip"] = "7-zip.org",
        ["Notepad++.Notepad++"] = "notepad-plus-plus.org",
        ["VideoLAN.VLC"] = "videolan.org",
        ["Google.Chrome"] = "google.com",
        ["Mozilla.Firefox"] = "firefox.com",
        ["Tailscale.Tailscale"] = "tailscale.com",
        ["Docker.DockerDesktop"] = "docker.com",
        ["Wireshark.Wireshark"] = "wireshark.org",
        ["WinSCP.WinSCP"] = "winscp.net",
    };

    public static void Init(string dataFolder)
    {
        _folder = System.IO.Path.Combine(dataFolder, "icons");
        try { System.IO.Directory.CreateDirectory(_folder); } catch { }
    }

    public static Task<ImageSource?> GetAsync(string id, string name)
    {
        lock (Pending)
        {
            if (Pending.TryGetValue(id, out var task)) return task;
            return Pending[id] = LoadAsync(id, name);
        }
    }

    private static string CachePath(string id) => System.IO.Path.Combine(_folder, Regex.Replace(id, @"[^A-Za-z0-9._-]", "_") + ".png");

    private static ImageSource FromFile(string path) => new BitmapImage(new Uri(path)) { DecodePixelWidth = 64 };

    private static async Task<ImageSource?> LoadAsync(string id, string name)
    {
        try
        {
            var cached = CachePath(id);
            if (System.IO.File.Exists(cached)) return FromFile(cached);

            var program = await Task.Run(() => FindProgram(name));
            if (program != null)
            {
                var image = await FromProgramAsync(program);
                if (image != null) return image;
            }

            var bytes = await DownloadAsync(id);
            if (bytes != null)
            {
                System.IO.File.WriteAllBytes(cached, bytes);
                return FromFile(cached);
            }
        }
        catch { /* no picture: the generic icon stays */ }
        return null;
    }

    // ───────── the installed program ─────────

    private static List<Installed> ReadInstalled()
    {
        var list = new List<Installed>();
        var places = new (RegistryKey Root, string Path)[]
        {
            (Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (Registry.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
        };
        foreach (var (root, path) in places)
        {
            try
            {
                using var parent = root.OpenSubKey(path);
                if (parent == null) continue;
                foreach (var subName in parent.GetSubKeyNames())
                {
                    try
                    {
                        using var key = parent.OpenSubKey(subName);
                        var display = key?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        list.Add(new Installed(display.Trim(), key!.GetValue("DisplayIcon") as string ?? "", key.GetValue("InstallLocation") as string ?? ""));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>The file whose icon stands for the app (an .exe or .ico), or null.</summary>
    private static string? FindProgram(string name)
    {
        if (name.Length < 3) return null;
        List<Installed> list;
        lock (Lock) list = _installed ??= ReadInstalled();

        var match = list.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(i => name.Length >= 5 && (i.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || (i.Name.Length >= 5 && name.Contains(i.Name, StringComparison.OrdinalIgnoreCase))));
        if (match == null) return null;

        // DisplayIcon looks like  "C:\path\app.exe",0   or   C:\path\app.exe,0   or   C:\path\app.ico
        var icon = match.Icon.Trim();
        if (icon.StartsWith('"'))
        {
            var end = icon.IndexOf('"', 1);
            if (end > 0) icon = icon[1..end];
        }
        else
        {
            var comma = icon.LastIndexOf(',');
            if (comma > 0) icon = icon[..comma];
        }
        try
        {
            icon = Environment.ExpandEnvironmentVariables(icon);
            if (icon.Length > 0 && System.IO.File.Exists(icon) && !icon.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return icon;

            var location = Environment.ExpandEnvironmentVariables(match.Location.Trim().Trim('"'));
            if (location.Length > 0 && System.IO.Directory.Exists(location))
            {
                var exes = System.IO.Directory.EnumerateFiles(location, "*.exe", System.IO.SearchOption.TopDirectoryOnly)
                    .Where(f => !System.IO.Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase)
                        && !System.IO.Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return exes.FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Replace(" ", "").Contains(name.Replace(" ", "").Split('.')[0], StringComparison.OrdinalIgnoreCase)) ?? exes.FirstOrDefault();
            }
        }
        catch { }
        return null;
    }

    /// <summary>The shell's own icon of the file (what Explorer shows), as a picture.</summary>
    private static async Task<ImageSource?> FromProgramAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale);
            if (thumbnail == null || thumbnail.Size == 0) return null;
            var bitmap = new BitmapImage { DecodePixelWidth = 64 };
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch { return null; }
    }

    // ───────── the website ─────────

    private static async Task<string?> HomepageHostAsync(string id)
    {
        if (KnownHosts.TryGetValue(id, out var known)) return known;
        var (_, output) = await Winget.RunAsync("show --id " + id + " --exact " + Winget.Quiet, null, CancellationToken.None);
        string? best = null;
        var rank = 9;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(Homepage|Publisher Url|Package Url)\s*:\s*(https?://\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var label = m.Groups[1].Value;
            var r = label.StartsWith("Home", StringComparison.OrdinalIgnoreCase) ? 0 : label.StartsWith("Publisher", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            if (r < rank) { rank = r; best = m.Groups[2].Value; }
        }
        return best != null && Uri.TryCreate(best, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static async Task<byte[]?> DownloadAsync(string id)
    {
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9._+\-]+$")) return null;   // ids such as ARP\... cannot be asked from winget
        await Gate.WaitAsync();
        try
        {
            var host = await HomepageHostAsync(id);
            if (host == null) return null;
            var bytes = await Http.GetByteArrayAsync("https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(host) + "&sz=64");
            return bytes.Length > 100 ? bytes : null;
        }
        catch { return null; }
        finally { Gate.Release(); }
    }
}
