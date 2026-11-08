using System.Text;
using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace PyScript;

/// <param name="Style">How the service is spoken to: "anthropic", "gemini" or "openai" (the common chat-completions format).</param>
internal sealed record ProviderInfo(string Id, string Name, string Style, string Url, string DefaultModel, string Note);

internal sealed class AiSettings
{
    public string Provider { get; set; } = "claude";
    public Dictionary<string, string> Models { get; set; } = new();
    public string CustomName { get; set; } = "";
    public string CustomUrl { get; set; } = "";
    public string CustomStyle { get; set; } = "openai";
}

/// <summary>
/// The AI providers, their settings (ai.json) and keys (one encrypted file per provider, readable only by this Windows user),
/// and the call that analyses code. Every key is sent only to the address of its own provider.
/// </summary>
internal static class Ai
{
    public static readonly ProviderInfo[] Providers =
    {
        new("claude", "Claude (Anthropic)", "anthropic", "https://api.anthropic.com", "claude-sonnet-5-5", "Get a key at console.anthropic.com."),
        new("gemini", "Gemini (Google)", "gemini", "https://generativelanguage.googleapis.com", "gemini-2.5-flash", "Get a key at aistudio.google.com."),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com", "deepseek-chat", "Get a key at platform.deepseek.com."),
        new("hcnsec", "api.hcnsec.cn", "openai", "https://api.hcnsec.cn/v1", "auto", "A free OpenAI-compatible gateway (GLM, DeepSeek, Qwen, Kimi, Step, MiniMax...). 'auto' lets the service pick the model. Register on api.hcnsec.cn to get a key."),
        new("custom", "Another provider...", "openai", "", "", "Enter the address of any service, how it is spoken to, and a model name."),
    };

    // model names offered in the box (the person can type any other). The api.hcnsec.cn list was read from its public model list in Oct-Nov 2026.
    private static readonly Dictionary<string, string[]> Known = new()
    {
        ["claude"] = new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1" },
        ["gemini"] = new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite" },
        ["deepseek"] = new[] { "deepseek-chat", "deepseek-reasoner" },
        ["hcnsec"] = new[]
        {
            "auto", "DeepSeek-V4-Flash", "DeepSeek-V4-Pro", "DeepSeek-V4.1-Flash", "glm-5.3", "glm-5.3-flash", "longcat-2.5",
            "MiMo-V2.6-Flash", "MiniMax-M3.1-Flash", "Qwen3.8-27B", "Qwen3.8-Flash-Next", "sensenova-6.8-flash-lite", "step-5-preview",
        },
    };

    public static string[] KnownModels(string id) => Known.TryGetValue(id, out var models) ? models : Array.Empty<string>();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private static string SettingsFile(string folder) => Path.Combine(folder, "ai.json");
    private static string KeyFile(string folder, string id) => Path.Combine(folder, "ai.key." + id);
    private static string OldKeyFile(string folder) => Path.Combine(folder, "ai.key");   // version 1.0 kept only the Claude key

