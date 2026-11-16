using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace AsdAi;

/// <param name="Style">How the service is spoken to: "anthropic", "gemini" or "openai" (the common chat-completions format).</param>
internal sealed record AiProvider(string Id, string Name, string Style, string Url, string DefaultModel, string Note);

/// <summary>One AI the person added: which provider, which model, and the role it plays in a discussion.</summary>
internal sealed class AiModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "claude";
    public string Model { get; set; } = "";
    public string Account { get; set; } = "";   // Cloudflare Account ID
    public string Url { get; set; } = "";       // "Another provider": its address
    public string Style { get; set; } = "openai";
    public string Role { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

internal sealed class AiHttpException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The AI models of an extension (models.json), their keys (one encrypted file per provider, readable only by this Windows user)
/// and the chat call. A key is sent only to the address of its own provider.
/// </summary>
internal static class AiModels
{
    public static readonly AiProvider[] Providers =
    {
        new("claude", "Claude (Anthropic)", "anthropic", "https://api.anthropic.com", "claude-sonnet-5-5", "Get a key at console.anthropic.com."),
        new("gemini", "Gemini (Google)", "gemini", "https://generativelanguage.googleapis.com", "gemini-2.5-flash", "Get a key at aistudio.google.com."),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com", "deepseek-chat", "Get a key at platform.deepseek.com."),
        new("hcnsec", "api.hcnsec.cn", "openai", "https://api.hcnsec.cn/v1", "auto", "A free OpenAI-compatible gateway (GLM, DeepSeek, Qwen, Kimi...). 'auto' lets the service pick the model."),
        new("cloudflare", "Cloudflare Workers AI", "openai", "https://api.cloudflare.com/client/v4/accounts/ACCOUNT_ID/ai/v1", "@cf/meta/llama-3.3-70b-instruct-fp8-fast", "Needs your Account ID and an API token with the 'Workers AI' permission."),
        new("custom", "Another provider...", "openai", "", "", "Enter the address of any service, how it is spoken to, and a model name."),
    };

    private static readonly Dictionary<string, string[]> Known = new()
    {
        ["claude"] = new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1" },
        ["gemini"] = new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite" },
        ["deepseek"] = new[] { "deepseek-chat", "deepseek-reasoner" },
        ["cloudflare"] = new[] { "@cf/meta/llama-3.3-70b-instruct-fp8-fast", "@cf/openai/gpt-oss-120b", "@cf/openai/gpt-oss-20b", "@cf/qwen/qwen3.8-27b", "@cf/deepseek-ai/deepseek-v4-flash-0731", "@cf/moonshotai/kimi-k2.7-code" },
        ["hcnsec"] = new[] { "auto", "DeepSeek-V4-Flash", "DeepSeek-V4-Pro", "glm-5.3", "glm-5.3-flash", "longcat-2.5", "MiMo-V2.6-Flash", "MiniMax-M3.1-Flash", "Qwen3.8-27B", "step-5-preview" },
    };

    public static string[] KnownModels(string providerId) => Known.TryGetValue(providerId, out var m) ? m : Array.Empty<string>();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    // ───────── the list of models ─────────

    private static string ListFile(string folder) => Path.Combine(folder, "models.json");

    public static List<AiModel> Load(string folder)
    {
        try
        {
            if (File.Exists(ListFile(folder))) return JsonSerializer.Deserialize<List<AiModel>>(File.ReadAllText(ListFile(folder))) ?? new();
        }
        catch { /* start with an empty list */ }
        return new();
    }

    public static void Save(string folder, List<AiModel> models)
    {
        try { File.WriteAllText(ListFile(folder), JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public static AiProvider Resolve(AiModel m)
    {
        var p = Providers.FirstOrDefault(x => x.Id == m.Provider) ?? Providers[0];
        if (p.Id == "cloudflare")
        {
            var account = m.Account.Trim();
            return p with { Url = account.Length == 0 ? "" : p.Url.Replace("ACCOUNT_ID", Uri.EscapeDataString(account)) };
        }
        if (p.Id == "custom") return p with { Style = m.Style, Url = m.Url.Trim() };
        return p;
    }

    public static string ModelName(AiModel m) => string.IsNullOrWhiteSpace(m.Model) ? Resolve(m).DefaultModel : m.Model.Trim();

    public static string Display(AiModel m) => m.Name.Trim().Length > 0 ? m.Name.Trim() : Resolve(m).Name.Replace("...", "") + " · " + ModelName(m);

    // ───────── keys ─────────

    /// <summary>Models of one provider share its key; "Another provider" has a key for each model.</summary>
    public static string KeyId(AiModel m) => m.Provider == "custom" ? "custom-" + m.Id : m.Provider;

    private static string KeyFile(string folder, string id) => Path.Combine(folder, "ai.key." + id);

    public static bool HasKey(string folder, AiModel m) => File.Exists(KeyFile(folder, KeyId(m)));

    public static void DeleteKey(string folder, string keyId)
    {
        try { File.Delete(KeyFile(folder, keyId)); } catch { }
    }

    public static async Task SaveKeyAsync(string folder, string keyId, string key)
    {
        var protectedKey = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.ConvertStringToBinary(key, BinaryStringEncoding.Utf8));
        CryptographicBuffer.CopyToByteArray(protectedKey, out var bytes);
        File.WriteAllBytes(KeyFile(folder, keyId), bytes);
    }

    public static async Task<string?> LoadKeyAsync(string folder, AiModel m)
    {
        try
        {
            var file = KeyFile(folder, KeyId(m));
            if (!File.Exists(file)) return null;
            var buffer = CryptographicBuffer.CreateFromByteArray(File.ReadAllBytes(file));
            var clear = await new DataProtectionProvider().UnprotectAsync(buffer);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clear);
        }
        catch { return null; }
    }

    // ───────── the call ─────────

    private static string UrlFor(AiProvider provider, string model)
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

    /// <summary>Asks one model. A busy service (429, 5xx) is tried again twice.</summary>
    public static async Task<string> AskAsync(AiModel m, string key, string system, string user, int maxTokens, CancellationToken ct)
    {
        var provider = Resolve(m);
        var model = ModelName(m);
        if (provider.Url.Length == 0) throw new InvalidOperationException("Enter the provider's address (for Cloudflare: your Account ID) in 'AI models'.");
        if (model.Length == 0) throw new InvalidOperationException("Enter a model name in 'AI models'.");
        for (var attempt = 0; ; attempt++)
        {
            try { return await AskOnceAsync(provider, model, key, system, user, maxTokens, ct); }
            catch (AiHttpException e) when (attempt < 2 && (e.Status == 429 || e.Status >= 500))
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), ct);
            }
        }
    }

    private static async Task<string> AskOnceAsync(AiProvider provider, string model, string key, string system, string user, int maxTokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UrlFor(provider, model));
        string json;
        switch (provider.Style)
        {
            case "anthropic":
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                var body = new Dictionary<string, object> { ["model"] = model, ["max_tokens"] = maxTokens, ["messages"] = new[] { new { role = "user", content = user } } };
                if (system.Length > 0) body["system"] = system;
                json = JsonSerializer.Serialize(body);
                break;
            }
            case "gemini":
            {
                request.Headers.Add("x-goog-api-key", key);
                var body = new Dictionary<string, object>
                {
                    ["contents"] = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                    ["generationConfig"] = new { maxOutputTokens = maxTokens },
                };
                if (system.Length > 0) body["systemInstruction"] = new { parts = new[] { new { text = system } } };
                json = JsonSerializer.Serialize(body);
                break;
            }
            default:
            {
                request.Headers.Add("Authorization", "Bearer " + key);
                var messages = new List<object>();
                if (system.Length > 0) messages.Add(new { role = "system", content = system });
                messages.Add(new { role = "user", content = user });
                json = JsonSerializer.Serialize(new { model, messages });
                break;
            }
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
                if (error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var mm)) message = mm.GetString() ?? message;
            }
            catch { /* not JSON: show it as it is */ }
            throw new AiHttpException((int)response.StatusCode, $"{provider.Name} answered {(int)response.StatusCode}: {message}");
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
            var text = Regex.Replace(result.ToString(), @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();   // some models print their thinking
            if (text.Length == 0) throw new InvalidOperationException("empty answer");
            return text;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            var shown = answer.Length > 300 ? answer[..300] : answer;
            throw new InvalidOperationException($"{provider.Name} answered, but not in the expected '{provider.Style}' format ({ex.Message}): {shown}");
        }
    }
}

