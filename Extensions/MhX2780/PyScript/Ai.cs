using System.Text;
using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace PyScript;

/// <summary>The API key (encrypted for this Windows user, in the extension's data folder) and the call that analyses code with Claude.</summary>
internal static class Ai
{
    public const string DefaultModel = "claude-sonnet-5-5";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private static string KeyFile(string folder) => Path.Combine(folder, "ai.key");
    private static string ModelFile(string folder) => Path.Combine(folder, "ai.model");

    public static string LoadModel(string folder)
    {
        try { var m = File.ReadAllText(ModelFile(folder)).Trim(); return m.Length > 0 ? m : DefaultModel; }
        catch { return DefaultModel; }
    }

    public static void SaveModel(string folder, string model)
    {
        try { File.WriteAllText(ModelFile(folder), model.Trim()); } catch { }
    }

    /// <summary>An empty key removes the saved one.</summary>
    public static async Task SaveKeyAsync(string folder, string key)
    {
        if (key.Length == 0)
        {
            try { File.Delete(KeyFile(folder)); } catch { }
            return;
        }
        var protectedKey = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.ConvertStringToBinary(key, BinaryStringEncoding.Utf8));
        CryptographicBuffer.CopyToByteArray(protectedKey, out var bytes);
        File.WriteAllBytes(KeyFile(folder), bytes);
    }

    public static async Task<string?> LoadKeyAsync(string folder)
    {
        try
        {
            if (!File.Exists(KeyFile(folder))) return null;
            var buffer = CryptographicBuffer.CreateFromByteArray(File.ReadAllBytes(KeyFile(folder)));
            var clear = await new DataProtectionProvider().UnprotectAsync(buffer);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clear);
        }
        catch { return null; }
    }

    public static async Task<string> AnalyzeAsync(string key, string model, string code, string runOutput, string problems, CancellationToken ct)
    {
        if (code.Length > 60000) code = code[..60000];
        if (runOutput.Length > 8000) runOutput = runOutput[^8000..];

        var prompt = new StringBuilder();
        prompt.AppendLine("You are the code assistant inside a small Python IDE. Analyse the Python program below.");
        prompt.AppendLine("Answer with these short sections: 1) What it does. 2) Bugs and errors (give line numbers and the reason). 3) How to fix them (show the corrected code). 4) Improvements (style, speed, safety).");
        prompt.AppendLine("If the run output or the problem list shows an error, explain its cause first.");
        prompt.AppendLine().AppendLine("=== CODE ===").AppendLine(code);
        if (problems.Length > 0) prompt.AppendLine().AppendLine("=== PROBLEMS FOUND BY THE CHECKER ===").AppendLine(problems);
        if (runOutput.Length > 0) prompt.AppendLine().AppendLine("=== OUTPUT OF THE LAST RUN ===").AppendLine(runOutput);

        var body = JsonSerializer.Serialize(new { model, max_tokens = 3000, messages = new[] { new { role = "user", content = prompt.ToString() } } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = json;
            try
            {
                using var error = JsonDocument.Parse(json);
                if (error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) message = m.GetString() ?? json;
            }
            catch { /* not JSON: show it as it is */ }
            throw new InvalidOperationException($"The AI service answered {(int)response.StatusCode}: {message}");
        }

        using var doc = JsonDocument.Parse(json);
        var text = new StringBuilder();
        foreach (var part in doc.RootElement.GetProperty("content").EnumerateArray())
            if (part.TryGetProperty("type", out var type) && type.GetString() == "text") text.Append(part.GetProperty("text").GetString());
        return text.ToString();
    }
}
