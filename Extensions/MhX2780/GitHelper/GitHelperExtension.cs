using System.Diagnostics;
using System.Text;
using ASD.Extensibility;
using AsdShared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GitHelper;

public sealed class GitHelperExtension : IAsdExtension
{
    public string Id => "MhX2780.GitHelper";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "git", Title = "Git Helper", Glyph = "", Create = () => new GitView(host.DataFolder) });
    }
}

/// <summary>Runs git.exe (never asks for a password in a console: a saved login from Git Credential Manager still works).</summary>
internal static class Git
{
    public static Task<(int Code, string Text)> RunAsync(string folder, params string[] args) => Task.Run(() =>
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.quotepath=false");
            foreach (var a in args) info.ArgumentList.Add(a);
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(true); } catch { }
                return (-1, "git took more than 2 minutes and was stopped.");
            }
            Task.WaitAll(output, error);
            var text = output.Result;
            if (error.Result.Length > 0) text += (text.Length > 0 ? "\n" : "") + error.Result;
            return (p.ExitCode, text);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-127, "Git was not found. Install Git for Windows (the Apps Store extension has it), then open this page again.");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    });
}

/// <summary>Pick a repository folder; see what changed, stage files, commit, push, pull, and read the history.</summary>
internal sealed class GitView : Grid
{
    private sealed record Change(char X, char Y, string Path);

    private readonly string _dataFolder;
    private string? _repo;
    private string _branchName = "";
    private bool _busy;

