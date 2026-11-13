using System.Text.RegularExpressions;
using AsdPython;

namespace PyScript;

/// <summary>
/// Suggestions for the module names after "import" and "from": the standard library and the packages installed for the Python that runs
/// the code (asked from Python itself, remembered for a few minutes), plus popular packages that are not installed, marked with the pip command.
/// After "from x import " the names inside x are suggested.
/// </summary>
internal sealed partial class PyScriptWindow
{
    /// <param name="Members">true = after "from x import" (names inside x), false = a module name</param>
    /// <param name="Module">the package before the last dot ("os" in "import os.pa"), null = a top-level module</param>
    private sealed record ImportContext(bool Members, string? Module, string Prefix);

    private sealed record ModuleInfo(string Name, bool Standard, bool Installed);

    private const string ModulesScript = """
        import sys, os, pkgutil, sysconfig
        std = set(getattr(sys, 'stdlib_module_names', ()))
        lib = os.path.normcase(sysconfig.get_paths()['stdlib'])
        found = {}
        for m in pkgutil.iter_modules():
            n = m.name
            if n.startswith('_'):
                continue
            p = os.path.normcase(str(getattr(m.module_finder, 'path', '') or ''))
            found[n] = 'S' if (n in std or (not std and p == lib)) else 'I'
        for n in std:
            if not n.startswith('_'):
                found.setdefault(n, 'S')
        print(' '.join(k + ':' + v for k, v in sorted(found.items())))
        """;

    // the modules inside a package, found without importing anything (python script.py package.name)
    private const string SubmodulesScript = """
        import sys, pkgutil
        from importlib.machinery import PathFinder
        path = None
        for part in sys.argv[1].split('.'):
            spec = PathFinder.find_spec(part, path)
            if spec is None or not spec.submodule_search_locations:
                sys.exit(0)
            path = list(spec.submodule_search_locations)
        print(' '.join(m.name for m in pkgutil.iter_modules(path) if not m.name.startswith('_')))
        """;

    // import name -> the name for "pip install"
    private static readonly Dictionary<string, string> Popular = new()
    {
        ["numpy"] = "numpy", ["pandas"] = "pandas", ["requests"] = "requests", ["matplotlib"] = "matplotlib", ["scipy"] = "scipy",
        ["flask"] = "flask", ["django"] = "django", ["fastapi"] = "fastapi", ["pytest"] = "pytest", ["torch"] = "torch",
        ["tensorflow"] = "tensorflow", ["sklearn"] = "scikit-learn", ["cv2"] = "opencv-python", ["PIL"] = "pillow",
        ["bs4"] = "beautifulsoup4", ["selenium"] = "selenium", ["sqlalchemy"] = "sqlalchemy", ["pygame"] = "pygame",
        ["aiohttp"] = "aiohttp", ["httpx"] = "httpx", ["pydantic"] = "pydantic", ["yaml"] = "pyyaml", ["openai"] = "openai",
        ["anthropic"] = "anthropic", ["tqdm"] = "tqdm", ["rich"] = "rich", ["click"] = "click", ["jinja2"] = "jinja2",
        ["cryptography"] = "cryptography", ["paramiko"] = "paramiko", ["psutil"] = "psutil", ["lxml"] = "lxml",
        ["openpyxl"] = "openpyxl", ["seaborn"] = "seaborn", ["plotly"] = "plotly", ["streamlit"] = "streamlit",
        ["dotenv"] = "python-dotenv", ["serial"] = "pyserial", ["dateutil"] = "python-dateutil", ["pyautogui"] = "pyautogui",
        ["discord"] = "discord.py", ["websockets"] = "websockets", ["nltk"] = "nltk", ["transformers"] = "transformers",
    };

    // shown first when nothing (or little) is typed
    private static readonly string[] CommonModules =
        ("os sys json re math random datetime time pathlib collections itertools functools typing subprocess threading asyncio logging " +
         "argparse csv sqlite3 urllib http socket shutil tkinter unittest numpy pandas requests matplotlib").Split(' ');

    private static readonly HashSet<string> HiddenModules = new() { "antigravity", "this", "idlelib", "turtledemo", "test" };

    private List<ModuleInfo>? _modules;
    private DateTime _modulesAt;
    private Task<List<ModuleInfo>>? _modulesTask;
    private readonly Dictionary<string, string[]> _submodules = new();

