using System.Text;
using AsdAi;

namespace AiPlanning;

/// <param name="Tone">info, agree, object, draft, flaws, question, final, warn or error: the UI colors a cell by it</param>
internal sealed record Cell(string Kind, string Title, string Subtitle, string Tone, List<(string Label, string Text)> Parts);

internal sealed record Turn(AiModel Model, string Raw, Dictionary<string, string> Parts, string? Error);

/// <summary>
/// Several AI models discuss one proposal until they agree on ONE decision.
/// Round 1: each gives its own view. The moderator merges the views into a draft. Next rounds: each model judges the draft (AGREE or OBJECT with the change it wants);
/// the moderator improves the draft. When every model agrees the decision is final. The models can list flaws, and ask the user questions through the moderator.
/// The time is limited (rounds and minutes) and can be extended: <see cref="RunAsync"/> continues from where the discussion stopped.
/// </summary>
internal sealed class Debate
{
    public string Folder = "";
    public string Proposal = "";
    public List<AiModel> Participants = new();
    public AiModel Moderator = null!;
    public bool FlawCheck = true;
    public int Round;
    public int MaxRounds = 5;
    public DateTime Deadline = DateTime.UtcNow.AddMinutes(10);
    public string Draft = "";
    public int DraftVersion;
    public bool Agreed;
    public string Final = "";
    public readonly List<string> Answers = new();
    public readonly StringBuilder Log = new();

    public Action<Cell> OnCell = _ => { };
    public Action<string> OnStatus = _ => { };
    /// <summary>Shows the questions to the user. Returns one answer per question, or null when the user lets the AIs decide.</summary>
    public Func<List<string>, CancellationToken, Task<List<string>?>> AskUser = (_, _) => Task.FromResult<List<string>?>(null);

    private List<Turn> _last = new();
    private bool _lastOpening;
    private int _asked;

    private static string Name(AiModel m) => AiModels.Display(m);