    private readonly TextBox _folderBox = new() { IsReadOnly = true, PlaceholderText = "Choose the folder of a Git repository", MinWidth = 420 };
    private readonly Button _choose = new() { Content = "Choose folder" };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _fetch = new() { Content = "Fetch" };
    private readonly Button _pull = new() { Content = "Pull" };
    private readonly Button _push = new() { Content = "Push" };
    private readonly Button _init = new() { Content = "Create a repository here", Visibility = Visibility.Collapsed };
    private readonly TextBlock _branch = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Button _stageAll = new() { Content = "Stage all" };
    private readonly Button _unstageAll = new() { Content = "Unstage all" };
    private readonly ListView _changes = new() { SelectionMode = ListViewSelectionMode.None, Height = 240 };
    private readonly TextBox _message = new() { PlaceholderText = "Commit message", MinWidth = 420 };
    private readonly Button _commit = new() { Content = "Commit" };
    private readonly Button _commitPush = new() { Content = "Commit and push" };
    private readonly ListView _log = new() { SelectionMode = ListViewSelectionMode.None, Height = 190 };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, Height = 110 };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };

    public GitView(string dataFolder)
    {
        _dataFolder = dataFolder;
        RowSpacing = 10;
        for (var i = 0; i < 8; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _folderBox, _choose, _refresh } }, 0);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _fetch, _pull, _push, _init } }, 1);
        Put(_branch, 2);
        var changesHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Changes (tick a file to stage it)", VerticalAlignment = VerticalAlignment.Center }, _stageAll, _unstageAll } };
        var changes = new StackPanel { Spacing = 6, Children = { changesHead, _changes } };
        Put(changes, 3);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _message, _commit, _commitPush } }, 4);
        Put(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "History", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _log } }, 5);
        Put(_status, 6);
        Put(_output, 7);
        EnableRepoButtons(false);

        _choose.Click += async (_, _) =>
        {
            var path = await Pick.FolderAsync();
            if (path != null) await OpenRepoAsync(path);
        };
        _refresh.Click += async (_, _) => await RefreshAsync();
        _fetch.Click += async (_, _) => await DoAsync("Fetching...", "fetch", "--prune");
        _pull.Click += async (_, _) => await DoAsync("Pulling...", "pull");
        _push.Click += async (_, _) => await PushAsync();
        _init.Click += async (_, _) => await DoAsync("Creating the repository...", "init");
        _stageAll.Click += async (_, _) => await QuietAsync("add", "-A");
        _unstageAll.Click += async (_, _) => await QuietAsync("reset", "-q");
        _commit.Click += async (_, _) => await CommitAsync(false);
        _commitPush.Click += async (_, _) => await CommitAsync(true);

        Loaded += async (_, _) =>
        {
            if (_repo != null) return;
            try
            {
                var last = System.IO.Path.Combine(_dataFolder, "last.txt");
                if (System.IO.File.Exists(last))
                {
                    var path = System.IO.File.ReadAllText(last).Trim();
                    if (System.IO.Directory.Exists(path)) await OpenRepoAsync(path);
                }
            }
            catch { }
        };
    }

    private void Put(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        Children.Add(element);
    }

    private void EnableRepoButtons(bool on)
    {
        _fetch.IsEnabled = _pull.IsEnabled = _push.IsEnabled = _stageAll.IsEnabled = _unstageAll.IsEnabled = on;
        _commit.IsEnabled = _commitPush.IsEnabled = _message.IsEnabled = on;
    }

    private void Show(string text) => _output.Text = text.Replace("\r\n", "\n").Replace('\n', '\r');

    private async Task OpenRepoAsync(string path)
    {
        _repo = path;
        _folderBox.Text = path;
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(_dataFolder, "last.txt"), path); } catch { }
        await RefreshAsync();
    }

    /// <summary>Runs git, shows what it printed, and reads the repository again.</summary>
    private async Task DoAsync(string title, params string[] args)
    {
        if (_repo == null || _busy) return;
        _busy = true;
        _status.Text = title;
        var (code, text) = await Git.RunAsync(_repo, args);
        Show("> git " + string.Join(' ', args) + "\n" + text.Trim());
        _busy = false;
        await RefreshAsync();
        _status.Text = code == 0 ? "Done." : "git stopped with code " + code + ". See the output.";
    }

    /// <summary>Like DoAsync, but only shows the output when something went wrong (staging a file is not worth a message).</summary>
    private async Task QuietAsync(params string[] args)
    {
        if (_repo == null || _busy) return;
        var (code, text) = await Git.RunAsync(_repo, args);
        if (code != 0) Show("> git " + string.Join(' ', args) + "\n" + text.Trim());
        await RefreshAsync();
    }

    private async Task PushAsync()
    {
        if (_repo == null || _busy) return;
        _busy = true;
        _status.Text = "Pushing...";
        var (code, text) = await Git.RunAsync(_repo, "push");
        if (code != 0 && _branchName.Length > 0 && text.Contains("no upstream branch", StringComparison.OrdinalIgnoreCase))
            (code, text) = await Git.RunAsync(_repo, "push", "-u", "origin", _branchName);
        Show("> git push\n" + text.Trim());
        _busy = false;
        await RefreshAsync();
        _status.Text = code == 0 ? "Pushed." : "The push failed. See the output.";
    }

    private async Task CommitAsync(bool push)
    {
        if (_repo == null || _busy) return;
        var message = _message.Text.Trim();
        if (message.Length == 0) { _status.Text = "Write a commit message first."; return; }
        _busy = true;
        _status.Text = "Committing...";
        var (code, text) = await Git.RunAsync(_repo, "commit", "-m", message);
        Show("> git commit\n" + text.Trim());
        _busy = false;
        if (code != 0)
        {
            await RefreshAsync();
            _status.Text = "Nothing was committed. Tick the files to include first, or see the output.";
            return;
        }
        _message.Text = "";
        if (push) await PushAsync();
        else
        {
            await RefreshAsync();
            _status.Text = "Committed.";
        }
    }

    private static string Word(char c) => c switch
    {
        'M' => "modified",
        'A' => "new file",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'U' => "conflict",
        'T' => "type changed",
        '?' => "untracked",
        _ => "",
    };

    private async Task RefreshAsync()
    {
        if (_repo == null) return;
        var (code, top) = await Git.RunAsync(_repo, "rev-parse", "--show-toplevel");
        _changes.Items.Clear();
        _log.Items.Clear();
        if (code == -127)
        {
            _init.Visibility = Visibility.Collapsed;
            _branch.Text = "";
            EnableRepoButtons(false);
            Show(top);
            return;
        }
        if (code != 0)
        {
            _init.Visibility = Visibility.Visible;
            _branch.Text = "This folder is not a Git repository.";
            EnableRepoButtons(false);
            Show(top.Trim());
            return;
        }
        _init.Visibility = Visibility.Collapsed;
        EnableRepoButtons(true);
        var root = top.Trim().Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (System.IO.Directory.Exists(root)) { _repo = root; _folderBox.Text = root; }   // paths of git are relative to the top of the repository

        var (_, status) = await Git.RunAsync(_repo, "status", "--porcelain=v1", "-b", "-z");
        var changes = new List<Change>();
        var branchLine = "";
        var parts = status.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length < 3) continue;
            if (part.StartsWith("## ")) { branchLine = part[3..]; continue; }
            var x = part[0];
            var y = part[1];
            changes.Add(new Change(x, y, part[3..]));
            if (x is 'R' or 'C' || y is 'R' or 'C') i++;   // the next entry is the old name
        }

        if (branchLine.StartsWith("No commits yet on ")) _branchName = branchLine["No commits yet on ".Length..];
        else if (branchLine.StartsWith("HEAD")) _branchName = "";
        else
        {
            var cut = branchLine.IndexOf("...", StringComparison.Ordinal);
            if (cut < 0) cut = branchLine.IndexOf(' ');
            _branchName = cut < 0 ? branchLine : branchLine[..cut];
        }
        _branch.Text = branchLine.Length == 0 ? "" : "Branch: " + branchLine.Replace("...", "  ->  ") + (changes.Count == 0 ? "   -   nothing to commit" : "");

        foreach (var c in changes) _changes.Items.Add(MakeRow(c));

        var (logCode, log) = await Git.RunAsync(_repo, "log", "-40", "--pretty=format:%h%x09%an%x09%ar%x09%s");
        if (logCode != 0) _log.Items.Add(new TextBlock { Text = "No commits yet.", Opacity = 0.7 });
        else
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t');
                if (f.Length < 4) continue;
                var row = new StackPanel { Spacing = 1, Padding = new Thickness(2, 3, 2, 3) };
                row.Children.Add(new TextBlock { Text = f[3], TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = f[0] + "  -  " + f[1] + "  -  " + f[2], Opacity = 0.6, FontSize = 12 });
                _log.Items.Add(row);
            }
    }

    private FrameworkElement MakeRow(Change c)
    {
        var staged = c.X != ' ' && c.X != '?';
        var box = new CheckBox { IsChecked = staged, MinWidth = 32 };
        box.Click += async (_, _) =>
        {
            if (box.IsChecked == true) await QuietAsync("add", "--", c.Path);
            else await QuietAsync("reset", "-q", "--", c.Path);
        };

        string what;
        if (c.X == '?') what = "untracked";
        else
        {
            var a = Word(c.X);
            var b = Word(c.Y);
            what = a.Length > 0 && b.Length > 0 ? a + ", then " + b : a + b;
        }

        var path = new TextBlock { Text = c.Path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var kind = new TextBlock { Text = what, Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(path, 1);
        Grid.SetColumn(kind, 2);
        row.Children.Add(box);
        row.Children.Add(path);
        row.Children.Add(kind);
        return row;
    }
}