    /// <summary>What the line before the caret asks for, or null when it is not an import line.</summary>
    private static ImportContext? ImportContextAt(string text, int caret)
    {
        caret = Math.Min(caret, text.Length);
        var start = caret;
        while (start > 0 && text[start - 1] != '\r' && text[start - 1] != '\n') start--;
        var line = text[start..caret];

        var from = Regex.Match(line, @"^[ \t]*from[ \t]+([A-Za-z_][\w.]*)[ \t]+import[ \t]+(.*)$");
        if (from.Success)
        {
            var rest = from.Groups[2].Value;
            var tail = rest[(rest.LastIndexOfAny(new[] { ',', '(' }) + 1)..].TrimStart();
            return Regex.IsMatch(tail, @"^\w*$") ? new ImportContext(true, from.Groups[1].Value.TrimEnd('.'), tail) : null;
        }

        var module = Regex.Match(line, @"^[ \t]*from[ \t]+([A-Za-z_][\w.]*)?$");
        if (module.Success) return Dotted(module.Groups[1].Value);

        var import = Regex.Match(line, @"^[ \t]*import[ \t]+(.*)$");
        if (import.Success)
        {
            var rest = import.Groups[1].Value;
            var tail = rest[(rest.LastIndexOf(',') + 1)..].TrimStart();
            return Regex.IsMatch(tail, @"^[\w.]*$") ? Dotted(tail) : null;
        }
        return null;
    }

    private static ImportContext? Dotted(string typed)
    {
        var dot = typed.LastIndexOf('.');
        if (dot < 0) return new ImportContext(false, null, typed);
        return dot == 0 ? null : new ImportContext(false, typed[..dot], typed[(dot + 1)..]);
    }

    private static string Describe(ModuleInfo m) =>
        m.Standard ? "standard library"
        : m.Installed ? "installed"
        : "not installed · pip install " + (Popular.TryGetValue(m.Name, out var pip) ? pip : m.Name);

    private static int Rank(string name)
    {
        var i = Array.IndexOf(CommonModules, name);
        return i < 0 ? int.MaxValue : i;
    }

    private Task<List<ModuleInfo>> ModulesAsync()
    {
        if (_modules != null && DateTime.UtcNow - _modulesAt < TimeSpan.FromMinutes(5)) return Task.FromResult(_modules);
        return _modulesTask ??= LoadModulesAsync();
    }

    private async Task<List<ModuleInfo>> LoadModulesAsync()
    {
        try
        {
            var list = await Task.Run(() =>
            {
                var result = new List<ModuleInfo>();
                var py = Py.Find();
                if (py == null) return result;
                var script = Path.Combine(_folder, "list_modules.py");
                File.WriteAllText(script, ModulesScript);
                var (code, output) = Py.RunQuick(py.File, py.Prefix.Append(script), 20000);
                if (code != 0) return result;
                foreach (var token in output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = token.LastIndexOf(':');
                    if (colon <= 0) continue;
                    var name = token[..colon];
                    if (HiddenModules.Contains(name)) continue;
                    result.Add(new ModuleInfo(name, token[(colon + 1)..] == "S", true));
                }
                if (result.Count > 0)
                {
                    var have = new HashSet<string>(result.Select(m => m.Name));
                    foreach (var name in Popular.Keys)
                        if (!have.Contains(name)) result.Add(new ModuleInfo(name, false, false));
                }
                return result;
            });
            if (list.Count > 0)
            {
                _modules = list;
                _modulesAt = DateTime.UtcNow;
            }
            return list;
        }
        finally { _modulesTask = null; }
    }

    private async Task<string[]> SubmodulesAsync(string module)
    {
        if (!Regex.IsMatch(module, @"^[A-Za-z_]\w*(\.\w+)*$")) return Array.Empty<string>();
        if (_submodules.TryGetValue(module, out var known)) return known;
        var names = await Task.Run(() =>
        {
            var py = Py.Find();
            if (py == null) return Array.Empty<string>();
            var script = Path.Combine(_folder, "list_submodules.py");
            File.WriteAllText(script, SubmodulesScript);
            var (code, output) = Py.RunQuick(py.File, py.Prefix.Append(script).Append(module), 10000);
            return code == 0 ? output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        });
        _submodules[module] = names;
        return names;
    }

    private async Task ShowImportSuggestionsAsync(ImportContext c, string text, int caret)
    {
        bool Fits(string name) => name.StartsWith(c.Prefix, StringComparison.OrdinalIgnoreCase) && !name.Equals(c.Prefix, StringComparison.Ordinal);

        List<Suggestion> items;
        if (c.Members)
        {
            var members = (await MembersAsync(c.Module!, text)).Select(ParseMember).ToList();
            foreach (var sub in await SubmodulesAsync(c.Module!))
                if (members.All(m => m.Label != sub)) members.Add(new Suggestion(sub, sub, "module", "submodule"));
            items = members.Where(m => Fits(m.Label)).Take(60).ToList();
        }
        else if (c.Module == null)
        {
            var all = await ModulesAsync();
            items = all.Where(m => Fits(m.Name))
                .OrderBy(m => m.Installed ? 0 : 1).ThenBy(m => Rank(m.Name)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .Select(m => new Suggestion(m.Name, m.Name, m.Installed ? "module" : "missing", Describe(m)))
                .ToList();
        }
        else
        {
            items = (await SubmodulesAsync(c.Module)).Where(Fits)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .Select(n => new Suggestion(n, n, "module", "in " + c.Module))
                .ToList();
        }

        if (_editor.SelectionStart != caret || _editor.Text != text) return;   // the person moved on
        if (items.Count == 0) { HideBubble(); return; }
        ShowBubble(items, caret - c.Prefix.Length, c.Prefix.Length);
    }
}