    private void Emit(string kind, string title, string sub, string tone, params (string, string)[] parts)
    {
        var list = parts.Where(p => p.Item2.Trim().Length > 0).Select(p => (p.Item1, p.Item2.Trim())).ToList();
        Log.AppendLine($"## {kind}: {title}{(sub.Length > 0 ? " (" + sub + ")" : "")}");
        foreach (var (label, text) in list) Log.AppendLine($"**{label}:** {text}\n");
        OnCell(new Cell(kind, title, sub, tone, list));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string P(Turn t, string label) => t.Parts.TryGetValue(label, out var v) ? v : "";

    private string Context()
    {
        var sb = new StringBuilder("=== PROPOSAL FROM THE USER ===\n").Append(Proposal);
        if (Answers.Count > 0) sb.Append("\n\n=== THE USER'S ANSWERS TO THE QUESTIONS ===\n").Append(string.Join("\n", Answers));
        return sb.ToString();
    }

    private static string SystemFor(AiModel m) =>
        $"You are \"{Name(m)}\", one of several AI advisors who must reach ONE shared decision together. " +
        (m.Role.Trim().Length > 0 ? $"Your role: {m.Role.Trim()}. " : "") +
        "Be concrete and brief (under 200 words). Do not agree just to be polite: object when you see a real problem, and say clearly what would fix it. " +
        "Reply in the language of the user's proposal, but keep the section labels exactly as asked, in English. No markdown tables.";

    private string Summary(Turn t)
    {
        var text = _lastOpening
            ? $"POSITION: {P(t, "POSITION")} | REASONS: {P(t, "REASONS")} | FLAWS: {P(t, "FLAWS")}"
            : $"VERDICT: {P(t, "VERDICT")} | CHANGES: {P(t, "CHANGES")} | FLAWS: {P(t, "FLAWS")}";
        return $"[{Name(t.Model)}] " + Clip(text.Replace("\n", " "), 900);
    }

    private string OpeningPrompt() =>
        Context() + "\n\nGive your own opening view. Reply exactly in this format:\n" +
        "POSITION: the decision you recommend, in one or two sentences\nREASONS: the main reasons, short\n" +
        "FLAWS: the weaknesses, risks and downsides of the proposal and of your own position\n" +
        "QUESTION: ONE question for the user, only if you cannot decide without the answer; otherwise write NONE";

    private string ReviewPrompt() =>
        Context() + $"\n\n=== CURRENT DRAFT OF THE GROUP DECISION (version {DraftVersion}) ===\n{Draft}\n\n=== WHAT THE OTHERS SAID IN THE LAST ROUND ===\n" +
        string.Join("\n", _last.Select(Summary)) +
        "\n\nJudge the draft. Reply exactly in this format:\nVERDICT: AGREE or OBJECT\n" +
        "CHANGES: if you object, the exact change that would make you agree; otherwise NONE\n" +
        "FLAWS: the flaws or risks of the draft that are still not covered; otherwise NONE\n" +
        "QUESTION: ONE question for the user only if the answer would change your verdict; otherwise NONE";

    private async Task<Turn> SpeakAsync(AiModel m, string key, bool opening, CancellationToken ct)
    {
        try
        {
            var raw = await AiModels.AskAsync(m, key, SystemFor(m), opening ? OpeningPrompt() : ReviewPrompt(), 1500, ct);
            var parts = opening ? AiText.Sections(raw, "POSITION", "REASONS", "FLAWS", "QUESTION") : AiText.Sections(raw, "VERDICT", "CHANGES", "FLAWS", "QUESTION");
            if (parts.Count == 0) parts[opening ? "POSITION" : "CHANGES"] = raw;   // it ignored the format: keep what it said
            return new Turn(m, raw, parts, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return new Turn(m, "", new(), e is OperationCanceledException ? "no answer in time" : e.Message); }
    }

    private static bool Agrees(Turn t)
    {
        var v = P(t, "VERDICT").Trim();
        return v.StartsWith("AGREE", StringComparison.OrdinalIgnoreCase) && !v.Contains("OBJECT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks the moderator; when it fails, the next model takes over.</summary>
    private async Task<string> ModeratorAsync(Dictionary<string, string> keys, string system, string prompt, int maxTokens, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var m in new[] { Moderator }.Concat(Participants.Where(p => p != Moderator)))
        {
            if (!keys.TryGetValue(m.Id, out var key)) continue;
            try { var answer = await AiModels.AskAsync(m, key, system, prompt, maxTokens, ct); Moderator = m; return answer; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { last = e; }
        }
        throw new InvalidOperationException("No model could act as moderator: " + (last?.Message ?? "no keys"));
    }

    private const string ModeratorSystem =
        "You are the neutral moderator of a discussion between AI advisors. Merge their views into ONE concrete decision. " +
        "Reply in the language of the user's proposal, but keep the section labels exactly as asked, in English. No markdown tables.";

    public async Task RunAsync(CancellationToken ct)
    {
        Agreed = false;
        var keys = new Dictionary<string, string>();
        foreach (var m in Participants.ToList())
        {
            var key = await AiModels.LoadKeyAsync(Folder, m);
            if (key == null) { Emit("Skipped", Name(m), "", "warn", ("Why", "There is no API key for this model (AI models...).")); Participants.Remove(m); }
            else keys[m.Id] = key;
        }
        if (Participants.Count < 2) { OnStatus("At least two AI models with an API key are needed."); return; }
        if (!Participants.Contains(Moderator)) Moderator = Participants[0];

        while (Round < MaxRounds && DateTime.UtcNow < Deadline)
        {
            ct.ThrowIfCancellationRequested();
            Round++;
            var opening = Draft.Length == 0;
            OnStatus($"Round {Round} of {MaxRounds}: {Participants.Count} AIs are thinking...");
            var turns = await Task.WhenAll(Participants.ToList().Select(p => SpeakAsync(p, keys[p.Id], opening, ct)));

            var good = new List<Turn>();
            foreach (var t in turns)
            {
                if (t.Error != null) { Emit("Round " + Round, Name(t.Model), "left the discussion", "error", ("Error", t.Error)); Participants.Remove(t.Model); continue; }
                good.Add(t);
                if (opening) Emit("Round " + Round, Name(t.Model), t.Model.Role, "info", ("Position", P(t, "POSITION")), ("Reasons", P(t, "REASONS")), ("Flaws", AiText.IsNone(P(t, "FLAWS")) ? "" : P(t, "FLAWS")));
                else Emit("Round " + Round, Name(t.Model), Agrees(t) ? "agrees" : "objects", Agrees(t) ? "agree" : "object", ("Verdict", P(t, "VERDICT")), ("Wants", AiText.IsNone(P(t, "CHANGES")) ? "" : P(t, "CHANGES")), ("Flaws", AiText.IsNone(P(t, "FLAWS")) ? "" : P(t, "FLAWS")));
            }
            if (Participants.Count < 2) { OnStatus("Fewer than two AIs are left, so the discussion stopped."); return; }

            if (!opening && good.Count == Participants.Count && good.All(Agrees)) { Agreed = true; break; }

            _last = good;
            _lastOpening = opening;
            OnStatus($"Round {Round}: the moderator ({Name(Moderator)}) is merging the views...");
            var questions = await SynthesizeAsync(keys, ct);
            if (questions.Count > 0 && _asked < 3)
            {
                _asked++;
                OnStatus("The AIs have a question for you.");
                var answers = await AskUser(questions, ct);
                if (answers == null) Answers.Add("The user did not answer: decide with reasonable assumptions and say which ones.");
                else for (var i = 0; i < questions.Count; i++) Answers.Add($"Q: {questions[i]}\nA: {(i < answers.Count && answers[i].Trim().Length > 0 ? answers[i].Trim() : "(no answer)")}");
                if (answers != null) Draft = Draft;   // the next round judges the same draft, now with the answers
            }
        }

        await FinishAsync(keys, ct);
    }

    private async Task<List<string>> SynthesizeAsync(Dictionary<string, string> keys, CancellationToken ct)
    {
        var prompt = Context() + (Draft.Length > 0 ? $"\n\n=== PREVIOUS DRAFT (version {DraftVersion}) ===\n{Draft}" : "") +
                     "\n\n=== WHAT EACH AI SAID ===\n" + string.Join("\n", _last.Select(Summary)) +
                     "\n\nReply exactly in this format:\nDRAFT: the single decision the group should adopt: concrete, 2 to 6 sentences, including the changes that objecting AIs asked for when they are reasonable\n" +
                     "DISAGREEMENTS: what is still disputed, or NONE\n" +
                     "QUESTIONS: at most 3 questions that only the user can answer and that would change the decision, one per line; or NONE";
        var raw = await ModeratorAsync(keys, ModeratorSystem, prompt, 1500, ct);
        var parts = AiText.Sections(raw, "DRAFT", "DISAGREEMENTS", "QUESTIONS");
        Draft = parts.TryGetValue("DRAFT", out var d) && d.Length > 0 ? d : raw;
        DraftVersion++;
        var disagreements = parts.GetValueOrDefault("DISAGREEMENTS") ?? "";
        var questions = new List<string>();
        if (parts.TryGetValue("QUESTIONS", out var q) && !AiText.IsNone(q))
            foreach (var line in q.Split('\n'))
            {
                var text = line.Trim().TrimStart('-', '*', '•', ' ').Trim();
                var dot = 0;
                while (dot < text.Length && char.IsDigit(text[dot])) dot++;
                if (dot > 0 && dot < text.Length && (text[dot] == '.' || text[dot] == ')')) text = text[(dot + 1)..].Trim();
                if (text.Length > 3 && !AiText.IsNone(text)) questions.Add(text);
            }
        questions = questions.Take(3).ToList();
        Emit("Moderator", $"Draft {DraftVersion}", Name(Moderator), "draft", ("Draft", Draft), ("Still disputed", AiText.IsNone(disagreements) ? "" : disagreements));
        return questions;
    }

    /// <summary>The flaw check (when on), then the final decision written by the moderator.</summary>
    private async Task FinishAsync(Dictionary<string, string> keys, CancellationToken ct)
    {
        if (Draft.Length == 0) { OnStatus("There was no time for a first round. Add time and continue."); return; }
        var flaws = new StringBuilder();
        foreach (var t in _last) if (!AiText.IsNone(P(t, "FLAWS"))) flaws.AppendLine($"[{Name(t.Model)}] {P(t, "FLAWS")}");

        if (FlawCheck && Participants.Count >= 2)
        {
            OnStatus("Flaw check: every AI looks for what could go wrong...");
            var prompt = Context() + "\n\n=== THE DECISION ===\n" + Draft +
                         "\n\nBe a strict critic of this decision. Reply exactly in this format:\nFLAWS: the most serious flaws, risks and failure scenarios, numbered, most serious first\nFIXES: for each one, how to reduce it";
            var results = await Task.WhenAll(Participants.ToList().Select(async m =>
            {
                try
                {
                    var raw = await AiModels.AskAsync(m, keys[m.Id], SystemFor(m), prompt, 1200, ct);
                    return (m, AiText.Sections(raw, "FLAWS", "FIXES"), (string?)null, raw);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { return (m, new Dictionary<string, string>(), e.Message, ""); }
            }));
            foreach (var (m, parts, error, raw) in results)
            {
                if (error != null) { Emit("Flaws", Name(m), "", "error", ("Error", error)); continue; }
                var f = parts.GetValueOrDefault("FLAWS") ?? raw;
                var fix = parts.GetValueOrDefault("FIXES") ?? "";
                Emit("Flaws", Name(m), m.Role, "flaws", ("Flaws", f), ("How to reduce them", fix));
                flaws.AppendLine($"[{Name(m)}] FLAWS: {Clip(f, 900)} FIXES: {Clip(fix, 600)}");
            }
        }

        OnStatus("The moderator is writing the final decision...");
        var agreement = Agreed ? $"All {Participants.Count} AIs agreed to the draft."
            : "The AIs did NOT all agree before the time was up. Objections in the last round:\n" + string.Join("\n", _last.Where(t => !Agrees(t)).Select(Summary));
        var finalPrompt = Context() + "\n\n=== THE DRAFT THE GROUP REACHED ===\n" + Draft + "\n\n=== AGREEMENT ===\n" + agreement +
                          "\n\n=== FLAWS AND RISKS RAISED ===\n" + (flaws.Length > 0 ? flaws.ToString() : "none") +
                          "\n\nWrite the final decision for the user. Reply exactly in this format:\nDECISION: the one decision, clear and direct\nWHY: the main reasons\n" +
                          "NEXT STEPS: numbered, concrete\nFLAWS AND RISKS: the real flaws and risks and how to reduce each\nAGREEMENT: who agreed, and what is still open when they did not all agree";
        var final = await ModeratorAsync(keys, ModeratorSystem, finalPrompt, 2500, ct);
        var p = AiText.Sections(final, "DECISION", "WHY", "NEXT STEPS", "FLAWS AND RISKS", "AGREEMENT");
        Final = p.Count == 0 ? final : string.Join("\n\n", p.Select(x => x.Key + ": " + x.Value));
        if (p.TryGetValue("DECISION", out var decision) && decision.Length > 0) Draft = decision;   // a later "think longer" continues from this
        Emit("Decision", Agreed ? "Final decision" : "Best decision so far (no full agreement)", Name(Moderator), Agreed ? "final" : "warn",
            p.Count == 0 ? new[] { ("Decision", final) } : new[] { ("Decision", p.GetValueOrDefault("DECISION") ?? ""), ("Why", p.GetValueOrDefault("WHY") ?? ""), ("Next steps", p.GetValueOrDefault("NEXT STEPS") ?? ""), ("Flaws and risks", p.GetValueOrDefault("FLAWS AND RISKS") ?? ""), ("Agreement", p.GetValueOrDefault("AGREEMENT") ?? "") });
        OnStatus(Agreed ? "Agreed. The decision is ready." : "Time is up without full agreement. You can add time and continue.");
    }

    public string ToMarkdown() => "# AI Planning\n\n## Proposal\n" + Proposal + "\n\n" + Log;
}
