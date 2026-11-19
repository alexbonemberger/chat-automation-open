namespace ChatAutomation.Domain;

// Parte pura, sem Console/UIA: a ordem do TAB é estável, inclusive quando
// existem vários chats e o mesmo método aparece em todos.
public sealed record CompletionItem(string Text, int Cursor);

public static class ConsoleCompletions
{
    private static readonly string[] ZeroArgumentMethods =
    [
        "clear()", "getText()", "getFullName()", "inspectFullName()",
        "captureFullName()", "setFocus()", "detectTextArea()",
        "isBusy()", "isGenerating()", "busyWindow()", "isAtMaxLimit",
        "isAtMaxLimit()", "checkMaxLimit()", "inspectMaxLimit()"
    ];

    private static readonly string[] ArgumentMethods =
    [
        "setText(\"\")", "setFullName(\"\")",
        "sendMessage()", "sendMessage(\"\")",
        "open(Mode.NewTab)", "open(Mode.NewWindow)"
    ];

    private static readonly string[] GlobalCommands =
    [
        "help()", "listChats()", "settings()", "exit", "quit",
        "refresh()", "web.refresh()", "web.listChats()",
        "web.addChat(\"\",\"\")", "web.captureChat(\"\")", "web.use(\"\")",
        "web.addProject(\"\",\"\")", "web.addProjectFromChat(\"\",\"\")",
        "web.previewProjectFromEdge()", "web.addProjectFromEdge()",
        "previewProjectFromEdge()", "addProjectFromEdge()",
        "web.previewNewChat()", "web.previewNewChat(\"\")",
        "web.previewNewChat(Project)", "web.previewNewChat(\"\",Project)",
        "web.addNewChat()", "web.addNewChat(\"\")",
        "web.addNewChat(Project)", "web.addNewChat(\"\",Project)",
        "web.resumeNewChat(\"\")", "web.reopenClosedNewChat(\"\")",
        "web.createProjectChat(\"\",\"\")",
        "web.createProjectChat(\"\",\"\",\"\")",
        "web.sendFirstNewChatMessage(\"\",\"\")",
        "web.reconcileProjectChat(\"\")",
        "web.completeNewChat(\"\")",
        "web.pendingNewChats()", "web.projectListChats()",
        "listProjects()", "pendingNewChats()", "projectListChats()",
        "inspectProjectNewChat()", "web.inspectProjectNewChat()",
        "use project", "use project None",
        "enableSendHumanText", "enableSendHumanText = true",
        "enableSendHumanText = false"
    ];

    public static IReadOnlyList<CompletionItem> Find(string prefix, IEnumerable<string> registeredChats,
        IEnumerable<string>? registeredProjects = null)
    {
        if (prefix is null) throw new ArgumentNullException(nameof(prefix));
        // Nao substituir texto digitado dentro de parametros do C#.
        // A excecao 'use' aceita autocomplete dentro das aspas.
        var isUseProject = prefix.Equals("use project", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("use project ", StringComparison.OrdinalIgnoreCase);
        var isUse = !isUseProject && (prefix.StartsWith("use ", StringComparison.OrdinalIgnoreCase) ||
                    prefix.Equals("use", StringComparison.OrdinalIgnoreCase));
        if (!isUse && !isUseProject && prefix.Contains('(') &&
            !prefix.EndsWith('(') && !prefix.EndsWith("(\"", StringComparison.Ordinal))
            return [];

        var distinctNames = registeredChats
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = new List<string>(GlobalCommands);
        if (isUseProject)
        {
            candidates.AddRange((registeredProjects ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => "use project \"" + id + "\""));
        }
        else if (isUse)
        {
            // use chat, use "chat e use G filtram os mesmos nomes.
            var typed = prefix.Length <= 3 ? "" : prefix[3..].TrimStart().TrimStart('"');
            candidates.AddRange(distinctNames
                .Where(name => name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Select(name => "use \"" + name + "\""));
        }
        else
        {
            candidates.Add("use");
            foreach (var name in distinctNames)
            {
                candidates.Add("use \"" + name + "\"");
                foreach (var method in ZeroArgumentMethods)
                    candidates.Add(name + "." + method);
                foreach (var method in ArgumentMethods)
                    candidates.Add(name + "." + method);
            }
            candidates.AddRange(ZeroArgumentMethods);
            candidates.AddRange(ArgumentMethods);
        }

        // Prefixo 'use chat' nao e prefixo textual de 'use "chat"'.
        var search = isUseProject
            ? "use project \"" + prefix["use project".Length..].TrimStart().TrimStart('"')
            : isUse ? prefix.Length <= 3 ? "use \"" : "use \"" + prefix[3..].TrimStart().TrimStart('"')
            : prefix;
        // Usuario que digita so "use" tambem pode completar diretamente.
        return candidates
            .Where(text => text.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                ((!isUse || isUseProject) && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
            .Select(text => new CompletionItem(text, CursorFor(text)))
            .ToArray();
    }

    private static int CursorFor(string text)
    {
        var emptyQuotes = text.IndexOf("\"\"", StringComparison.Ordinal);
        if (emptyQuotes >= 0) return emptyQuotes + 1;
        return text.Length;
    }
}

public sealed class CompletionCycle
{
    private IReadOnlyList<CompletionItem> _items = [];
    private int _current = -1;

    public CompletionItem? Next(string currentLine, IEnumerable<string> chatNames, bool backwards = false,
        IEnumerable<string>? projectIds = null)
    {
        if (_current < 0)
        {
            _items = ConsoleCompletions.Find(currentLine, chatNames, projectIds);
            if (_items.Count == 0) return null;
            _current = backwards ? _items.Count - 1 : 0;
        }
        else
        {
            _current = (_current + (backwards ? -1 : 1) + _items.Count) % _items.Count;
        }
        return _items[_current];
    }

    public void Reset()
    {
        _current = -1;
        _items = [];
    }
}