    public static AiSettings Load(string folder)
    {
        try
        {
            if (File.Exists(SettingsFile(folder))) return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(SettingsFile(folder))) ?? new();
        }
        catch { /* start again with the defaults */ }
        var settings = new AiSettings();
        try
        {
            var old = Path.Combine(folder, "ai.model");
            if (File.Exists(old)) settings.Models["claude"] = File.ReadAllText(old).Trim();
        }
        catch { }
        return settings;
    }

    public static void Save(string folder, AiSettings settings)
    {
        try { File.WriteAllText(SettingsFile(folder), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public static ProviderInfo Find(string id) => Providers.FirstOrDefault(p => p.Id == id) ?? Providers[0];

    /// <summary>The chosen provider; for "Another provider" with the person's own name, address and style.</summary>
    public static ProviderInfo Resolve(AiSettings settings)
    {
        var provider = Find(settings.Provider);
        if (provider.Id != "custom") return provider;
        var name = string.IsNullOrWhiteSpace(settings.CustomName) ? "Another provider" : settings.CustomName.Trim();
        return provider with { Name = name, Style = settings.CustomStyle, Url = settings.CustomUrl.Trim() };
    }

    public static string ModelFor(AiSettings settings, ProviderInfo provider)
        => settings.Models.TryGetValue(provider.Id, out var model) && !string.IsNullOrWhiteSpace(model) ? model.Trim() : provider.DefaultModel;

    public static bool HasKey(string folder, string id) => File.Exists(KeyFile(folder, id)) || (id == "claude" && File.Exists(OldKeyFile(folder)));

    /// <summary>An empty key removes the saved one.</summary>
    public static async Task SaveKeyAsync(string folder, string id, string key)
    {
        if (key.Length == 0)
        {
            try { File.Delete(KeyFile(folder, id)); if (id == "claude") File.Delete(OldKeyFile(folder)); } catch { }
            return;
        }
        var protectedKey = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.ConvertStringToBinary(key, BinaryStringEncoding.Utf8));
        CryptographicBuffer.CopyToByteArray(protectedKey, out var bytes);
        File.WriteAllBytes(KeyFile(folder, id), bytes);
    }

    public static async Task<string?> LoadKeyAsync(string folder, string id)
    {
        try
        {
            var file = KeyFile(folder, id);
            if (!File.Exists(file) && id == "claude") file = OldKeyFile(folder);
            if (!File.Exists(file)) return null;
            var buffer = CryptographicBuffer.CreateFromByteArray(File.ReadAllBytes(file));
            var clear = await new DataProtectionProvider().UnprotectAsync(buffer);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clear);
        }
        catch { return null; }
    }

    private static string UrlFor(ProviderInfo provider, string model)
    {
        var address = provider.Url.Trim().TrimEnd('/');
        if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        switch (provider.Style)
        {
            case "anthropic":
                return address.EndsWith("/messages") ? address : address.EndsWith("/v1") ? address + "/messages" : address + "/v1/messages";
            case "gemini":
                return address.Contains(":generateContent") ? address : address + "/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent";
            default:
                return address.EndsWith("/chat/completions") ? address : address.EndsWith("/v1") ? address + "/chat/completions" : address + "/v1/chat/completions";
        }
    }

    public static async Task<string> AnalyzeAsync(ProviderInfo provider, string model, string key, string code, string runOutput, string problems, CancellationToken ct)
    {
        if (provider.Url.Length == 0) throw new InvalidOperationException("Enter the address of the provider in 'AI settings'.");
        if (model.Length == 0) throw new InvalidOperationException("Enter a model name in 'AI settings'.");
        if (code.Length > 60000) code = code[..60000];
        if (runOutput.Length > 8000) runOutput = runOutput[^8000..];

        var prompt = new StringBuilder();
        prompt.AppendLine("You are the code assistant inside a small Python IDE. Analyse the Python program below.");
        prompt.AppendLine("Answer with these short sections: 1) What it does. 2) Bugs and errors (give line numbers and the reason). 3) How to fix them (show the corrected code). 4) Improvements (style, speed, safety).");
        prompt.AppendLine("If the run output or the problem list shows an error, explain its cause first.");
        prompt.AppendLine().AppendLine("=== CODE ===").AppendLine(code);
        if (problems.Length > 0) prompt.AppendLine().AppendLine("=== PROBLEMS FOUND BY THE CHECKER ===").AppendLine(problems);
        if (runOutput.Length > 0) prompt.AppendLine().AppendLine("=== OUTPUT OF THE LAST RUN ===").AppendLine(runOutput);
        var text = prompt.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, UrlFor(provider, model));
        string json;
        switch (provider.Style)
        {
            case "anthropic":
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                json = JsonSerializer.Serialize(new { model, max_tokens = 3000, messages = new[] { new { role = "user", content = text } } });
                break;
            case "gemini":
                request.Headers.Add("x-goog-api-key", key);
                json = JsonSerializer.Serialize(new { contents = new[] { new { role = "user", parts = new[] { new { text } } } } });
                break;
            default:
                request.Headers.Add("Authorization", "Bearer " + key);
                json = JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = text } } });
                break;
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var answer = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = answer.Length > 600 ? answer[..600] : answer;
            try
            {
                using var error = JsonDocument.Parse(answer);
                if (error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
            }
            catch { /* not JSON: show it as it is */ }
            throw new InvalidOperationException($"{provider.Name} answered {(int)response.StatusCode}: {message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            var result = new StringBuilder();
            switch (provider.Style)
            {
                case "anthropic":
                    foreach (var part in root.GetProperty("content").EnumerateArray())
                        if (part.TryGetProperty("type", out var type) && type.GetString() == "text") result.Append(part.GetProperty("text").GetString());
                    break;
                case "gemini":
                    foreach (var part in root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray())
                        if (part.TryGetProperty("text", out var t)) result.Append(t.GetString());
                    break;
                default:
                    result.Append(root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
                    break;
            }
            if (result.Length == 0) throw new InvalidOperationException("empty answer");
            return result.ToString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            var shown = answer.Length > 300 ? answer[..300] : answer;
            throw new InvalidOperationException($"{provider.Name} answered, but not in the expected '{provider.Style}' format ({ex.Message}): {shown}");
        }
    }
}
