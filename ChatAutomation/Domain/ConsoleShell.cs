using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ChatAutomation.Domain;

// Interpretador de comandos tipados: não executa expressões C# arbitrárias.
public static class ConsoleShell
{
    private static readonly Regex ChatCall = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9_-]{0,49})\.(?<method>setText|sendMessage|setFocus|open|detectTextArea|getText|getFullName|inspectFullName|captureFullName|setFullName|clear|isGenerating|isBusy|busyWindow|checkMaxLimit|inspectMaxLimit|isAtMaxLimit)\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SelectedCall = new(
        @"^(?<method>setText|sendMessage|setFocus|open|detectTextArea|getText|getFullName|inspectFullName|captureFullName|setFullName|clear|isGenerating|isBusy|busyWindow|checkMaxLimit|inspectMaxLimit|isAtMaxLimit)\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LimitStateRead = new(
        @"^(?:(?<name>[A-Za-z][A-Za-z0-9_-]{0,49})\.)?isAtMaxLimit$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WebUseCall = new(
        @"^web\.use\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CaptureCall = new(
        @"^web\.captureChat\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddCall = new(
        @"^web\.addChat\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddProjectCall = new(
        @"^web\.addProject\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddProjectFromChatCall = new(
        @"^web\.addProjectFromChat\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PreviewNewChatCall = new(
        @"^web\.previewNewChat\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddNewChatCall = new(
        @"^web\.addNewChat\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CreateProjectChatCall = new(
        @"^web\.createProjectChat\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReopenClosedNewChatCall = new(
        @"^web\.reopenClosedNewChat\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ResumeNewChatCall = new(
        @"^web\.resumeNewChat\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SendFirstNewChatMessageCall = new(
        @"^web\.sendFirstNewChatMessage\s*\((?<args>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReconcileProjectChatCall = new(
        @"^web\.reconcileProjectChat\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CompleteNewChatCall = new(
        @"^web\.completeNewChat\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProjectListCall = new(
        @"^(?:web\.)?projectListChats\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UseProjectCall = new(
        @"^web\.useProject\s*\((?<arg>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HumanTextAssignment = new(
        @"^(?:web\.)?enableSendHumanText\s*=\s*(?<value>true|false)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HumanTextSetter = new(
        @"^(?:web\.)?(?:setEnableSendHumanText|enableSendHumanText)\s*\(\s*(?<value>true|false)\s*\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Run(WebInterface web)
    {
        Console.WriteLine("ChatAutomation V7.1 | console interativo (TAB: completar/ciclar; Shift+TAB: voltar; help())");
        Print(new { status = "READY", selectedChat = web.CurrentChatName, selectedProject = web.CurrentProjectId,
            enableSendHumanText = web.EnableSendHumanText, projects = web.ListProjects(), chats = web.ListChats() });
        var input = new ConsoleLineEditor();
        while (true)
        {
            var project = web.CurrentProject;
            var line = input.ReadLine(
                FormatPrompt(project?.FullName, web.CurrentChatName,
                    project?.Id, web.CurrentChat?.FullName),
                () => web.Chats.Select(chat => chat.Name).ToArray(),
                () => web.ListProjects().Select(p => p.Id).ToArray());
            if (line is null) return;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("quit", StringComparison.OrdinalIgnoreCase)) return;
            try { Dispatch(web, line); }
            catch (COMException ex)
            {
                // Falha UIA pode acontecer APÓS o clique. Nunca executar o
                // comando novamente automaticamente: expor reserva durável.
                var pending = web.PendingNewChats();
                Print(new
                {
                    status = "BLOCKED_UIA_UNCERTAIN",
                    reasonCode = "COMException",
                    hresult = $"0x{ex.HResult:X8}",
                    detail = ex.Message,
                    pending,
                    note = "Não repetir createProjectChat nem o envio. " +
                        "Se a reserva estiver FIRST_SEND_ATTEMPTED, executar " +
                        "web.reconcileProjectChat para o ID pendente (sem envio)."
                });
            }
            catch (Exception ex)
            {
                Print(new { status = "BLOCKED", reasonCode = ex.GetType().Name, detail = ex.Message });
            }
        }
    }

    // Mostra o nome completo do projeto do USE atual, não o nome interno
    // do executável nem o ID PRO_0000 como título. None é o estado inicial.
    internal static string FormatPrompt(string? projectFullName,
        string? selectedChatName, string? projectId,
        string? chatFullName = null)
    {
        var displayedProject = string.IsNullOrWhiteSpace(projectFullName)
            ? "None" : projectFullName;
        var displayedId = string.IsNullOrWhiteSpace(projectId) ? "None" : projectId;
        var displayedChat = selectedChatName ?? "-";
        // O projeto já aparece à direita. Abreviar SOMENTE o prefixo
        // coincidente do nome lógico do chat na apresentação; Name, URL,
        // seleção e identificação persistentes permanecem intocados.
        if (!string.IsNullOrWhiteSpace(selectedChatName) &&
            !string.IsNullOrWhiteSpace(projectId))
        {
            var duplicatePrefix = projectId + "-";
            if (displayedChat.StartsWith(duplicatePrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                displayedChat.Length > duplicatePrefix.Length)
                displayedChat = displayedChat[duplicatePrefix.Length..];
        }
        // Sem FullName, não renderizar separadores nem placeholders.
        if (!string.IsNullOrWhiteSpace(selectedChatName) &&
            !string.IsNullOrWhiteSpace(chatFullName))
            displayedChat += " - " + chatFullName.Trim();
        return $"{displayedProject}[{displayedChat}|{displayedId}]> ";
    }

    public static void Dispatch(WebInterface web, string line)
    {
        line = line.Trim().TrimEnd(';').Trim();
        if (OneOf(line, "help", "help()", "web.help()"))
        {
            Console.WriteLine(HelpText);
            return;
        }
        if (OneOf(line, "enableSendHumanText", "enableSendHumanText()",
            "web.enableSendHumanText", "web.enableSendHumanText()", "settings()", "web.settings()"))
        {
            Print(new { status = "SETTINGS", enableSendHumanText = web.EnableSendHumanText,
                path = ChatAutomation.State.AutomationSettings.FilePath });
            return;
        }
        var assignment = HumanTextAssignment.Match(line);
        var setter = HumanTextSetter.Match(line);
        if (assignment.Success || setter.Success)
        {
            var enabled = bool.Parse((assignment.Success ? assignment : setter).Groups["value"].Value);
            web.EnableSendHumanText = enabled;
            Print(new { status = "SETTINGS_SAVED", enableSendHumanText = web.EnableSendHumanText,
                path = ChatAutomation.State.AutomationSettings.FilePath });
            return;
        }
        if (OneOf(line, "listChats", "listChats()", "web.listChats()"))
        {
            Print(new { status = "OK", selectedChat = web.CurrentChatName, chats = web.ListChats() });
            return;
        }
        if (OneOf(line, "refresh", "refresh()", "web.refresh()"))
        {
            var refreshed = web.Refresh();
            Print(new { status = "REFRESHED", count = refreshed.Count, chats = refreshed });
            return;
        }
        if (OneOf(line, "use", "use()", "currentChat()", "web.currentChat()"))
        {
            Print(new { status = "CURRENT_CHAT", selectedChat = web.CurrentChatName,
                url = web.CurrentChat?.Url });
            return;
        }
        if (OneOf(line, "listProjects", "listProjects()", "web.listProjects()"))
        {
            Print(new { status = "OK", selectedProject = web.CurrentProjectId,
                projects = web.ListProjects() });
            return;
        }
        if (OneOf(line, "web.previewProjectFromEdge()", "previewProjectFromEdge()"))
        {
            var detected = web.PreviewProjectFromEdge();
            Print(new { status = "PREVIEW_ONLY", result = detected,
                approvalCommand = "web.addProjectFromEdge()",
                note = "Confirme o nome detectado no ChatGPT antes de cadastrar. " +
                    "Nenhum projeto foi gravado." });
            return;
        }
        if (OneOf(line, "web.addProjectFromEdge()", "addProjectFromEdge()"))
        {
            var registered = web.AddProjectFromEdge();
            Print(new { status = "PROJECT_ADDED", registered.Id,
                registered.FullName, registered.UrlPrefix,
                selectedProject = web.CurrentProjectId });
            return;
        }
        if (OneOf(line, "web.inspectProjectNewChat()", "inspectProjectNewChat()"))
        {
            var observedProjectUi = web.InspectProjectNewChat();
            Print(new { status = observedProjectUi.Status,
                selectedProject = web.CurrentProjectId, result = observedProjectUi });
            return;
        }
        if (OneOf(line, "pendingNewChats", "pendingNewChats()", "web.pendingNewChats()"))
        {
            Print(new { status = "OK", pending = web.PendingNewChats() });
            return;
        }
        if (OneOf(line, "use project", "use project()", "web.useProject()"))
        {
            Print(new { status = "CURRENT_PROJECT", selectedProject = web.CurrentProjectId });
            return;
        }
        // Selecionar projeto compatibiliza o contexto; um chat de outro
        // projeto é desselecionado, preservando seu cadastro.
        if (line.StartsWith("use project ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = line["use project ".Length..].Trim();
            var projectId = string.Equals(arg, "None", StringComparison.OrdinalIgnoreCase)
                ? null : arg.StartsWith('"') ? StringArgument(arg) : arg;
            var project = web.UseProject(projectId);
            Print(new { status = "USING_PROJECT", selectedProject = project?.Id ?? "None",
                fullName = project?.FullName, selectedChat = web.CurrentChatName });
            return;
        }
        var useProject = UseProjectCall.Match(line);
        if (useProject.Success)
        {
            var literal = useProject.Groups["arg"].Value.Trim();
            var id = string.Equals(literal, "None", StringComparison.OrdinalIgnoreCase)
                ? null : StringArgument(literal);
            var project = web.UseProject(id);
            Print(new { status = "USING_PROJECT", selectedProject = project?.Id ?? "None",
                selectedChat = web.CurrentChatName });
            return;
        }
        var fromRegisteredChat = AddProjectFromChatCall.Match(line);
        if (fromRegisteredChat.Success)
        {
            var args = StringArguments(fromRegisteredChat.Groups["args"].Value, 2);
            var project = web.AddProjectFromChat(args[0], args[1]);
            Print(new { status = "PROJECT_ADDED", project.Id, project.FullName, project.UrlPrefix,
                sourceChat = args[1], selectedProject = web.CurrentProjectId });
            return;
        }
        var projectCreate = AddProjectCall.Match(line);
        if (projectCreate.Success)
        {
            var args = StringArguments(projectCreate.Groups["args"].Value, 2);
            var project = web.AddProject(args[0], args[1]);
            Print(new { status = "PROJECT_ADDED", project.Id, project.FullName, project.UrlPrefix,
                selectedProject = web.CurrentProjectId });
            return;
        }
        var newChatPreview = PreviewNewChatCall.Match(line);
        if (newChatPreview.Success)
        {
            var args = ParseNewChatArguments(newChatPreview.Groups["args"].Value);
            var project = ResolveNewChatProject(web, args.ProjectToken);
            Print(new { status = "PREVIEW_ONLY", result = web.PreviewNewChat(args.Title, project),
                selectedProject = web.CurrentProjectId, selectedChat = web.CurrentChatName });
            return;
        }
        var newChatCall = AddNewChatCall.Match(line);
        if (newChatCall.Success)
        {
            var args = ParseNewChatArguments(newChatCall.Groups["args"].Value);
            var project = ResolveNewChatProject(web, args.ProjectToken);
            var created = project is null
                ? args.Title is null ? web.AddNewChat() : web.AddNewChat(args.Title)
                : args.Title is null ? web.AddNewChat(project) : web.AddNewChat(args.Title, project);
            Print(new { status = created.Status, result = created, selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var projectCompose = CreateProjectChatCall.Match(line);
        if (projectCompose.Success)
        {
            var args = ParseProjectChatArguments(projectCompose.Groups["args"].Value);
            var created = web.CreateProjectChat(args[0], args[1],
                args.Length == 3 ? args[2] : null);
            Print(new { status = created.Status, result = created,
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var reopenClosed = ReopenClosedNewChatCall.Match(line);
        if (reopenClosed.Success)
        {
            var reopenedChat = web.ReopenClosedNewChat(
                StringArgument(reopenClosed.Groups["arg"].Value));
            Print(new { status = reopenedChat.Status, result = reopenedChat,
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var newChatResume = ResumeNewChatCall.Match(line);
        if (newChatResume.Success)
        {
            var resumedChat = web.ResumeNewChat(StringArgument(newChatResume.Groups["arg"].Value));
            Print(new { status = resumedChat.Status, result = resumedChat,
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var firstNewChatSend = SendFirstNewChatMessageCall.Match(line);
        if (firstNewChatSend.Success)
        {
            var args = StringArguments(firstNewChatSend.Groups["args"].Value, 2);
            var firstSendResult = web.SendFirstNewChatMessage(args[0], args[1]);
            Print(new { status = "INPUT_DISPATCHED_UNCONFIRMED",
                result = firstSendResult, selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var reconcileProjectChat = ReconcileProjectChatCall.Match(line);
        if (reconcileProjectChat.Success)
        {
            var name = StringArgument(reconcileProjectChat.Groups["arg"].Value);
            var updated = web.ReconcileProjectChat(name);
            Print(new { status = updated.Status, result = updated,
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var newChatComplete = CompleteNewChatCall.Match(line);
        if (newChatComplete.Success)
        {
            var finalized = web.CompleteNewChat(
                StringArgument(newChatComplete.Groups["arg"].Value));
            Print(new { status = finalized.Status, result = finalized,
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName });
            return;
        }
        var projectChats = ProjectListCall.Match(line);
        if (projectChats.Success)
        {
            var raw = projectChats.Groups["arg"].Value.Trim();
            var id = raw.Length == 0 ? null : StringArgument(raw);
            var project = id is null ? web.CurrentProject : web.GetProject(id);
            Print(new { status = "OK", queriedProject = project?.Id ?? "None",
                selectedProject = web.CurrentProjectId,
                selectedChat = web.CurrentChatName,
                chats = web.ProjectListChats(project) });
            return;
        }
        // Sintaxe SQL: use "chat00"; ou use chat00; (aspas e ponto-e-vírgula opcionais).
        if (line.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
        {
            var nameText = line[4..].Trim();
            var name = nameText.StartsWith('"') ? StringArgument(nameText) : nameText;
            SelectChat(web, name);
            return;
        }
        var webUse = WebUseCall.Match(line);
        if (webUse.Success)
        {
            SelectChat(web, StringArgument(webUse.Groups["arg"].Value));
            return;
        }
        var add = AddCall.Match(line);
        if (add.Success)
        {
            var args = StringArguments(add.Groups["args"].Value, 2);
            var chat = web.AddChat(args[0], args[1]);
            Print(new { status = "ADDED", chat.Name, chat.Url });
            return;
        }
        var capture = CaptureCall.Match(line);
        if (capture.Success)
        {
            var chat = web.CaptureChat(StringArgument(capture.Groups["arg"].Value));
            Print(new { status = "CAPTURED", chat.Name, chat.Url,
                selectedChat = web.CurrentChatName,
                note = "Chat cadastrado. Use \"nome\" para selecioná-lo no console." });
            return;
        }
        if (line.StartsWith("bindToChat(", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "BIND_TO_CHAT_DEPRECATED: no console use web.captureChat(\"nome\") " +
                "para cadastrar a guia ativa e use \"nome\" para selecionar o chat.");
        // Propriedade sem parênteses: leitura do cache persistente, sem UIA.
        // isAtMaxLimit()/checkMaxLimit(): força uma nova inspeção UIA.
        var stateRead = LimitStateRead.Match(line);
        if (stateRead.Success)
        {
            var chat = stateRead.Groups["name"].Success
                ? web.GetChat(stateRead.Groups["name"].Value) : web.RequireCurrentChat();
            Print(new { chat = chat.Name, isAtMaxLimit = chat.IsAtMaxLimit, source = "cached" });
            return;
        }
        var match = ChatCall.Match(line);
        var selectedMatch = SelectedCall.Match(line);
        if (!match.Success && !selectedMatch.Success)
            throw new ArgumentException("Comando inválido. Use help() para consultar os métodos.");
        var chatObject = match.Success
            ? web.GetChat(match.Groups["name"].Value)
            : web.RequireCurrentChat();
        var operation = match.Success ? match : selectedMatch;
        var method = operation.Groups["method"].Value.ToLowerInvariant();
        var argumentValue = operation.Groups["arg"].Value.Trim();
        object? result = method switch
        {
            "settext" => chatObject.SetText(StringArgument(argumentValue)),
            "sendmessage" => argumentValue.Length == 0
                ? chatObject.SendMessage() : chatObject.SendMessage(StringArgument(argumentValue)),
            "gettext" when argumentValue.Length == 0 => chatObject.GetText(),
            "getfullname" when argumentValue.Length == 0 => chatObject.FullName,
            "inspectfullname" when argumentValue.Length == 0 => chatObject.InspectFullName(),
            "capturefullname" when argumentValue.Length == 0 => chatObject.CaptureFullName(),
            "setfullname" => chatObject.SetFullName(StringArgument(argumentValue)),
            "clear" when argumentValue.Length == 0 => chatObject.Clear(),
            "setfocus" when argumentValue.Length == 0 => chatObject.SetFocus(),
            "detecttextarea" when argumentValue.Length == 0 => chatObject.DetectTextArea(),
            "open" => chatObject.Open(ParseMode(argumentValue)),
            "isgenerating" when argumentValue.Length == 0 => chatObject.IsGenerating(),
            "isbusy" when argumentValue.Length == 0 => chatObject.IsBusy(),
            "busywindow" when argumentValue.Length == 0 => chatObject.BusyWindow(),
            "isatmaxlimit" when argumentValue.Length == 0 => chatObject.CheckMaxLimit(),
            "checkmaxlimit" when argumentValue.Length == 0 => chatObject.CheckMaxLimit(),
            "inspectmaxlimit" when argumentValue.Length == 0 => chatObject.InspectMaxLimit(),
            _ => throw new ArgumentException("Argumentos incorretos para o método.")
        };
        Print(new { chat = chatObject.Name, result });
    }

    private static Project? ResolveNewChatProject(WebInterface web, string? token) =>
        token switch
        {
            null => null,
            "Project" => web.CurrentProject ?? throw new InvalidOperationException(
                "PROJECT_NONE: use project \"PRO_0000\" antes de usar Project."),
            _ => web.GetProject(token)
        };

    public sealed record NewChatArguments(string? Title, string? ProjectToken);

    // O símbolo Project em chamadas do console refere-se ao USE de projeto.
    // Em C# a API real recebe uma instância tipada Project.
    public static NewChatArguments ParseNewChatArguments(string raw)
    {
        var args = raw.Trim();
        if (args.Length == 0) return new(null, null);
        if (args.Equals("Project", StringComparison.OrdinalIgnoreCase))
            return new(null, "Project");
        if (Regex.IsMatch(args, @"^PRO_\d{4,}$", RegexOptions.IgnoreCase))
            return new(null, args.ToUpperInvariant());

        var symbolSuffix = Regex.Match(args,
            @"^(?<title>.+),\s*(?<project>Project|PRO_\d{4,})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (symbolSuffix.Success)
            return new(StringArgument(symbolSuffix.Groups["title"].Value),
                symbolSuffix.Groups["project"].Value.Equals("Project", StringComparison.OrdinalIgnoreCase)
                    ? "Project" : symbolSuffix.Groups["project"].Value.ToUpperInvariant());

        var values = JsonSerializer.Deserialize<string[]>("[" + args + "]")
            ?? throw new ArgumentException("NEW_CHAT_INVALID_ARGUMENTS");
        return values.Length switch
        {
            1 when values[0] is not null => new(values[0], null),
            2 when values[0] is not null && values[1] is not null =>
                new(values[0], values[1]),
            _ => throw new ArgumentException(
                "NEW_CHAT_INVALID_ARGUMENTS: use (), (\"nome\"), (Project), (\"nome\", Project) ou (\"nome\", \"PRO_0000\").")
        };
    }

    // Sintaxe estritamente tipada; nunca interpreta expressões ou código arbitrário.
    public static string[] ParseProjectChatArguments(string raw)
    {
        var values = JsonSerializer.Deserialize<string[]>("[" + raw + "]") ??
            throw new ArgumentException("PROJECT_CHAT_ARGUMENTS_INVALID");
        if ((values.Length != 2 && values.Length != 3) ||
            values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "PROJECT_CHAT_ARGUMENTS_INVALID: use projeto, mensagem e título opcional.");
        return values;
    }

    private static void SelectChat(WebInterface web, string name)
    {
        var chat = web.UseChat(name);
        Print(new { status = "USING", selectedChat = chat.Name,
            selectedProject = web.CurrentProjectId, url = chat.Url,
            fullName = chat.FullName, fullNameSource = chat.FullNameSource,
            note = "Chat e projeto selecionados em conjunto; a guia do Edge não muda com USE." });
    }

    private static bool OneOf(string candidate, params string[] options) =>
        options.Any(o => candidate.Equals(o, StringComparison.OrdinalIgnoreCase));

    public static Mode ParseMode(string value) => value.Trim() switch
    {
        "Mode.NewTab" or "NewTab" or "new-tab" => Mode.NewTab,
        "Mode.NewWindow" or "NewWindow" or "new-window" => Mode.NewWindow,
        _ => throw new ArgumentException("Modo inválido. Use Mode.NewTab ou Mode.NewWindow.")
    };

    public static string StringArgument(string literal) => StringArguments(literal, 1)[0];

    // Parser JSON de argumentos: vírgulas e aspas escapadas funcionam dentro das strings.
    public static string[] StringArguments(string arguments, int expected)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>("[" + arguments + "]");
            if (values is null || values.Length != expected || values.Any(v => v is null))
                throw new ArgumentException($"Informe {expected} argumento(s) entre aspas duplas.");
            return values;
        }
        catch (JsonException)
        {
            throw new ArgumentException("Argumentos inválidos. Use strings JSON entre aspas duplas, por exemplo: \"texto\".");
        }
    }

    public const string HelpText = """
        CHAT AUTOMATION V7.1 — GUIA DE COMANDOS
        O console aceita métodos por nome do chat ou pelo contexto selecionado com USE.
        Todas as chamadas abaixo são executadas DENTRO do console interativo.

        PROJETOS E NOVAS CONVERSAS
        listProjects()                             Lista projetos cadastrados
        web.previewProjectFromEdge()             Lê nome pelo placeholder/título UIA, sem gravar
        web.addProjectFromEdge()                 Confirma a prévia e cadastra projeto selecionado
        web.addProject("Nome completo","https://chatgpt.com/g/g-p-...")  Cadastra projeto
        web.addProjectFromChat("Nome completo","GPTBOT_0001")             Extrai URL do chat já cadastrado
        use project "PRO_0000"                 Seleciona projeto; limpa chat de outro projeto
        use project None                          Desseleciona projeto e chat incompatível
        use project                               Mostra seleção atual
        projectListChats()                        Lista chats do projeto selecionado
        web.projectListChats("PRO_0000")       Lista chats de um projeto explícito
        web.previewNewChat()                      Mostra próximo ID, sem abrir guia ou reservar
        web.previewNewChat("Título", Project)      Prévia para o projeto selecionado
        web.addNewChat()                          Abre guia e reserva GPTBOT_0009 (ou projeto USE)
        web.addNewChat("Título descritivo")        Usa título e projeto USE (se existir)
        web.addNewChat(Project)                    Usa projeto atualmente selecionado
        web.addNewChat("Título", Project)           Título + projeto selecionado
        web.addNewChat("Título","PRO_0000")     Título + projeto explícito
        inspectProjectNewChat()                  Inspeciona botões do projeto sem ações
        web.inspectProjectNewChat()              Equivalente; requer aba do projeto selecionada
        pendingNewChats()                         Lista reservas de chats sem URL definitiva
        web.resumeNewChat("PRO_0000-GPTBOT_0000")  Retoma guia fixa por RuntimeId
        web.reopenClosedNewChat("PRO_0000-GPTBOT_0000")  Reabre guia FECHADA, mesma reserva
        Reabertura explícita exige TAB_CREATED e ausência comprovada do pin antigo.
        Não reabre após PROJECT_ACTION_ATTEMPTED: evitar criar conversas duplicadas.
        Falha no projeto: a URL raiz pode redirecionar a um chat antigo;
        a recuperação tenta ação específica de novo chat dentro do projeto,
        mantendo o HWND/RuntimeId e bloqueando CTAs genéricos.
        web.createProjectChat("PRO_0001","Mensagem")  Prepara /project na mesma guia; ENVIA e cadastra
        web.reconcileProjectChat("PRO_0001-GPTBOT_0000")  Lê UIA, cadastra URL final, nunca reenvia
        web.sendFirstNewChatMessage("ID","Texto")  ENVIO REAL inicial em /project
        web.completeNewChat("GPTBOT_0009")         Projetos: reconcilia e espera idle antes de registrar
        addNewChat nunca envia; sendFirstNewChatMessage envia apenas por ação explícita.
        Uma nova guia pode ficar OPENED_PENDING_URL até o ChatGPT criar sua URL.
        O identificador é persistido mesmo quando a abertura fica inconclusiva.

        AUTOCOMPLETE (somente no console interativo)
        TAB                  Completa e percorre as opções em ordem alfabética
        Shift+TAB            Percorre as opções no sentido inverso
        setT + TAB           setText("") com cursor ENTRE as aspas
        chat00.setT + TAB    chat00.setText("") com cursor ENTRE as aspas
        use G + TAB          use "GPTBOT_0001" (nome lido do catálogo em runtime)
        chat00. + TAB        Percorre os métodos de chat00
        open + TAB           Percorre open(Mode.NewTab) e open(Mode.NewWindow)
        ← / →, Home / End   Movimentam o cursor na linha editável
        ↑ / ↓               Histórico dos comandos nesta sessão
        Esc                  Limpa a linha sem executar o comando
        O TAB nunca envia mensagens: somente Enter executa o comando.

        NOME COMPLETO DO CHAT (metadado separado de GPTBOT_XXXX)
        use "PRO_0001-GPTBOT_0003"               Seleciona a conversa por ID
        getFullName()                            Lê o fullName salvo (sem UIA)
        inspectFullName()                        Lê TabItem da guia vinculada e confirma URL/RuntimeId
        captureFullName()                        Persiste título UIA sem criar chat ou enviar mensagem
        setFullName("Título exibido no ChatGPT")  Define manualmente, sem renomear no site
        PRO_0001-GPTBOT_0003.setFullName("Título")  Também aceita operação por ID
        fullName e fullNameSource aparecem em listChats() e projectListChats().
        Na criação, tenta capturar após idle; sem confirmação mantém fullName nulo.

        ACOMPANHAMENTO DA GERAÇÃO (somente leitura; não muda o foco)
        use "chat00"
        isGenerating()                              Retorna true se há botão UIA de interrupção ativo
        isBusy()                                    Alias de isGenerating(), retorna bool
        busyWindow()                                Abre CMD independente; atualiza a cada 8 s
        chat00.isBusy()                             Consulta pelo nome, sem executar USE

        LIMITE DE CONVERSA (leitura UIA passiva; sem clicar no botão)
        web.addChat("GPTBOT_0001","https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities/c/1d7a0124-91da-6bbe-d5e3-fec6ec8936cc")
        use "GPTBOT_0001"
        isAtMaxLimit                                Lê estado booleano persistido, inicia false
        isAtMaxLimit()                              Inspeciona UIA e atualiza o estado
        checkMaxLimit()                             Alias de isAtMaxLimit()
        inspectMaxLimit()                           Retorna evidências: aviso + botão, sem clicar
        GPTBOT_0001.checkMaxLimit()                 Consulta pelo nome, sem USE
        GPTBOT_0001.isAtMaxLimit                    Lê o estado armazenado, sem consulta UIA
        O estado fica true após duas evidências positivas e só é reiniciado ao
        vincular outro chat com URL diferente. Envio bloqueado para chats no limite.

        chat00.isGenerating()                       Consulta pelo nome, sem executar USE
        chat00.busyWindow()                         Abre monitor do chat00 sem alterar o USE
        Se a guia estiver inativa, ou a UIA não for conclusiva, retorna BLOCKED
        em vez de confundir "desconhecido" com false. Para monitorar, use setFocus()
        uma vez e mantenha essa guia ativa na sua janela do Edge.

        GUIAS DUPLICADAS
        Uma guia é fixada por HWND + RuntimeId UIA, não somente pela URL.
        Em duplicatas ambíguas, execute setFocus() com a guia desejada
        selecionada no Edge; não existe escolha aleatória.

        INICIAR E INSPECIONAR
        help()                                      Todos os comandos e exemplos
        listChats() / web.listChats()               Lista nomes e URLs registrados
        use                                         Mostra o chat selecionado nesta sessão
        currentChat()                               Mostra o chat selecionado nesta sessão
        web.addChat("chat00","https://chatgpt.com/c/ID")  Cadastra chat e disponibiliza imediatamente
        web.refresh()                               Recarrega alterações externas em chats.json
        web.captureChat("chat01")                    Cadastra a guia ativa no Edge (sem alterar USE)
        Se "chat01" já tiver outra URL, a captura é bloqueada; não muda o destino.
        USE de chat também seleciona seu projeto; USE de projeto limpa chat incompatível.
        use "chat01"                                Seleciona o contexto para os próximos comandos
        bindToChat() / bindToChat("nome")            Obsoleto no console; use captureChat + use
        exit                                        Encerra o console interativo

        CONFIGURAÇÃO PERSISTENTE DE TEXTO HUMANO
        enableSendHumanText                         Consulta valor salvo (padrão: false)
        enableSendHumanText = true                  Autoriza enviar texto existente/alterado por humano
        enableSendHumanText = false                 Volta a exigir setText() nesta sessão
        web.setEnableSendHumanText(true)           Outra forma de ativar (aceita false)
        web.enableSendHumanText()                   Consulta a configuração
        settings()                                  Mostra configuração e arquivo persistente
        A escolha é salva imediatamente em %LOCALAPPDATA%\ChatAutomation\settings.json.

        EXEMPLO — DIGITAR MANUALMENTE NO EDGE E ENVIAR PELO CONSOLE
        use "chat00"
        enableSendHumanText = true
        (digite ou altere o texto diretamente no campo do Edge)
        getText()                                   Confira o rascunho atual
        sendMessage()                               ENVIO REAL do conteúdo atual do editor
        enableSendHumanText = false                 Desativa novamente, se desejar

        FORMA 1 — ACESSO DIRETO PELO NOME (não precisa de USE)
        chat00.setFocus()                           Traz a janela e guia da URL exata ao foco
        chat00.detectTextArea()                      Localiza o editor de entrada
        chat00.setText("Rascunho")                  Substitui o texto SEM enviar
        chat00.getText()                            Lê somente o editor, não o histórico
        chat00.clear()                              Limpa somente o editor de entrada
        chat00.sendMessage()                        Com opção false, exige setText; com true, lê o editor atual
        chat00.sendMessage("Olá")                  Prepara e tenta enviar uma mensagem real
        chat00.open(Mode.NewTab)                    Abre guia e verifica URL antes de retornar
        chat00.open(Mode.NewWindow)                 Abre janela e verifica URL antes de retornar

        EXEMPLO DIRETO
        web.addChat("chat01","https://chatgpt.com/c/ID")
        chat01.setText("Teste sem envio")    Foco automático; setFocus opcional
        chat01.getText()
        chat01.clear()
        chat01.open(Mode.NewTab)
        chat01.setText("Texto depois de abrir")        URL e HWND já estão confirmados
        chat01.getText()
        chat01.sendMessage("Mensagem explícita")  ENVIO REAL

        FORMA 2 — SELECIONAR CONTEXTO, COMO USE EM BANCO DE DADOS
        use "chat00"                                Seleciona chat00 para esta sessão
        use chat00                                  Equivalente sem aspas
        USE "chat00";                               Equivalente, aceita ponto-e-vírgula
        web.use("chat00")                          Alternativa pelo método WebInterface
        use                                         Mostra qual chat está selecionado
        setFocus()                                  Foca a janela e guia (opcional antes de setText)
        detectTextArea()                            Detecta o editor do chat selecionado
        setText("Rascunho")                         Localiza/foca o editor e preenche SEM enviar
        getText()                                   Lê o editor selecionado
        clear()                                     Limpa o editor selecionado
        sendMessage()                               Envia texto atual se enableSendHumanText=true
        sendMessage("Olá")                         Prepara e tenta enviar mensagem real
        open(Mode.NewTab)                           Solicita abrir em nova guia
        open(Mode.NewWindow)                        Solicita abrir em nova janela

        EXEMPLO COM CONTEXTO SELECIONADO
        listChats()
        use "chat00"
        setText("Primeiro rascunho")                 setFocus() é opcional
        getText()
        clear()
        setText("Mensagem final")
        sendMessage()                               ENVIO REAL
        use "chat01"                                Troca de contexto sem reiniciar o console
        open(Mode.NewWindow)

        ADICIONAR CHAT DURANTE A SESSÃO
        web.addChat("chat02","https://chatgpt.com/c/ID")
        use "chat02"                                Disponível imediatamente após addChat
        setFocus()
        web.refresh()                               Necessário apenas para mudanças externas
        use                                         Confere o contexto da sessão

        CLI POWERSHELL 7 (FORA do console interativo)
        .\chat-automation.ps1                       Abre o console interativo
        .\chat-features.ps1 help                    Exibe esta ajuda
        .\chat-features.ps1 settings
        .\chat-features.ps1 set-send-human-text -Enabled true
        .\chat-features.ps1 list-chats
        .\chat-features.ps1 add-chat -Name chat03 -Url 'https://chatgpt.com/c/ID'
        .\chat-features.ps1 refresh-chats
        .\chat-features.ps1 focus-chat -Chat chat03
        .\chat-features.ps1 set-text -Chat chat03 -Text 'Rascunho'
        .\chat-features.ps1 get-text -Chat chat03
        .\chat-features.ps1 clear-chat -Chat chat03
        .\chat-features.ps1 send-chat-message -Chat chat03 -Text 'Mensagem'
        .\chat-features.ps1 open-chat -Chat chat03 -Mode new-window
        .\chat-features.ps1 remove-chat -Chat chat03
        .\bind-to-chat.ps1 -Name chat04           LEGADO: captura e vínculo global compatível

        COMPORTAMENTO E SEGURANÇA
        use seleciona apenas o contexto do console; setText() foca Edge/editor automaticamente.
        setFocus() continua disponível para foco explícito, mas não é obrigatório antes de setText().
        Ao trocar de contexto, outros chats continuam registrados e acessíveis pelo nome.
        Um chat removido ou uma URL alterada externamente desativa a seleção após refresh().
        Sem USE, chamadas sem prefixo retornam erro (nunca escolhem um chat arbitrário).
        setText() não envia. sendMessage() executa envio REAL, sem comprovar entrega remota.
        Toda mensagem preparada/enviada pelo programa começa com [dd/MM/yyyy-HH:mm] em UTC-3.
        O cabeçalho aparece na primeira linha, seguido do conteúdo em outra linha.
        setText() marca ao preparar; sendMessage() preserva esse horário sem duplicar.
        enableSendHumanText=false exige rascunho preparado na sessão, sem alterações humanas.
        enableSendHumanText=true também adiciona o cabeçalho a rascunhos humanos sem
        timestamp, substituindo o texto no editor e verificando o readback antes do envio.
        Ambos os modos exigem URL exata e texto legível e não vazio antes do clique.
        clear() tenta apagar repetidamente e identifica placeholders conhecidos do editor.
        getText() lê o editor via UIA; se o provedor não expuser texto, retorna erro.
        Um campo vazio (incluindo placeholder "Pergunte ao ChatGPT") retorna "".
        refresh() invalida rascunhos pendentes para evitar envio desatualizado.
        A localização da janela depende da URL exata, não só do título da guia.
        """;

    private static void Print(object value) => Output.Print(value);
}
