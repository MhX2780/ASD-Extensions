using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsdAi;

/// <summary>
/// One dialog to add, change and delete the AI models and their keys. Changes are saved at once (models.json and the encrypted keys);
/// the list that is passed in is changed in place, so the caller only has to refresh what it shows after the dialog closes.
/// </summary>
internal static class AiModelsDialog
{
    private static readonly string[] Roles =
    {
        "Balanced thinker",
        "Critic: looks for flaws and risks",
        "Optimist: sees the opportunities",
        "Pragmatist: what works in practice",
        "Skeptic: questions every assumption",
        "Domain expert",
    };

    public static async Task ShowAsync(XamlRoot root, string folder, List<AiModel> models, bool withRole)
    {
        AiModel? current = models.FirstOrDefault();
        var loading = false;

        var pick = new ComboBox { Header = "Model", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Name shown (optional)" };
        var provider = new ComboBox { Header = "Provider", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in AiModels.Providers) provider.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        var modelBox = new TextBox { Header = "Model name (type any, or pick one below)" };
        var suggest = new ComboBox { PlaceholderText = "Known models of this provider", HorizontalAlignment = HorizontalAlignment.Stretch };
        var account = new TextBox { Header = "Cloudflare Account ID" };
        var url = new TextBox { Header = "Address of the service", PlaceholderText = "https://example.com/v1" };
        var style = new ComboBox { Header = "How the service is spoken to", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "openai", "anthropic", "gemini" }) style.Items.Add(new ComboBoxItem { Content = s, Tag = s });
        var roleBox = new TextBox { Header = "Role in a discussion (optional)" };
        var roles = new ComboBox { PlaceholderText = "Pick a role", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var r in Roles) roles.Items.Add(r);
        var key = new PasswordBox { Header = "API key" };
        var enabled = new CheckBox { Content = "Use this model", IsChecked = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var save = new Button { Content = "Save this model" };
        var delete = new Button { Content = "Delete this model" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle) save.Style = accentStyle;

        string ProviderId() => (provider.SelectedItem as ComboBoxItem)?.Tag as string ?? "claude";

        void RefreshProviderParts()
        {
            var id = ProviderId();
            var info = AiModels.Providers.First(p => p.Id == id);
            note.Text = info.Note + " The key is stored encrypted, for this Windows user only, and is sent only to this provider.";
            account.Visibility = id == "cloudflare" ? Visibility.Visible : Visibility.Collapsed;
            url.Visibility = style.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
            suggest.Items.Clear();
            foreach (var m in AiModels.KnownModels(id)) suggest.Items.Add(m);
            suggest.Visibility = suggest.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshPick()
        {
            loading = true;
            pick.Items.Clear();
            foreach (var m in models) pick.Items.Add(new ComboBoxItem { Content = (m.Enabled ? "" : "(off) ") + AiModels.Display(m), Tag = m });
            pick.Items.Add(new ComboBoxItem { Content = "+ New model", Tag = null });
            pick.SelectedIndex = current == null ? pick.Items.Count - 1 : Math.Max(0, models.IndexOf(current));
            loading = false;
        }

        void LoadForm()
        {
            loading = true;
            var m = current;
            var id = m?.Provider ?? "claude";
            for (var i = 0; i < provider.Items.Count; i++)
                if (provider.Items[i] is ComboBoxItem { Tag: string t } && t == id) provider.SelectedIndex = i;
            name.Text = m?.Name ?? "";
            modelBox.Text = m?.Model ?? "";
            account.Text = m?.Account ?? "";
            url.Text = m?.Url ?? "";
            var styleId = m?.Style ?? "openai";
            for (var i = 0; i < style.Items.Count; i++)
                if (style.Items[i] is ComboBoxItem { Tag: string t } && t == styleId) style.SelectedIndex = i;
            roleBox.Text = m?.Role ?? "";
            enabled.IsChecked = m?.Enabled ?? true;
            key.Password = "";
            key.PlaceholderText = m != null && AiModels.HasKey(folder, m) ? "A key is saved. Leave empty to keep it." : "Paste the key here";
            delete.IsEnabled = m != null;
            status.Text = "";
            RefreshProviderParts();
            loading = false;
        }

        pick.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            current = (pick.SelectedItem as ComboBoxItem)?.Tag as AiModel;
            LoadForm();
        };
        provider.SelectionChanged += (_, _) => { if (!loading) RefreshProviderParts(); };
        suggest.SelectionChanged += (_, _) => { if (!loading && suggest.SelectedItem is string s) modelBox.Text = s; };
        roles.SelectionChanged += (_, _) => { if (!loading && roles.SelectedItem is string s) roleBox.Text = s; };

        save.Click += async (_, _) =>
        {
            try
            {
                var m = current ?? new AiModel();
                m.Provider = ProviderId();
                m.Name = name.Text.Trim();
                m.Model = modelBox.Text.Trim();
                m.Account = account.Text.Trim();
                m.Url = url.Text.Trim();
                m.Style = (style.SelectedItem as ComboBoxItem)?.Tag as string ?? "openai";
                m.Role = roleBox.Text.Trim();
                m.Enabled = enabled.IsChecked == true;
                if (m.Model.Length == 0) m.Model = AiModels.Resolve(m).DefaultModel;
                if (current == null) { models.Add(m); current = m; }
                if (key.Password.Trim().Length > 0) await AiModels.SaveKeyAsync(folder, AiModels.KeyId(m), key.Password.Trim());
                AiModels.Save(folder, models);
                RefreshPick();
                LoadForm();
                status.Text = "Saved.";
            }
            catch (Exception ex) { status.Text = "Could not save: " + ex.Message; }
        };

        delete.Click += (_, _) =>
        {
            if (current == null) return;
            var gone = current;
            models.Remove(gone);
            if (!models.Any(m => AiModels.KeyId(m) == AiModels.KeyId(gone))) AiModels.DeleteKey(folder, AiModels.KeyId(gone));
            AiModels.Save(folder, models);
            current = models.FirstOrDefault();
            RefreshPick();
            LoadForm();
            status.Text = "Deleted.";
        };

        var form = new StackPanel { Spacing = 10, MinWidth = 440 };
        foreach (var element in new UIElement[] { pick, name, provider, modelBox, suggest, account, url, style })
            form.Children.Add(element);
        if (withRole) { form.Children.Add(roleBox); form.Children.Add(roles); }
        form.Children.Add(key);
        form.Children.Add(enabled);
        form.Children.Add(note);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, delete } });
        form.Children.Add(status);

        RefreshPick();
        LoadForm();
        var dialog = new ContentDialog
        {
            Title = "AI models",
            Content = new ScrollViewer { Content = form, MaxHeight = 560, Padding = new Thickness(0, 0, 14, 0) },
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }
}
