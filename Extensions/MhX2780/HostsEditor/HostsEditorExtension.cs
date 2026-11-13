using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HostsEditor;

public sealed class HostsEditorExtension : IAsdExtension
{
    public string Id => "MhX2780.HostsEditor";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "hosts", Title = "Hosts Editor", Glyph = "", Create = () => new HostsView(host.DataFolder) });
    }
}

/// <summary>
/// The hosts file as text, with a quick way to add a line. Reading needs no permission; saving copies the new text over the file with
/// administrator rights (Windows shows its own permission window), after a backup into the extension's folder.
/// </summary>
internal sealed class HostsView : StackPanel
{
    private static readonly string HostsPath = System.IO.Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    private readonly string _folder;
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false,
        FontFamily = new FontFamily("Consolas"), FontSize = 13, Height = 360,
    };
    private readonly TextBox _ip = new() { Header = "IP address", Text = "127.0.0.1", Width = 160 };
    private readonly TextBox _name = new() { Header = "Name (for example example.com)", Width = 300 };
    private readonly Button _add = new() { Content = "Add the line" };
    private readonly Button _block = new() { Content = "Block this name (0.0.0.0)" };
    private readonly Button _reload = new() { Content = "Read the file again" };
    private readonly Button _save = new() { Content = "Save", IsEnabled = false };
    private readonly ComboBox _backups = new() { Header = "Backups", MinWidth = 260 };
    private readonly Button _useBackup = new() { Content = "Load this backup" };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private bool _loading;

    public HostsView(string folder)
    {
        _folder = folder;
        Spacing = 12;
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);

        Children.Add(new TextBlock
        {
            Text = "Each line is an IP address and a name. A line that starts with # is a comment. Saving asks Windows for administrator permission.",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(_editor);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _save, _reload } });
        bottom.Children.Add(_ip);
        bottom.Children.Add(_name);
        var addButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Children = { _add, _block } };
        bottom.Children.Add(addButtons);
        Children.Add(bottom);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Children = { _backups, _useBackup } });
        Children.Add(_status);

        _editor.TextChanged += (_, _) => { if (!_loading) { _save.IsEnabled = true; _status.Text = "Not saved yet."; } };
        _reload.Click += (_, _) => Load();
        _save.Click += async (_, _) => await SaveAsync();
        _add.Click += (_, _) => AddLine(_ip.Text.Trim(), _name.Text.Trim());
        _block.Click += (_, _) => AddLine("0.0.0.0", _name.Text.Trim());
        _useBackup.Click += (_, _) =>
        {
            if (_backups.SelectedItem is not string file) return;
            try
            {
                SetText(System.IO.File.ReadAllText(System.IO.Path.Combine(_folder, file)));
                _save.IsEnabled = true;
                _status.Text = "The backup is loaded. Press Save to use it.";
            }
            catch (Exception ex) { _status.Text = "Could not read the backup: " + ex.Message; }
        };
        Loaded += (_, _) => { Load(); ListBackups(); };
    }

    private void SetText(string text)
    {
        _loading = true;
        _editor.Text = text.Replace("\r\n", "\n").Replace('\n', '\r');
        _loading = false;
    }

    private string EditorText()
    {
        var text = _editor.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        return text.EndsWith("\r\n") ? text : text + "\r\n";
    }

    private void Load()
    {
        try
        {
            SetText(System.IO.File.ReadAllText(HostsPath));
            _save.IsEnabled = false;
            _status.Text = "Read " + HostsPath;
        }
        catch (Exception ex) { _status.Text = "Could not read the hosts file: " + ex.Message; }
    }

    private void ListBackups()
    {
        _backups.Items.Clear();
        try
        {
            foreach (var file in System.IO.Directory.GetFiles(_folder, "hosts-backup-*.txt").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                _backups.Items.Add(System.IO.Path.GetFileName(file));
            if (_backups.Items.Count > 0) _backups.SelectedIndex = 0;
        }
        catch { }
    }

    private void AddLine(string ip, string name)
    {
        if (!IPAddress.TryParse(ip, out _)) { _status.Text = "The IP address is not valid."; return; }
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]+$")) { _status.Text = "The name can only contain letters, digits, dots, dashes and underscores."; return; }
        var text = _editor.Text;
        if (text.Length > 0 && !text.EndsWith('\r') && !text.EndsWith('\n')) text += "\r";
        _editor.Text = text + ip + "\t" + name + "\r";
        _status.Text = "Line added. Press Save to apply it.";
    }

    private async Task SaveAsync()
    {
        var text = EditorText();
        _save.IsEnabled = false;
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (System.IO.File.Exists(HostsPath)) System.IO.File.Copy(HostsPath, System.IO.Path.Combine(_folder, "hosts-backup-" + stamp + ".txt"), true);
            var temp = System.IO.Path.Combine(_folder, "hosts.new");
            System.IO.File.WriteAllText(temp, text, new UTF8Encoding(false));

            _status.Text = "Waiting for Windows to ask for permission...";
            var info = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "/c copy /Y \"" + temp + "\" \"" + HostsPath + "\" & ipconfig /flushdns",
            };
            using var process = Process.Start(info);
            if (process != null) await process.WaitForExitAsync();

            var now = System.IO.File.ReadAllText(HostsPath);
            if (now.Replace("\r\n", "\n").TrimEnd() == text.Replace("\r\n", "\n").TrimEnd()) _status.Text = "Saved, and the DNS cache was cleared. A backup of the old file is in the list below.";
            else { _status.Text = "The file was not changed."; _save.IsEnabled = true; }
            ListBackups();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _status.Text = "Cancelled: Windows needs administrator permission to change the hosts file.";
            _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save: " + ex.Message;
            _save.IsEnabled = true;
        }
    }
}