/// <summary>Reads the answers of a model that was told to write "LABEL: text" sections.</summary>
internal static class AiText
{
    /// <summary>The text of each label (a label starts a line; its text goes on until the next label). Bold marks are dropped.</summary>
    public static Dictionary<string, string> Sections(string text, params string[] labels)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buffer = new StringBuilder();
        void Flush()
        {
            if (current != null) result[current] = buffer.ToString().Replace("**", "").Trim();
            buffer.Clear();
        }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var probe = raw.TrimStart(' ', '\t', '*', '#');
            string? hit = null;
            var rest = "";
            foreach (var label in labels)
            {
                if (!probe.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var after = probe[label.Length..].TrimStart('*', ' ');
                if (after.StartsWith(':') || after.StartsWith('：')) { hit = label; rest = after[1..]; break; }
            }
            if (hit != null) { Flush(); current = hit; buffer.AppendLine(rest.TrimStart('*', ' ')); }
            else if (current != null) buffer.AppendLine(raw);
        }
        Flush();
        return result;
    }

    /// <summary>"NONE", "none.", "-", "n/a" or nothing at all.</summary>
    public static bool IsNone(string? s)
    {
        var t = (s ?? "").Trim().Trim('.', '-', '*', ' ', '—');
        return t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("none ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("لا ");
    }
}
