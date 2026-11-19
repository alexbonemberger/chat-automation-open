using System.Text.Json;
using System.Runtime.InteropServices;
using ChatAutomation.Browser;
using ChatAutomation.Domain;
using ChatAutomation.Features;
using ChatAutomation.State;

namespace ChatAutomation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var command = args.Length == 0 ? "shell" : args[0].ToLowerInvariant();
            var cli = Options.Parse(args.Skip(args.Length == 0 ? 0 : 1).ToArray());
            // O console moderno usa exclusivamente web.use. O arquivo de
            // vínculo global é carregado somente pelas rotas legadas.
            if (command is "bind-to-chat" or "show-binding" or "clear-binding"
                or "focus-current-chat" or "open-current-chat" ||
                ((command is "send-message" or "detect-text-area") && cli.Get("chat") is null))
                CurrentBinding.Load();
            if (command is "help" or "--help" or "-h")
            {
                Console.WriteLine(Help);
                return 0;
            }
            if (command == "self-test")
            {
                SelfTest.Run();
                Output.Print(new { status = "PASS", test = "URL identity, deterministic tab selection, stop classifier, message timestamp UTC-3, shell autocomplete and parser, editor readback, registry refresh, persistent human-text setting" });
                return 0;
            }
            using var web = new WebInterface();
            switch (command)
            {
                case "shell":
                    ConsoleShell.Run(web);
                    return 0;
                case "check-max-limit":
                    var limitChat = web.GetChat(RequireChatName(cli));
                    Output.Print(new { chat = limitChat.Name,
                        isAtMaxLimit = limitChat.CheckMaxLimit(), source = "uia-verified-and-persisted" });
                    return 0;
                case "inspect-max-limit":
                    var targetChat = web.GetChat(RequireChatName(cli));
                    Output.Print(new { chat = targetChat.Name, evidence = targetChat.InspectMaxLimit(),
                        isAtMaxLimit = targetChat.IsAtMaxLimit });
                    return 0;
                case "is-busy":
                case "is-generating":
                    Output.Print(new { chat = RequireChatName(cli),
                        isBusy = web.GetChat(RequireChatName(cli)).IsBusy() });
                    return 0;
                case "busy-window":
                    Output.Print(web.GetChat(RequireChatName(cli)).BusyWindow());
                    return 0;
                case "busy-monitor":
                    MonitorBusy(web.GetChat(RequireChatName(cli)));
                    return 0;
                case "settings":
                    Output.Print(new { status = "SETTINGS", enableSendHumanText = web.EnableSendHumanText,
                        path = AutomationSettings.FilePath });
                    return 0;
                case "set-send-human-text":
                    var enabledText = cli.Get("enabled") ?? throw new ArgumentException("Informe --enabled true ou false.");
                    if (!bool.TryParse(enabledText, out var enabled))
                        throw new ArgumentException("--enabled aceita somente true ou false.");
                    web.EnableSendHumanText = enabled;
                    Output.Print(new { status = "SETTINGS_SAVED", enableSendHumanText = web.EnableSendHumanText,
                        path = AutomationSettings.FilePath });
                    return 0;
                case "list-chats":
                    Output.Print(new { status = "OK", chats = web.ListChats(), path = ChatRegistry.FilePath });
                    return 0;
                case "refresh-chats":
                    var refreshed = web.Refresh();
                    Output.Print(new { status = "REFRESHED", count = refreshed.Count, chats = refreshed });
                    return 0;
                case "add-chat":
                    var added = web.AddChat(cli.Get("name") ?? throw new ArgumentException("Informe --name."),
                        cli.Get("url") ?? throw new ArgumentException("Informe --url."));
                    Output.Print(new { status = "ADDED", name = added.Name, url = added.Url });
                    return 0;
                case "get-text":
                    Output.Print(new { status = "READ", chat = RequireChatName(cli),
                        text = web.GetChat(RequireChatName(cli)).GetText() });
                    return 0;
                case "clear-chat":
                    Output.Print(new { chat = RequireChatName(cli),
                        result = web.GetChat(RequireChatName(cli)).Clear() });
                    return 0;
                case "bind-to-chat":
                    // A rota CLI é mantida para scripts externos existentes.
                    // Apenas esta chamada intencional usa a API obsoleta.
#pragma warning disable CS0618
                    var bound = web.BindToChat(cli.Get("name"), cli.Get("url"),
                        cli.Get("profile-directory"), cli.GetLong("window-hwnd"));
#pragma warning restore CS0618
                    Output.Print(new { status = "BOUND", name = bound.Name, url = bound.Url,
                        currentBindedChat = CurrentBinding.currentBindedChat });
                    return 0;
                case "remove-chat":
                    web.RemoveChat(RequireChatName(cli));
                    Output.Print(new { status = "REMOVED", chat = cli.Get("chat") });
                    return 0;
                case "show-binding":
                    Output.Print(new { status = "OK", currentBindedChat = CurrentBinding.currentBindedChat });
                    return 0;
                case "clear-binding":
                    CurrentBinding.Clear();
                    Output.Print(new { status = "CLEARED", note = "Catálogo de chats preservado." });
                    return 0;
                case "focus-chat":
                    Output.Print(new { status = "FOCUSED_EXACT_URL", chat = RequireChatName(cli),
                        match = web.GetChat(RequireChatName(cli)).SetFocus() });
                    return 0;
                case "set-text":
                    Output.Print(new { chat = RequireChatName(cli),
                        result = web.GetChat(RequireChatName(cli)).SetText(LoadMessage(cli)) });
                    return 0;
                case "send-chat-message":
                    Output.Print(new { chat = RequireChatName(cli),
                        result = web.GetChat(RequireChatName(cli)).SendMessage(LoadMessage(cli)) });
                    return 0;
                case "open-chat":
                    Output.Print(new { chat = RequireChatName(cli),
                        result = web.GetChat(RequireChatName(cli)).Open(
                            ParseMode(cli.Get("mode")), cli.GetLong("window-hwnd"), cli.Get("profile-directory")) });
                    return 0;
                case "focus-current-chat":
                    Output.Print(new { status = "FOCUSED_EXACT_URL", match = ChatFeatures.FocusCurrentChat(web.Automation) });
                    return 0;
                case "detect-text-area":
                    Output.Print(new { status = "COMPOSER_FOUND", editor = cli.Get("chat") is { } chatName
                        ? web.GetChat(chatName).DetectTextArea() : ChatFeatures.DetectTextArea(web.Automation) });
                    return 0;
                case "send-message":
                    var message = LoadMessage(cli);
                    Output.Print(cli.Get("chat") is { } selected
                        ? web.GetChat(selected).SendMessage(message)
                        : ChatFeatures.SendMessage(web.Automation, message));
                    return 0;
                case "open-current-chat":
                    Output.Print(ChatFeatures.OpenCurrentChat(web.Automation, ParseLegacyMode(cli.Get("mode")),
                        cli.GetLong("window-hwnd"), cli.Get("profile-directory")));
                    return 0;
                case "inspect-edge":
                    Output.Print(new { status = "INSPECTED", controls = EdgeBrowser.Inspect(web.Automation,
                        cli.GetLong("window-hwnd")) });
                    return 0;
                case "list-edge-windows":
                    Output.Print(new { status = "OK", windows = EdgeBrowser.Windows().Select(h => new
                        { hwnd = h.ToInt64(), title = ChatAutomation.Infrastructure.Win32.Title(h) }).ToArray() });
                    return 0;
                default:
                    throw new ArgumentException("Comando não reconhecido: " + command + ". Use help.");
            }
        }
        catch (Exception ex)
        {
            Output.Print(new { status = "BLOCKED", reasonCode = ex.GetType().Name, detail = ex.Message });
            return 2;
        }
    }

    private static void MonitorBusy(Chat chat)
    {
        const int intervalSeconds = 8;
        var nextLimitProbe = DateTimeOffset.MinValue;
        Console.CursorVisible = false;
        try
        {
            while (true)
            {
                bool? busy = null;
                string? problem = null;
                string? limitProblem = null;
                try { busy = chat.IsBusy(); }
                catch (Exception ex) { problem = ex.Message; }

                // Checagem de limite independente e de baixa frequência:
                // imediatamente na primeira ociosidade e depois a cada 60 s.
                if (busy == false && !chat.IsAtMaxLimit && DateTimeOffset.UtcNow >= nextLimitProbe)
                {
                    nextLimitProbe = DateTimeOffset.UtcNow.AddMinutes(1);
                    try { chat.CheckMaxLimit(); }
                    catch (Exception ex) { limitProblem = ex.Message; }
                }

                Console.Clear();
                Console.WriteLine($"CHAT: {chat.Name}");
                Console.WriteLine($"URL: {chat.Url}");
                Console.WriteLine($"isBusy(): {(busy.HasValue ? busy.Value.ToString().ToLowerInvariant() : "UNKNOWN")}");
                Console.WriteLine($"isAtMaxLimit: {chat.IsAtMaxLimit.ToString().ToLowerInvariant()}");
                if (problem is not null) Console.WriteLine($"UIA busy: {problem}");
                if (limitProblem is not null) Console.WriteLine($"UIA limite: {limitProblem}");
                Console.WriteLine("Sair: Ctrl+C");
                for (var seconds = intervalSeconds; seconds > 0; seconds--)
                {
                    Console.Write($"\rPróxima leitura em {seconds}s   ");
                    Thread.Sleep(1000);
                }
            }
        }
        finally { Console.CursorVisible = true; }
    }

    private static string RequireChatName(Options cli) => cli.Get("chat")
        ?? throw new ArgumentException("Informe --chat nome, exemplo --chat chat00.");

    private static Mode ParseMode(string? arg) => (arg ?? "new-tab").ToLowerInvariant() switch
    {
        "new-tab" or "newtab" => Mode.NewTab,
        "new-window" or "newwindow" => Mode.NewWindow,
        _ => throw new ArgumentException("--mode aceita new-tab ou new-window.")
    };

    private static ChatOpenMode ParseLegacyMode(string? arg) =>
        ParseMode(arg) == Mode.NewTab ? ChatOpenMode.NewTab : ChatOpenMode.NewWindow;

    private static string LoadMessage(Options args)
    {
        var alternatives = new[] { args.Get("text"), args.Get("file"), args.Has("stdin") ? "<stdin>" : null }
            .Count(v => v is not null);
        if (alternatives != 1)
            throw new ArgumentException("Informe exatamente uma fonte: --text, --file ou --stdin.");
        if (args.Get("text") is { } text) return text;
        if (args.Get("file") is { } file) return File.ReadAllText(file);
        return Console.In.ReadToEnd();
    }

    private static string Help => ConsoleShell.HelpText;
}

internal static class Output
{
    public static void Print(object result) => Console.WriteLine(JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
    public long? GetLong(string key)
    {
        var value = Get(key);
        if (value is null) return null;
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException("--" + key + " exige um inteiro positivo.");
        return parsed;
    }
    public static Options Parse(string[] args)
    {
        var parsed = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
                throw new ArgumentException("Parâmetro inesperado: " + arg);
            var name = arg[2..];
            if (parsed._values.ContainsKey(name))
                throw new ArgumentException("Parâmetro duplicado: " + arg);
            if (name == "stdin") { parsed._values[name] = null; continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Falta o valor de: " + arg);
            parsed._values[name] = args[++i];
        }
        return parsed;
    }
}

internal static class SelfTest
{
    public static void Run()
    {
        Check(UrlIdentity.Exact("https://chatgpt.com/c/Ab?x=1", "https://chatgpt.com/c/Ab?x=1"));
        Check(UrlIdentity.Exact("chatgpt.com/c/Ab", "https://chatgpt.com/c/Ab"));
        Check(!UrlIdentity.Exact("https://chatgpt.com/c/Ab", "https://chatgpt.com/c/ABC"));
        Check(!UrlIdentity.Exact("https://chatgpt.com/c/Ab?x=1", "https://chatgpt.com/c/Ab?x=2"));
        Check(!UrlIdentity.Exact("https://chatgpt.com/c/Ab/extra", "https://chatgpt.com/c/Ab"));
        Check(!UrlIdentity.TryNormalize("javascript:alert(1)", out _));
        Check(Options.Parse(["--mode", "new-tab"]).Get("mode") == "new-tab");
        Check(Options.Parse(["--window-hwnd", "918794"]).GetLong("window-hwnd") == 918794);
        Check(ConsoleShell.ParseMode("Mode.NewWindow") == Mode.NewWindow);
        // Nome externo da interface: somente evidência de guia + página,
        // nunca posição em "Recentes" nem título da janela inteira do Edge.
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "Controlar UIA com C# 2 - ChatGPT") == "Controlar UIA com C# 2");
        // Evidência real Edge 157: o TabItem inclui uso de memória,
        // mas pertence à URL/RuntimeId pinados do chat, não ao processo.
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "Continuidade de sistemas - Uso de memória - 597 MB") ==
            "Continuidade de sistemas");
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "Continuidade de sistemas - Uso de memória - 1,5 GB") ==
            "Continuidade de sistemas");
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "Continue systems - Memory usage - 597 MB") ==
            "Continue systems");
        Check(ChatFullNameInspector.NormalizeTabTitle(
            " - Uso de memória - 597 MB") is null);
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "  Continuidade de sistemas  ") == "Continuidade de sistemas");
        Check(ChatFullNameInspector.NormalizeTabTitle(
            "Continuidade de sistemas - Uso de memória - 597 MB - Extra") ==
            "Continuidade de sistemas - Uso de memória - 597 MB - Extra");
        Check(WebInterface.FullNameSourceFromPreview("AUTO_DETECTED") ==
            "UIA_TAB_AND_PAGE");
        Check(WebInterface.FullNameSourceFromPreview("AUTO_DETECTED_TAB_ONLY") ==
            "UIA_TAB_VERIFIED");
        Check(WebInterface.FullNameSourceFromPreview("UIA_TITLE_UNCONFIRMED") is null);
        Check(ChatFullNameInspector.NormalizeTabTitle("ChatGPT") is null);
        Check(ChatFullNameInspector.NormalizeTabTitle("Novo chat") is null);
        Check(ChatFullNameInspector.NormalizeTabTitle("  ") is null);
        Check(ChatFullNameInspector.IsConfirmedTitle(
            "Controlar UIA com C# 2",
            ["Controlar UIA com C# 2", "Continuidade de sistemas"]));
        Check(!ChatFullNameInspector.IsConfirmedTitle(
            "Controlar UIA com C# 2", ["Outra conversa", "ChatGPT"]));
        Check(ChatRegistry.NormalizeFullName("  Título de teste  ") ==
            "Título de teste");
        var invalidFullNameRejected = false;
        try { _ = ChatRegistry.NormalizeFullName("linha 1\nlinha 2"); }
        catch (ArgumentException) { invalidFullNameRejected = true; }
        Check(invalidFullNameRejected);
        Check(new ProjectChatEntry("test", null, "https://chatgpt.com/c/x",
            "REGISTERED", "PRO_0001", "Título externo").FullName ==
            "Título externo");
        Check(Project.DefaultIdPrefix == "PRO_");
        Check(Chat.DefaultIdPrefix == "GPTBOT_");
        // Cabeçalho obrigatório em todo rascunho e envio gerado pelo programa.
        // Instantes em UTC não dependem do fuso horário do Windows.
        var referenceUtc = new DateTimeOffset(2026, 11, 18, 3, 32, 0, TimeSpan.Zero);
        var textStamped = MessageTimestamp.Apply("Teste operacional", referenceUtc);
        Check(textStamped == "[18/11/2026-00:32]\\nTeste operacional".Replace("\\n", "\n"));
        Check(MessageTimestamp.HasPrefix(textStamped));
        Check(MessageTimestamp.Apply(textStamped, referenceUtc.AddMinutes(15)) == textStamped);
        Check(MessageTimestamp.Apply(string.Empty, referenceUtc) == string.Empty);
        Check(MessageTimestamp.Apply("Duas\\nlinhas".Replace("\\n", "\n"), referenceUtc) ==
            "[18/11/2026-00:32]\\nDuas\\nlinhas".Replace("\\n", "\n"));
        Check(MessageTimestamp.Apply("Teste", referenceUtc.AddMinutes(-42)) ==
            "[17/11/2026-23:50]\\nTeste".Replace("\\n", "\n"));
        Check(MessageTimestamp.Apply("Teste operacional", new DateTimeOffset(
            2026, 11, 18, 5, 32, 0, TimeSpan.FromHours(2))) == textStamped);
        Check(!MessageTimestamp.HasPrefix("[99/99/2026-00:32] texto"));
        Check(MessageTimestamp.Apply("[99/99/2026-00:32] texto", referenceUtc)
            .StartsWith("[18/11/2026-00:32]"));
        Check(MessageTimestamp.HasPrefix("[18/11/2026-00:32] texto"));
        Check(MessageTimestamp.Apply("   ", referenceUtc) == "   ");
        Check(!MessageTimestamp.HasMessageBody("   "));
        Check(!MessageTimestamp.HasMessageBody("[18/11/2026-00:32]\\n".Replace("\\n", "\n")));
        Check(!MessageTimestamp.HasMessageBody("[18/11/2026-00:32] texto".Replace("texto", "  ")));
        Check(MessageTimestamp.HasMessageBody(textStamped));

        Check(Chat.InitialStandaloneSequence == 9);
        Check(ConsoleShell.FormatPrompt(null, null, null) == "None[-|None]> ");
        Check(ConsoleShell.FormatPrompt(null, "chat00", null) ==
            "None[chat00|None]> ");
        Check(ConsoleShell.FormatPrompt(
            "RM0 — MA GPTBOT Base Capabilities", "GPTBOT_0001", "PRO_0000") ==
            "RM0 — MA GPTBOT Base Capabilities[GPTBOT_0001|PRO_0000]> ");
        Check(ConsoleShell.FormatPrompt(
            "RM0 — MA GPTBOT Base Capabilities", null, "PRO_0000") ==
            "RM0 — MA GPTBOT Base Capabilities[-|PRO_0000]> ");
        Check(ConsoleShell.FormatPrompt(
            "autonomous-cycle-test", "PRO_0001-GPTBOT_0003",
            "PRO_0001", "Continuidade de sistemas") ==
            "autonomous-cycle-test[GPTBOT_0003 - Continuidade de sistemas|PRO_0001]> ");
        Check(ConsoleShell.FormatPrompt(
            "autonomous-cycle-test", "PRO_0001-GPTBOT_0003",
            "PRO_0001", null) ==
            "autonomous-cycle-test[GPTBOT_0003|PRO_0001]> ");
        // Abreviar só quando o mesmo projeto aparece como sufixo;
        // preservar identificador inteiro em projetos diferentes.
        Check(ConsoleShell.FormatPrompt(
            "Outro Projeto", "PRO_0001-GPTBOT_0003",
            "PRO_0002", "Continuidade de sistemas") ==
            "Outro Projeto[PRO_0001-GPTBOT_0003 - Continuidade de sistemas|PRO_0002]> ");
        Check(ConsoleShell.FormatPrompt(
            null, "PRO_0001-GPTBOT_0003", null,
            "Continuidade de sistemas") ==
            "None[PRO_0001-GPTBOT_0003 - Continuidade de sistemas|None]> ");
        Check(ConsoleShell.FormatPrompt(
            "autonomous-cycle-test", "GPTBOT_0009",
            "PRO_0001", null) ==
            "autonomous-cycle-test[GPTBOT_0009|PRO_0001]> ");
        Check(ConsoleShell.FormatPrompt(
            "autonomous-cycle-test", null, "PRO_0001",
            "Título antigo") ==
            "autonomous-cycle-test[-|PRO_0001]> ");

        // No fluxo de criação, URL /c/ definitiva não basta: o
        // registro aguarda o fim observável da geração na mesma guia.
        Check(!WebInterface.ResponseIdleConfirmed(false, 1, 60000));
        Check(!WebInterface.ResponseIdleConfirmed(false, 7, 60000));
        Check(!WebInterface.ResponseIdleConfirmed(false, 8, 1999));
        Check(WebInterface.ResponseIdleConfirmed(false, 8, 2000));
        Check(!WebInterface.ResponseIdleConfirmed(true, 0, 60000));
        Check(!WebInterface.ResponseIdleConfirmed(true, 1, 60000));
        Check(WebInterface.ResponseIdleConfirmed(true, 2, 500));
        Check(WebInterface.IsTransientUiaCom(new COMException(
            "UIA event failure", unchecked((int)0x80040201))));
        Check(!WebInterface.IsTransientUiaCom(new COMException(
            "Access denied", unchecked((int)0x80070005))));
        Check(ConsoleShell.ParseNewChatArguments("").Title is null);
        Check(ConsoleShell.ParseNewChatArguments("Project").ProjectToken == "Project");
        Check(ConsoleShell.ParseNewChatArguments("PRO_0000").ProjectToken == "PRO_0000");
        Check(ConsoleShell.ParseNewChatArguments("\"Meu chat\"").Title == "Meu chat");
        var specifiedProject = ConsoleShell.ParseNewChatArguments("\"Pesquisa\", Project");
        Check(specifiedProject.Title == "Pesquisa" && specifiedProject.ProjectToken == "Project");
        var specifiedId = ConsoleShell.ParseNewChatArguments("\"Pesquisa\", \"PRO_0000\"");
        Check(specifiedId.Title == "Pesquisa" && specifiedId.ProjectToken == "PRO_0000");
        var projectChat = "https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities/c/1d7a0124-91da-6bbe-d5e3-fec6ec8936cc";
        var projectPrefix = ChatUrlScope.ProjectPrefix(projectChat);
        Check(projectPrefix == "https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities");
        var emptyProjectPage = "https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260/project";
        var emptyProjectRoot = "https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260";
        // Nome do projeto vem da UIA (placeholder), nunca do slug.
        Check(ChatFeatures.MatchesComposerIdentity(
            "Novo chat em autonomous-cycle-test", ""));
        Check(ChatFeatures.MatchesComposerIdentity(
            "New chat in Sample Project", ""));
        Check(!ChatFeatures.MatchesComposerIdentity("Novo chat", ""));
        Check(ChatFeatures.IsKnownPlaceholder("Novo chat em autonomous-cycle-test"));
        // Caso real Edge 157: Document:ChatGPT + Edit sem nome/id,
        // depois de navegar da primeira conversa registrada para /project.
        Check(ChatFeatures.IsSupportedComposerPageUrl(emptyProjectPage));
        Check(ChatFeatures.IsSupportedComposerPageUrl(
            "https://chatgpt.com/c/402d246a-4d67-7bee-b816-52724781e3f4"));
        Check(ChatFeatures.IsSupportedComposerPageUrl(
            emptyProjectRoot + "-autonomous-cycle-test/c/" +
            "402d246a-4d67-7bee-b816-52724781e3f4"));
        Check(!ChatFeatures.IsSupportedComposerPageUrl(
            emptyProjectRoot + "-autonomous-cycle-test/c/local-chatgpt%3A12345678"));
        Check(!ChatFeatures.IsSupportedComposerPageUrl("https://example.com/c/12345678"));
        Check(!ChatFeatures.IsSupportedComposerPageUrl("https://chatgpt.com/"));
        Check(!ChatFeatures.IsSupportedComposerPageUrl(
            emptyProjectRoot + "/settings"));
        Check(ChatFeatures.CanUseUnnamedProjectComposer(true, 1, 1, "", ""));
        Check(ChatFeatures.CanUseUnnamedProjectComposer(true, 1, 1, null, null));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(false, 1, 1, "", ""));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(true, 0, 1, "", ""));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(true, 2, 1, "", ""));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(true, 1, 2, "", ""));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(true, 1, 1,
            "Barra de pesquisa e endereços", "view_1017"));
        Check(!ChatFeatures.CanUseUnnamedProjectComposer(true, 1, 1,
            "", "prompt-alternativo"));
        // Edge 157, segunda falha real (18/11): o editor aparece como
        // Edit:# irmão de Document:ChatGPT, fora da árvore do documento,
        // e é acompanhado apenas da barra de endereço do Edge.
        Check(ChatFeatures.IsBrowserAddressBar(
            "Barra de pesquisa e endereços", "view_1017"));
        Check(ChatFeatures.IsBrowserAddressBar(
            "Search and address bar", null));
        Check(!ChatFeatures.IsBrowserAddressBar("", ""));
        Check(!ChatFeatures.IsBrowserAddressBar("Pesquisar conversas", ""));
        Check(ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 1, "", ""));
        Check(ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 1, null, null));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            false, 1, 2, 1, 1, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 0, 2, 1, 1, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 2, 2, 1, 1, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 3, 1, 1, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 2, 0, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 0, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 2, "", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 1, "Barra de pesquisa e endereços", ""));
        Check(!ChatFeatures.CanUseDetachedUnnamedComposer(
            true, 1, 2, 1, 1, "", "view_1017"));
        // O Send único pode estar fora do Document no mesmo layout UIA.
        Check(ChatFeatures.CanUseDetachedSendButton(true, 1, 1));
        Check(!ChatFeatures.CanUseDetachedSendButton(false, 1, 1));
        Check(!ChatFeatures.CanUseDetachedSendButton(true, 0, 1));
        Check(!ChatFeatures.CanUseDetachedSendButton(true, 2, 1));
        Check(!ChatFeatures.CanUseDetachedSendButton(true, 1, 0));
        Check(!ChatFeatures.CanUseDetachedSendButton(true, 1, 2));
        Check(ConsoleCompletions.Find("web.createProjectC", ["chat00"])
            .Any(x => x.Text == "web.createProjectChat(\"\",\"\")"));
        var projectCreateArgs = ConsoleShell.ParseProjectChatArguments(
            "\"PRO_0001\",\"Mensagem inicial\",\"Título\"");
        Check(projectCreateArgs.Length == 3 &&
            projectCreateArgs[0] == "PRO_0001" &&
            projectCreateArgs[1] == "Mensagem inicial" &&
            projectCreateArgs[2] == "Título");
        var missingMessageRejected = false;
        try { _ = ConsoleShell.ParseProjectChatArguments("\"PRO_0001\",\"\""); }
        catch (ArgumentException) { missingMessageRejected = true; }
        Check(missingMessageRejected);
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Novo chat em autonomous-cycle-test") == "autonomous-cycle-test");
        Check(ProjectNameInspector.NameFromComposeLabel(
            "New chat in Sample Project") == "Sample Project");
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Nova conversa em Projeto de Testes") == "Projeto de Testes");
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Novo chat") is null);
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Novo chat em   ") is null);
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Novo chat em Projeto\nOutro") is null);
        Check(ProjectNameInspector.NameFromComposeLabel(
            "Novo chat em " + new string('x', 201)) is null);
        var namePreview = new ProjectNamePreview("PREVIEW_ONLY",
            "autonomous-cycle-test", emptyProjectRoot, emptyProjectPage,
            "Novo chat em autonomous-cycle-test", true, true,
            "PLACEHOLDER_AND_HEADING", 1180792, "runtime.project");
        Check(ProjectNameInspector.SamePreviewIdentity(namePreview, namePreview));
        Check(!ProjectNameInspector.SamePreviewIdentity(namePreview,
            namePreview with { FullName = "Outro Projeto" }));
        Check(!ProjectNameInspector.SamePreviewIdentity(namePreview,
            namePreview with { TabRuntimeId = "runtime.other" }));
        Check(!ProjectNameInspector.SamePreviewIdentity(namePreview,
            namePreview with { ObservedUrl = emptyProjectRoot }));
        Check(!ProjectNameInspector.SamePreviewIdentity(namePreview,
            namePreview with { PlaceholderText = "Novo chat em Outro Projeto" }));
        Check(ProjectNameInspector.IsProjectPage(emptyProjectPage));
        Check(ProjectNameInspector.IsProjectPage(emptyProjectPage + "/"));
        Check(!ProjectNameInspector.IsProjectPage(emptyProjectRoot));
        Check(!ProjectNameInspector.IsProjectPage(
            "https://chatgpt.com/g/g-p-other/project/test"));
        Check(!ProjectNameInspector.IsProjectPage(
            "https://evil.example/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260/project"));
        Check(ConsoleCompletions.Find("web.previewProjectFromE", ["chat00"])
            .Single().Text == "web.previewProjectFromEdge()");
        Check(ConsoleCompletions.Find("web.addProjectFromE", ["chat00"])
            .Single().Text == "web.addProjectFromEdge()");
        Check(ChatUrlScope.ProjectPrefix(emptyProjectPage) == emptyProjectRoot);
        Check(ChatUrlScope.ProjectPrefix(emptyProjectRoot) == emptyProjectRoot);
        Check(ProjectNewChatInspector.IsProjectUrl(emptyProjectPage, emptyProjectRoot));
        Check(ChatUrlScope.ProjectPage(emptyProjectRoot) == emptyProjectPage);
        Check(NewChatLauncher.InExpectedScope(emptyProjectPage, emptyProjectRoot));
        // Projeto consultado explicitamente não exige USE: uma única
        // /project em qualquer guia pode ser recuperada pelo RuntimeId.
        // Varredura incompleta, duplicata ou falta do pin sempre bloqueia.
        var projectTab = new TabMatch(1180792, 2, emptyProjectPage,
            "Edge", "runtime.project.single");
        Check(WebInterface.CanRecoverUniqueProjectComposeScan(
            new ScanReport([projectTab], [])));
        Check(!WebInterface.CanRecoverUniqueProjectComposeScan(
            new ScanReport([], [])));
        Check(!WebInterface.CanRecoverUniqueProjectComposeScan(
            new ScanReport([projectTab, projectTab with { TabRuntimeId = "runtime.other" }], [])));
        Check(!WebInterface.CanRecoverUniqueProjectComposeScan(
            new ScanReport([projectTab], ["restauração UIA não comprovada"])));
        Check(!WebInterface.CanRecoverUniqueProjectComposeScan(
            new ScanReport([projectTab with { TabRuntimeId = null }], [])));
        Check(NewChatLauncher.IsProjectComposePage(emptyProjectPage, emptyProjectRoot));
        Check(NewChatLauncher.IsProjectComposePage(emptyProjectRoot, emptyProjectRoot));
        Check(!NewChatLauncher.IsProjectComposePage(
            emptyProjectRoot + "/c/12345678", emptyProjectRoot));
        Check(!NewChatLauncher.InExpectedScope(
            emptyProjectRoot + "/unrelated", emptyProjectRoot));
        Check(!ProjectNewChatInspector.IsProjectUrl(
            emptyProjectRoot + "/project/another", emptyProjectRoot));
        Check(!ProjectNewChatInspector.IsProjectUrl(
            "https://chatgpt.com/g/g-p-different/project", emptyProjectRoot));
        // E2E observado em 18/11: /project sem slug -> /c/ com
        // slug e ID local-chatgpt%3A temporário, depois UUID remoto.
        var projectWithSlug = emptyProjectRoot + "-autonomous-cycle-test";
        var provisionalChatUrl = projectWithSlug +
            "/c/local-chatgpt%3A1448ec50-9c6d-4f5f-a3e6-fc384140d045";
        var finalChatUrl = projectWithSlug +
            "/c/644b8448-7e2e-e669-26f2-2187c0093b9d";
        Check(ChatUrlScope.SameProjectSegment(
            "g-p-d3b62d37c2f0d39ccdbb171f4c929260",
            "g-p-d3b62d37c2f0d39ccdbb171f4c929260-autonomous-cycle-test"));
        Check(!ChatUrlScope.SameProjectSegment(
            "g-p-d3b62d37c2f0d39ccdbb171f4c929260",
            "g-p-d3b62d37c2f0d39ccdbb171f4c929260f-other"));
        Check(!ChatUrlScope.SameProjectSegment(
            "g-p-d3b62d37c2f0d39ccdbb171f4c929260",
            "g-p-70ac3d89809a068c9b6e53a6a5eaa804-autonomous-cycle-test"));
        Check(ChatUrlScope.IsProvisionalConversation(provisionalChatUrl, emptyProjectRoot));
        Check(!ChatUrlScope.IsPersistentConversation(provisionalChatUrl, emptyProjectRoot));
        Check(ChatUrlScope.IsPersistentConversation(finalChatUrl, emptyProjectRoot));
        Check(ChatUrlScope.IsPersistentConversation(
            finalChatUrl.Replace("/c/", "/uc/"), emptyProjectRoot));
        Check(!ChatUrlScope.IsPersistentConversation(
            finalChatUrl, "https://chatgpt.com/g/g-p-70ac3d89809a068c9b6e53a6a5eaa804"));
        Check(!ChatUrlScope.IsPersistentConversation(finalChatUrl + "/extra",
            emptyProjectRoot));
        Check(!ChatUrlScope.IsPersistentConversation(
            finalChatUrl.Replace("chatgpt.com", "example.net"), emptyProjectRoot));
        Check(!ChatUrlScope.IsPersistentConversation(finalChatUrl + "?query=1",
            emptyProjectRoot));
        Check(ConsoleCompletions.Find("web.reconcileProjectC", ["chat00"])
            .Single().Text == "web.reconcileProjectChat(\"\")");
        Check(ChatUrlScope.IsPersistentConversation(projectChat, projectPrefix));
        Check(!ChatUrlScope.IsPersistentConversation(projectPrefix, projectPrefix));
        Check(!ChatUrlScope.IsPersistentConversation("https://chatgpt.com/c/abcdefgh", projectPrefix));
        Check(ChatUrlScope.IsPersistentConversation("https://chatgpt.com/c/abcdefgh", null));
        Check(!ChatUrlScope.IsPersistentConversation("https://evil.example/c/abcdefgh", null));
        // Leitura de ações de nova conversa só aceita URL do projeto ou
        // conversa persistente dentro dele; jamais uma guia de outro projeto.
        Check(ProjectNewChatInspector.IsProjectUrl(projectPrefix, projectPrefix));
        Check(ProjectNewChatInspector.IsProjectUrl(projectChat, projectPrefix));
        Check(!ProjectNewChatInspector.IsProjectUrl("https://chatgpt.com/", projectPrefix));
        Check(!ProjectNewChatInspector.IsProjectUrl(
            "https://chatgpt.com/g/g-p-another-project", projectPrefix));
        Check(!ProjectNewChatInspector.IsProjectUrl(
            "https://chatgpt.com/g/g-p-another-project/c/abcdefgh", projectPrefix));
        Check(ProjectNewChatInspector.IsRelevantControl("Novo chat", "", ""));
        Check(ProjectNewChatInspector.IsRelevantControl(
            "", "new-chat-in-project", ""));
        Check(ProjectNewChatInspector.IsRelevantControl(
            "", "", "Start a conversation"));
        Check(!ProjectNewChatInspector.IsRelevantControl("Configurações", "", ""));
        Check(ProjectNewChatInspector.ActionPriority("Novo chat", "", "") == 0);
        Check(ProjectNewChatInspector.ActionPriority("", "new-chat", "") == 0);
        Check(ProjectNewChatInspector.ActionPriority("Conversa anterior", "", "") == 1);
        Check(ProjectNewChatInspector.ActionPriority("Project settings", "", "") == 2);
        Check(ConsoleCompletions.Find("web.inspectProjectNewC", ["chat00"])
            .Single().Text == "web.inspectProjectNewChat()");
        Check(ConsoleCompletions.Find("inspectProjectNewC", ["chat00"])
            .Single().Text == "inspectProjectNewChat()");
        Check(NewChatLauncher.CanInvokeProjectAction(true, true));
        Check(!NewChatLauncher.CanInvokeProjectAction(false, true)); // Outra janela ativa.
        Check(!NewChatLauncher.CanInvokeProjectAction(true, false)); // Outra guia ativa.
        Check(!NewChatLauncher.CanInvokeProjectAction(false, false));
        Check(NewChatLauncher.ShouldPauseForSelectedTabChange(false));
        Check(!NewChatLauncher.ShouldPauseForSelectedTabChange(true));
        var pausedFocus = NewChatLauncher.FocusPaused(
            new SelectedTabState(1180792, 2, "runtime.new.pinned"),
            "https://chatgpt.com/g/g-p-project");
        Check(pausedFocus.Status == "PAUSED_FOCUS_CHANGED");
        Check(pausedFocus.WindowHandle == 1180792);
        Check(pausedFocus.TabRuntimeId == "runtime.new.pinned");
        Check(pausedFocus.ObservedUrl == "https://chatgpt.com/g/g-p-project");
        Check(NewChatLauncher.IsProjectNewChatAction("New chat in project"));
        Check(NewChatLauncher.IsProjectNewChatAction("Novo chat neste projeto"));
        Check(!NewChatLauncher.IsProjectNewChatAction("New chat")); // sidebar ambígua
        Check(!NewChatLauncher.IsProjectNewChatAction("Novo chat"));
        Check(NewChatLauncher.InExpectedScope("https://chatgpt.com/", null));
        Check(NewChatLauncher.InExpectedScope(projectChat, projectPrefix));
        Check(!NewChatLauncher.InExpectedScope("https://chatgpt.com/c/abcdefgh", projectPrefix));
        Check(ConsoleCompletions.Find("use project P", ["chat00"], ["PRO_0000"])
            .Single().Text == "use project \"PRO_0000\"");
        Check(ConsoleCompletions.Find("web.addNewC", ["chat00"])
            .Any(v => v.Text == "web.addNewChat()"));
        Check(ConsoleCompletions.Find("web.resumeNewC", ["chat00"])
            .Single().Text == "web.resumeNewChat(\"\")");
        Check(ConsoleCompletions.Find("web.reopenClosedN", ["chat00"])
            .Single().Text == "web.reopenClosedNewChat(\"\")");
        Check(ConsoleCompletions.Find("web.previewNewC", ["chat00"])
            .Any(v => v.Text == "web.previewNewChat()"));
        Check(ConsoleCompletions.Find("web.sendFirstNewC", ["chat00"])
            .Single().Text == "web.sendFirstNewChatMessage(\"\",\"\")");
        Check(ConsoleCompletions.Find("web.addProjectFromC", ["GPTBOT_0001"])
            .Single().Text == "web.addProjectFromChat(\"\",\"\")");
        // Em layouts com AltGr, "/" pode ser Ctrl+Alt + caractere imprimível.
        Check(ConsoleLineEditor.ShouldInsertPrintable(
            new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, true, true)));
        Check(!ConsoleLineEditor.ShouldInsertPrintable(
            new ConsoleKeyInfo((char)22, ConsoleKey.V, false, false, true)));
        Check(ConsoleLineEditor.IsPasteShortcut(
            new ConsoleKeyInfo((char)22, ConsoleKey.V, false, false, true)));
        Check(ConsoleLineEditor.IsPasteShortcut(
            new ConsoleKeyInfo('\0', ConsoleKey.Insert, true, false, false)));
        Check(ConsoleLineEditor.FlattenPaste(
            "https://chatgpt.com/g/g-p-example") ==
            "https://chatgpt.com/g/g-p-example");
        Check(ConsoleLineEditor.FlattenPaste("a\r\nb") == "a b");
        Check(ChatLimitDetector.IsMaxLimitWarning(
            "Você chegou à duração máxima desta conversa, mas pode continuar conversando em um novo chat."));
        Check(ChatLimitDetector.IsMaxLimitWarning(
            "You've reached the maximum length for this conversation."));
        Check(!ChatLimitDetector.IsMaxLimitWarning("ChatGPT está gerando uma resposta."));
        Check(ChatLimitDetector.IsNewChatAction("Iniciar novo chat"));
        Check(ChatLimitDetector.IsNewChatAction("Start a new chat"));
        Check(!ChatLimitDetector.IsNewChatAction("Novo chat"));
        Check(!ChatLimitDetector.EvidenceConfirmed(true, false));
        Check(!ChatLimitDetector.EvidenceConfirmed(false, true));
        Check(ChatLimitDetector.EvidenceConfirmed(true, true));
        Check(ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 1, true, 1));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(false, 1, true, 1));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 0, true, 1));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 2, true, 1));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 1, false, 1));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 1, true, 0));
        Check(!ChatLimitDetector.CanConfirmDetachedMaxLimit(true, 1, true, 2));
        // O preflight ativo de sendMessage pode refocar a MESMA guia;
        // nunca usar esse retry para falhas genéricas ou após o clique.
        Check(Chat.IsPassiveLimitFocusError("BUSY_TAB_NOT_ACTIVE: guia fixada está em segundo plano"));
        Check(!Chat.IsPassiveLimitFocusError("BUSY_TARGET_CHANGED: guia divergiu"));
        Check(!Chat.IsPassiveLimitFocusError("CHAT_AT_MAX_LIMIT"));
        Check(!Chat.IsPassiveLimitFocusError(null));
        // isBusy não pode devolver false se o único Stop válido
        // estiver fora do Document no layout UIA achatado do Edge.
        Check(ChatFeatures.CanUseDetachedBusyStop(true, 1, 1));
        Check(!ChatFeatures.CanUseDetachedBusyStop(false, 1, 1));
        Check(!ChatFeatures.CanUseDetachedBusyStop(true, 0, 1));
        Check(!ChatFeatures.CanUseDetachedBusyStop(true, 2, 1));
        Check(!ChatFeatures.CanUseDetachedBusyStop(true, 1, 2));
        Check(!ChatFeatures.CanUseDetachedBusyStop(true, 1, 0));
        Check(ChatFeatures.IsStopControl("Stop generating", null));
        Check(ChatFeatures.IsStopControl("Parar de gerar", null));
        Check(ChatFeatures.IsStopControl(null, "stop-button"));
        Check(!ChatFeatures.IsStopControl("Stop loading", null));
        Check(!ChatFeatures.IsStopControl("Send", "send-button"));
        var url = "https://chatgpt.com/c/duplicate";
        var first = new TabMatch(100, 0, url, "Edge", "runtime.a");
        var second = new TabMatch(100, 1, url, "Edge", "runtime.b");
        var third = new TabMatch(200, 0, url, "Edge", "runtime.c");
        var exactPin = new ChatBinding(url, DateTimeOffset.UtcNow, "test", 100, null, "runtime.b");
        Check(EdgeBrowser.ChooseExactMatch([first, second, third], exactPin, 0, null) == second);
        var legacyPin = new ChatBinding(url, DateTimeOffset.UtcNow, "test", 100);
        Check(EdgeBrowser.ChooseExactMatch([first, second, third], legacyPin, 200, third) == third);
        var blockedAmbiguous = false;
        try { EdgeBrowser.ChooseExactMatch([first, second, third], legacyPin, 0, null); }
        catch (InvalidOperationException ex) { blockedAmbiguous = ex.Message.Contains("AMBIGUOUS_CHAT_TABS"); }
        Check(blockedAmbiguous);
        Check(EdgeBrowser.PinnedTabIndex(["tab.a", "tab.b", "tab.c"], "tab.b") == 1);
        // Regressão de navegação: a seleção pode voltar à guia antiga
        // após Ctrl+T, mas o pin novo continua a ser alvo determinístico.
        Check(EdgeBrowser.PinnedTabIndex(
            ["runtime.old.selected", "runtime.new.pinned"], "runtime.new.pinned") == 1);
        Check(EdgeBrowser.PinnedTabIndex(["tab.a", "tab.b", "tab.c"], "tab.a") == 0);
        var missingPinnedTabBlocked = false;
        try { EdgeBrowser.PinnedTabIndex(["tab.a", "tab.b"], "tab.closed"); }
        catch (InvalidOperationException ex) { missingPinnedTabBlocked = ex.Message.Contains("PINNED_TAB_NOT_FOUND"); }
        Check(missingPinnedTabBlocked);
        var duplicateRuntimeIdBlocked = false;
        try { EdgeBrowser.PinnedTabIndex(["tab.a", "tab.a"], "tab.a"); }
        catch (InvalidOperationException ex) { duplicateRuntimeIdBlocked = ex.Message.Contains("AMBIGUOUS_TAB_RUNTIME_ID"); }
        Check(duplicateRuntimeIdBlocked);
        var emptyRuntimeIdBlocked = false;
        try { EdgeBrowser.PinnedTabIndex(["tab.a"], ""); }
        catch (InvalidOperationException ex) { emptyRuntimeIdBlocked = ex.Message.Contains("TAB_IDENTITY_UNAVAILABLE"); }
        Check(emptyRuntimeIdBlocked);
        Check(ChatFeatures.SameTabIdentity(first, first));
        Check(!ChatFeatures.SameTabIdentity(first, second)); // Mesmo HWND e URL, guia diferente.
        Check(!ChatFeatures.SameTabIdentity(first, third));  // Outra janela da mesma URL.
        Check(!ChatFeatures.SameTabIdentity(first,
            new TabMatch(100, 0, url, "Edge", "runtime.changed"))); // Guia substituída.
        Check(EdgeBrowser.ChooseExactMatch([first, second], exactPin, 0, null) == second);
        Check(EdgeBrowser.ChooseExactMatch([first], exactPin, 0, null) == first); // Pin expirado, única candidata.
        // Pós-clique deve distinguir tentativa de recebimento real.
        // Nunca converter rascunho preservado em êxito confirmado.
        Check(ChatFeatures.ClassifySendReadback("texto", "texto") ==
            "SEND_DRAFT_RETAINED_UNCONFIRMED");
        Check(ChatFeatures.ClassifySendReadback("texto\r\n", "texto") ==
            "SEND_DRAFT_RETAINED_UNCONFIRMED");
        Check(ChatFeatures.ClassifySendReadback("", "texto") ==
            "SEND_EDITOR_CLEARED_SERVER_UNCONFIRMED");
        Check(ChatFeatures.ClassifySendReadback("Pergunte ao ChatGPT", "texto") ==
            "SEND_EDITOR_CLEARED_SERVER_UNCONFIRMED");
        Check(ChatFeatures.ClassifySendReadback("outro texto", "texto") ==
            "SEND_EDITOR_CHANGED_UNCONFIRMED");
        Check(ChatFeatures.ClassifySendReadback(null, "texto") ==
            "SEND_OBSERVATION_UNAVAILABLE");
        Check(ChatFeatures.TextEquivalent("abc\r\n", "abc"));
        Check(ChatFeatures.TextEquivalent("abc\r", "abc"));
        Check(ChatFeatures.TextEquivalent("", ""));
        Check(!ChatFeatures.TextEquivalent("abc", "abcd"));
        // Autocomplete do console (sem depender do terminal/UIA):
        var partial = ConsoleCompletions.Find("setT", ["chat00", "GPTBOT_0001"]);
        Check(partial.Count == 2); // "setT" corresponde a setText e settings (case-insensitive).
        Check(partial[0].Text == "setText(\"\")"); // Primeira opção em ordem alfabética.
        Check(partial[1].Text == "settings()");
        Check(partial[0].Cursor == "setText(\"".Length);
        var byName = ConsoleCompletions.Find("chat00.setT", ["chat00", "GPTBOT_0001"]);
        Check(byName.Count == 1 && byName[0].Text == "chat00.setText(\"\")");
        Check(byName[0].Cursor == "chat00.setText(\"".Length);
        var useWithQuote = ConsoleCompletions.Find("use \"G", ["chat00", "GPTBOT_0001"]);
        Check(useWithQuote.Count == 1 && useWithQuote[0].Text == "use \"GPTBOT_0001\"");
        var useWithoutQuote = ConsoleCompletions.Find("use G", ["chat00", "GPTBOT_0001"]);
        Check(useWithoutQuote.Count == 1 && useWithoutQuote[0].Text == "use \"GPTBOT_0001\"");
        Check(ConsoleCompletions.Find("use G", ["chat00"]).Count == 0);
        Check(ConsoleCompletions.Find("setText(\"texto", ["chat00"]).Count == 0);
        Check(ConsoleCompletions.Find("setText(", ["chat00"]).Single().Text == "setText(\"\")");
        Check(ConsoleCompletions.Find("chat00.setText(", ["chat00"]).Single().Text == "chat00.setText(\"\")");
        Check(ConsoleCompletions.Find("chat00.", ["chat00"]).Count > 5);
        var openChoices = ConsoleCompletions.Find("open", ["chat00"]);
        Check(openChoices.Count == 2);
        Check(openChoices[0].Text == "open(Mode.NewTab)");
        Check(openChoices[1].Text == "open(Mode.NewWindow)");
        var cycle = new CompletionCycle();
        Check(cycle.Next("open", ["chat00"])?.Text == "open(Mode.NewTab)");
        Check(cycle.Next("open(Mode.NewTab)", ["chat00"])?.Text == "open(Mode.NewWindow)");
        Check(cycle.Next("open(Mode.NewWindow)", ["chat00"])?.Text == "open(Mode.NewTab)");
        Check(cycle.Next("open(Mode.NewTab)", ["chat00"], backwards: true)?.Text == "open(Mode.NewWindow)");
        cycle.Reset();
        Check(cycle.Next("setT", ["chat00"])?.Text == "setText(\"\")");
        Check(ConsoleCompletions.Find("setFull", ["chat00"]).Single().Text ==
            "setFullName(\"\")");
        Check(ConsoleCompletions.Find("chat00.setFull", ["chat00"]).Single().Text ==
            "chat00.setFullName(\"\")");
        Check(ConsoleCompletions.Find("inspectFull", ["chat00"]).Single().Text ==
            "inspectFullName()");
        Check(ConsoleCompletions.Find("captureFull", ["chat00"]).Single().Text ==
            "captureFullName()");
        Check(ConsoleCompletions.Find("web.addC", ["chat00"]).Single().Text == "web.addChat(\"\",\"\")");
        Check(ConsoleCompletions.Find("web.captureC", ["chat00"]).Single().Text == "web.captureChat(\"\")");
        Check(ConsoleCompletions.Find("bindToChat", ["chat00"]).Count == 0);
        Check(ConsoleShell.StringArgument("\"Teste\"") == "Teste");
        Check(ConsoleShell.StringArguments("\"chat00\",\"https://chatgpt.com/c/Id\"", 2)[0] == "chat00");
        var temporary = Path.Combine(Path.GetTempPath(), "chat-automation-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var storage = Path.Combine(temporary, "chats.json");
        try
        {
            var settingsPath = Path.Combine(temporary, "settings.json");
            var persistent = new AutomationSettings(settingsPath);
            Check(!persistent.EnableSendHumanText);
            persistent.EnableSendHumanText = true;
            Check(new AutomationSettings(settingsPath).EnableSendHumanText);
            persistent.EnableSendHumanText = false;
            Check(!new AutomationSettings(settingsPath).EnableSendHumanText);
            Check(File.ReadAllText(settingsPath).Contains("enableSendHumanText"));
            Check(ChatFeatures.IsKnownPlaceholder("Pergunte ao ChatGPT\n"));
            Check(ChatFeatures.IsKnownPlaceholder("Ask anything"));
            Check(!ChatFeatures.IsKnownPlaceholder("texto humano"));
            Check(ChatFeatures.EditorTextOrEmpty("Pergunte ao ChatGPT\n") == "");
            Check(ChatFeatures.EditorTextOrEmpty("  Pergunte ao ChatGPT\r\n") == "");
            Check(ChatFeatures.EditorTextOrEmpty("Ask anything\n") == "");
            Check(ChatFeatures.EditorTextOrEmpty("\n") == "");
            Check(ChatFeatures.EditorTextOrEmpty("  ") == "");
            Check(ChatFeatures.EditorTextOrEmpty("mensagem digitada") == "mensagem digitada");
            Check(ChatFeatures.EditorTextOrEmpty("linha 1\nlinha 2\n") == "linha 1\nlinha 2\n");
            // Reutilizar uma guia para novo chat não pode deixar o pin
            // antigo apontando silenciosamente para outra conversa.
            var reuseStore = new ChatRegistry(
                Path.Combine(temporary, "tab-reuse-tests.json"), migrateLegacy: false);
            var oldChatUrl = "https://chatgpt.com/c/b0371e61-ca56-accb-6be4-b6e25e23aed0";
            var reuseSource = reuseStore.Upsert("original",
                new ChatBinding(oldChatUrl, DateTimeOffset.UtcNow,
                    "reuse-self-test", 59, null, "runtime.original"));
            Check(reuseSource.PreferredWindowHandle == 59);
            Check(reuseSource.PreferredTabRuntimeId == "runtime.original");
            var wrongReleaseRejected = false;
            try { reuseStore.ReleaseTabPin("original", oldChatUrl,
                59, "runtime.wrong"); }
            catch (InvalidOperationException ex)
            { wrongReleaseRejected = ex.Message.Contains("PROJECT_SOURCE_PIN_CHANGED"); }
            Check(wrongReleaseRejected);
            Check(reuseStore.Get("original").PreferredTabRuntimeId == "runtime.original");
            var detachedSource = reuseStore.ReleaseTabPin(
                "original", oldChatUrl, 59, "runtime.original");
            Check(detachedSource.Url == oldChatUrl &&
                detachedSource.PreferredWindowHandle is null &&
                detachedSource.PreferredTabRuntimeId is null);
            Check(reuseStore.ReleaseTabPin("original", oldChatUrl,
                59, "runtime.original").PreferredWindowHandle is null);
            Check(new ChatRegistry(Path.Combine(temporary, "tab-reuse-tests.json"),
                migrateLegacy: false).Get("original").Url == oldChatUrl);

            // Retomada após COM: já existe uma conversa definitiva na
            // guia reservada, mas FIRST_SEND_ATTEMPTED não foi persistido.
            // O registro é permitido apenas após URL/escopo/pin validados.
            var recoveryPath = Path.Combine(temporary, "recovered-projects.json");
            var recoveryChatPath = Path.Combine(temporary, "recovered-chats.json");
            var recoveryStore = new ProjectRegistry(recoveryPath, recoveryChatPath);
            var recoveryProject = recoveryStore.AddProject(
                "autonomous-cycle-test",
                "https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260");
            var recoveryPending = recoveryStore.Reserve("Teste automático V7.1",
                recoveryProject.Id);
            Check(recoveryPending.State == "RESERVED");
            Check(WebInterface.CanRecoverUntouchedReservedChat(recoveryPending));
            Check(!WebInterface.CanRecoverUntouchedReservedChat(
                recoveryPending with { State = "TAB_CREATED" }));
            Check(!WebInterface.CanRecoverUntouchedReservedChat(
                recoveryPending with { WindowHandle = 81 }));
            Check(!WebInterface.CanRecoverUntouchedReservedChat(
                recoveryPending with { TabRuntimeId = "runtime.old" }));
            Check(!WebInterface.CanRecoverUntouchedReservedChat(
                recoveryPending with { LastObservedUrl = recoveryProject.UrlPrefix }));
            Check(recoveryStore.MarkTabCreated(
                recoveryPending.Name, 81, "runtime.new-chat").State == "TAB_CREATED");
            var recoveredUrl = recoveryProject.UrlPrefix +
                "-autonomous-cycle-test/c/f1f619ba-7c05-9a3b-7c72-6463e42d8272";
            Check(ChatUrlScope.IsPersistentConversation(recoveredUrl,
                recoveryProject.UrlPrefix));
            Check(recoveryStore.MarkOpened(recoveryPending.Name,
                81, "runtime.new-chat", recoveredUrl).State == "OPENED_PENDING_URL");
            // Falha/cancelamento antes de salvar a URL NÃO pode remover
            // a única reserva capaz de recuperar a criação.
            var orphanCompletionBlocked = false;
            try { recoveryStore.MarkCompleted(recoveryPending.Name); }
            catch (InvalidOperationException ex)
            {
                orphanCompletionBlocked = ex.Message.Contains(
                    "NEW_CHAT_COMPLETION_NOT_PERSISTED");
            }
            Check(orphanCompletionBlocked);
            Check(recoveryStore.ListPending().Any(p =>
                p.Name == recoveryPending.Name &&
                p.State == "OPENED_PENDING_URL"));

            var recoveryRegistry = new ChatRegistry(
                recoveryChatPath, migrateLegacy: false);
            Check(recoveryRegistry.InsertNewChat(recoveryPending.Name,
                new ChatBinding(recoveredUrl, DateTimeOffset.UtcNow,
                    "recovered-uia", 81, null, "runtime.new-chat"),
                recoveryProject.Id, recoveryPending.DisplayTitle).Url == recoveredUrl);
            recoveryStore.MarkCompleted(recoveryPending.Name);
            recoveryStore.MarkCompleted(recoveryPending.Name); // Idempotência.
            Check(recoveryStore.ListPending().Count == 0);
            Check(new ChatRegistry(recoveryChatPath, migrateLegacy: false)
                .Get(recoveryPending.Name).ProjectId == recoveryProject.Id);

            // Um único contexto: chat do projeto implica PRO_xxxx no
            // prompt; consulta de outro projeto não muda seleção.
            var contextDir = Path.Combine(temporary, "context-selection");
            Directory.CreateDirectory(contextDir);
            using (var scopedWeb = new WebInterface(
                Path.Combine(contextDir, "chats.json"), migrateLegacy: false,
                settingsPath: Path.Combine(contextDir, "settings.json")))
            {
                var contextA = scopedWeb.AddProject("autonomous-cycle-test",
                    emptyProjectRoot);
                var contextB = scopedWeb.AddProject("RM0", projectPrefix);
                scopedWeb.AddChat("projectA",
                    emptyProjectRoot + "-autonomous-cycle-test/c/" +
                    "402d246a-4d67-7bee-b816-52724781e3f4");
                scopedWeb.AddChat("projectB", projectChat);
                scopedWeb.AddChat("standalone",
                    "https://chatgpt.com/c/48aca83d-e8ea-cc2b-bb44-e44c2a1869b8");
                Check(scopedWeb.CurrentProjectId == "None" &&
                    scopedWeb.CurrentChatName is null);
                scopedWeb.UseChat("projectA");
                Check(scopedWeb.CurrentProjectId == contextA.Id &&
                    scopedWeb.CurrentChatName == "projectA");
                Check(ConsoleShell.FormatPrompt(scopedWeb.CurrentProject?.FullName,
                    scopedWeb.CurrentChatName, scopedWeb.CurrentProjectId) ==
                    "autonomous-cycle-test[projectA|PRO_0000]> ");
                var beforeQueryProject = scopedWeb.CurrentProjectId;
                var savedOutputProject = Console.Out;
                try
                {
                    using var capturedProject = new StringWriter();
                    Console.SetOut(capturedProject);
                    ConsoleShell.Dispatch(scopedWeb,
                        "web.projectListChats(\"PRO_0001\")");
                    var listing = capturedProject.ToString();
                    Check(listing.Contains("\"queriedProject\": \"PRO_0001\"") &&
                        listing.Contains("\"selectedProject\": \"PRO_0000\""));
                }
                finally { Console.SetOut(savedOutputProject); }
                Check(scopedWeb.CurrentProjectId == beforeQueryProject &&
                    scopedWeb.CurrentChatName == "projectA");
                scopedWeb.UseProject(contextB.Id);
                Check(scopedWeb.CurrentProjectId == contextB.Id &&
                    scopedWeb.CurrentChatName is null);
                scopedWeb.UseChat("projectB");
                Check(scopedWeb.CurrentProjectId == contextB.Id &&
                    scopedWeb.CurrentChatName == "projectB");
                scopedWeb.Refresh();
                Check(scopedWeb.CurrentProjectId == contextB.Id &&
                    scopedWeb.CurrentChatName == "projectB");
                scopedWeb.UseProject("None");
                Check(scopedWeb.CurrentProjectId == "None" &&
                    scopedWeb.CurrentChatName is null);
                scopedWeb.UseChat("standalone");
                Check(scopedWeb.CurrentProjectId == "None" &&
                    scopedWeb.CurrentChatName == "standalone");
                scopedWeb.UseChat("projectA");
                Check(scopedWeb.CurrentProjectId == contextA.Id &&
                    scopedWeb.CurrentChatName == "projectA");

                // Importação externa inconsistente: ProjectId de A com
                // URL pertencente a B não pode ser usada nem listada
                // silenciosamente como chat registrado no projeto A.
                new ChatRegistry(Path.Combine(contextDir, "chats.json"),
                    migrateLegacy: false).Upsert("wrongProject",
                    new ChatBinding(projectPrefix +
                        "/c/d2f8d010-6003-6bfc-93bc-5352e65fc08a",
                        DateTimeOffset.UtcNow, "scope-negative"),
                    projectId: contextA.Id);
                scopedWeb.Refresh();
                var badUseBlocked = false;
                try { scopedWeb.UseChat("wrongProject"); }
                catch (InvalidOperationException ex)
                { badUseBlocked = ex.Message.Contains("CHAT_PROJECT_SCOPE_MISMATCH"); }
                Check(badUseBlocked);
                Check(scopedWeb.CurrentChatName == "projectA");
                var badListingBlocked = false;
                try { scopedWeb.ProjectListChats(contextA); }
                catch (InvalidOperationException ex)
                { badListingBlocked = ex.Message.Contains("PROJECT_LIST_SCOPE_MISMATCH"); }
                Check(badListingBlocked);
            }

            var registry = new ChatRegistry(storage, migrateLegacy: false);
            var record = registry.Upsert(null, new ChatBinding("https://chatgpt.com/c/Ab", DateTimeOffset.UtcNow, "self-test"));
            Check(record.Name == "chat00");
            Check(new ChatRegistry(storage, migrateLegacy: false).Get("chat00").Url == "https://chatgpt.com/c/Ab");
            Check(registry.Upsert(null, new ChatBinding("https://chatgpt.com/c/Ab", DateTimeOffset.UtcNow, "self-test")).Name == "chat00");
            Check(registry.Upsert(null, new ChatBinding("https://chatgpt.com/c/Bc", DateTimeOffset.UtcNow, "self-test")).Name == "chat01");
            Check(new ChatRegistry(storage, migrateLegacy: false).ListChats().Count == 2);
            Check(!registry.Get("chat01").IsAtMaxLimit);
            Check(registry.MarkMaxLimit("chat01", "https://chatgpt.com/c/Bc").IsAtMaxLimit);
            Check(new ChatRegistry(storage, migrateLegacy: false).Get("chat01").IsAtMaxLimit);
            Check(registry.Upsert("chat01", new ChatBinding(
                "https://chatgpt.com/c/Bc", DateTimeOffset.UtcNow, "test")).IsAtMaxLimit);
            Check(!registry.Upsert("chat01", new ChatBinding(
                "https://chatgpt.com/c/Changed", DateTimeOffset.UtcNow, "test")).IsAtMaxLimit);
            registry.Remove("chat00");
            Check(new ChatRegistry(storage, migrateLegacy: false).ListChats().Count == 1);
            var savedTitle = registry.SetFullName("chat01",
                "https://chatgpt.com/c/Changed",
                "  Minha conversa  ", "MANUAL");
            Check(savedTitle.FullName == "Minha conversa");
            Check(savedTitle.FullNameSource == "MANUAL");
            var uiTabTitle = registry.SetFullName("chat01",
                "https://chatgpt.com/c/Changed",
                "Título validado no TabItem", "UIA_TAB_VERIFIED");
            Check(uiTabTitle.FullName == "Título validado no TabItem");
            Check(uiTabTitle.FullNameSource == "UIA_TAB_VERIFIED");
            savedTitle = registry.SetFullName("chat01",
                "https://chatgpt.com/c/Changed", "Minha conversa", "MANUAL");

            Check(new ChatRegistry(storage, migrateLegacy: false)
                .Get("chat01").FullName == "Minha conversa");
            var metadataRebound = registry.Upsert("chat01", new ChatBinding(
                "https://chatgpt.com/c/Changed", DateTimeOffset.UtcNow,
                "rebind-test", 15, null, "runtime.fullname"));
            Check(metadataRebound.FullName == "Minha conversa");
            Check(metadataRebound.FullNameSource == "MANUAL");
            var wrongFullNameUrlBlocked = false;
            try { registry.SetFullName("chat01", "https://chatgpt.com/c/Wrong",
                "Outro"); }
            catch (InvalidOperationException ex)
            { wrongFullNameUrlBlocked = ex.Message.Contains("CHAT_FULL_NAME_URL_CHANGED"); }
            Check(wrongFullNameUrlBlocked);
            using var web = new WebInterface(storage, migrateLegacy: false, settingsPath: settingsPath);
            // Testar projetos em catálogo isolado; não alterar as contagens
            // verificadas pelos testes históricos de chats.
            var projectTestDir = Path.Combine(temporary, "projects-selftest");
            Directory.CreateDirectory(projectTestDir);
            var projectStorage = Path.Combine(projectTestDir, "chats.json");
            using (var projectWeb = new WebInterface(projectStorage,
                migrateLegacy: false, settingsPath: settingsPath))
            {
                Check(projectWeb.CurrentProjectId == "None");
                Check(projectWeb.ListProjects().Count == 0);
                var noPreviewApprovalBlocked = false;
                try { projectWeb.AddProjectFromEdge(); }
                catch (InvalidOperationException ex)
                {
                    noPreviewApprovalBlocked = ex.Message.Contains("PROJECT_PREVIEW_REQUIRED");
                }
                Check(noPreviewApprovalBlocked);
                Check(projectWeb.ListProjects().Count == 0);
                // O endpoint de apresentação /project de um projeto vazio
                // deve cadastrar a identidade canônica, sem criar chats.
                var blankProjectStore = new ProjectRegistry(
                    Path.Combine(projectTestDir, "empty-project-link.json"), projectStorage);
                var blankRegistered = blankProjectStore.AddProject(
                    "Nome completo do projeto", emptyProjectPage);
                Check(blankRegistered.Id == "PRO_0000");
                Check(blankRegistered.UrlPrefix == emptyProjectRoot);
                Check(blankRegistered.FullName == "Nome completo do projeto");
                Check(blankRegistered.NextChatNumber == 0);
                Check(blankProjectStore.ListPending().Count == 0);
                Check(blankProjectStore.Preview(null, blankRegistered.Id).LandingUrl ==
                    emptyProjectPage);
                var blankReserved = blankProjectStore.Reserve(
                    "Primeiro chat do projeto vazio", blankRegistered.Id);
                Check(blankReserved.Name == "PRO_0000-GPTBOT_0000");
                Check(blankReserved.State == "RESERVED");
                Check(blankReserved.LandingUrl == emptyProjectPage);
                Check(blankProjectStore.ListProjects().Single().NextChatNumber == 1);
                Check(blankProjectStore.ListPending().Single().Name == blankReserved.Name);
                Check(blankProjectStore.MarkTabCreated(blankReserved.Name, 50, "runtime.first")
                    .State == "TAB_CREATED");
                Check(blankProjectStore.MarkOpened(blankReserved.Name, 50,
                    "runtime.first", emptyProjectPage).State == "OPENED_PENDING_URL");
                var firstSendWrongPinBlocked = false;
                try
                {
                    blankProjectStore.MarkFirstSendAttempted(blankReserved.Name,
                        50, "runtime.unrelated", emptyProjectPage);
                }
                catch (InvalidOperationException ex)
                {
                    firstSendWrongPinBlocked = ex.Message.Contains(
                        "NEW_CHAT_FIRST_SEND_NOT_READY");
                }
                Check(firstSendWrongPinBlocked);
                var firstSend = blankProjectStore.MarkFirstSendAttempted(
                    blankReserved.Name, 50, "runtime.first", emptyProjectPage);
                Check(firstSend.State == "FIRST_SEND_ATTEMPTED");
                Check(firstSend.ProjectId == blankRegistered.Id);
                Check(firstSend.LastObservedUrl == emptyProjectPage);
                Check(blankProjectStore.ListProjects().Single().NextChatNumber == 1);
                var duplicateFirstSendBlocked = false;
                try
                {
                    blankProjectStore.MarkFirstSendAttempted(blankReserved.Name,
                        50, "runtime.first", emptyProjectPage);
                }
                catch (InvalidOperationException ex)
                {
                    duplicateFirstSendBlocked = ex.Message.Contains(
                        "NEW_CHAT_FIRST_SEND_NOT_READY");
                }
                Check(duplicateFirstSendBlocked);
                Check(new ProjectRegistry(
                    Path.Combine(projectTestDir, "empty-project-link.json"), projectStorage)
                    .GetPending(blankReserved.Name).State == "FIRST_SEND_ATTEMPTED");
                var project = projectWeb.AddProject("RM0 — MA GPTBOT Base Capabilities", projectChat);
                Check(project.Id == "PRO_0000" && project.UrlPrefix == projectPrefix);
                Check(projectWeb.CurrentProjectId == "None");
                Check(projectWeb.UseProject("PRO_0000")?.Id == project.Id);
                Check(projectWeb.CurrentProjectId == "PRO_0000");
                Check(projectWeb.UseProject("None") is null && projectWeb.CurrentProjectId == "None");
                Check(projectWeb.UseProject(project)?.Id == project.Id);
                var projectStore = new ProjectRegistry(
                    Path.Combine(projectTestDir, "projects.json"), projectStorage);
                Check(projectStore.ListProjects().Single().Id == "PRO_0000");
                Check(projectStore.Preview(null, null).Name == "GPTBOT_0009");
                Check(projectStore.Preview("Rascunho", project.Id).Name ==
                    "PRO_0000-GPTBOT_0000");
                Check(projectStore.ListPending().Count == 0); // preview não reserva
                var reservedStandalone = projectStore.Reserve(null, null);
                Check(reservedStandalone.Name == "GPTBOT_0009" && reservedStandalone.ProjectId is null);
                Check(projectStore.Reserve("Sem projeto", null).Name == "GPTBOT_0010");
                var reservedInProject = projectStore.Reserve("Com projeto", project.Id);
                Check(reservedInProject.Name == "PRO_0000-GPTBOT_0000");
                Check(reservedInProject.LandingUrl ==
                    ChatUrlScope.ProjectPage(projectPrefix));
                Check(projectStore.Reserve(null, project.Id).Name == "PRO_0000-GPTBOT_0001");
                var secondProject = projectWeb.AddProject("Outro Projeto",
                    "https://chatgpt.com/g/g-p-another-project");
                Check(secondProject.Id == "PRO_0001");
                Check(projectStore.Reserve(null, secondProject.Id).Name ==
                    "PRO_0001-GPTBOT_0000"); // sequência independente por projeto.
                Check(new ProjectRegistry(Path.Combine(projectTestDir, "projects.json"), projectStorage)
                    .ListProjects().Count == 2);
                // Cadastro com URL derivada de chat previamente registrado.
                var linkedChatUrl = "https://chatgpt.com/g/g-p-from-chat/c/37c88f8e-eb9e-1334-e68c-6d75d0fa5ac0";
                projectWeb.AddChat("fromChat", linkedChatUrl);
                var linkedProject = projectWeb.AddProjectFromChat("Projeto de chat existente", "fromChat");
                Check(linkedProject.Id == "PRO_0002");
                Check(linkedProject.UrlPrefix == "https://chatgpt.com/g/g-p-from-chat");
                Check(projectWeb.CurrentProjectId == project.Id);
                Check(projectWeb.ProjectListChats(linkedProject).Any(c => c.Name == "fromChat"));
                Check(project.ProjectListChats().Count == 2);
                Check(Project.RegisteredChats[project.Id].Count == 2);
                // Persistir o pin antes da navegação e reaproveitar a MESMA
                // reserva após falhas/retomadas, sem avançar o contador.
                Check(projectStore.MarkTabCreated(reservedInProject.Name, 42, "runtime.new")
                    .State == "TAB_CREATED");
                var duplicateTabRejected = false;
                try { projectStore.MarkTabCreated(reservedInProject.Name, 99, "other"); }
                catch (InvalidOperationException ex)
                { duplicateTabRejected = ex.Message.Contains("NEW_CHAT_TAB_ALREADY_ASSIGNED"); }
                Check(duplicateTabRejected);
                Check(new ProjectRegistry(Path.Combine(projectTestDir, "projects.json"), projectStorage)
                    .GetPending(reservedInProject.Name).TabRuntimeId == "runtime.new");
                using (projectStore.AcquireChatOperation(reservedInProject.Name))
                {
                    Check(projectStore.GetPending(reservedInProject.Name).Name ==
                        reservedInProject.Name);
                }
                Check(projectStore.MarkOpened(reservedInProject.Name, 42, "runtime.new",
                    projectPrefix).State == "OPENED_PENDING_URL");
                Check(new ProjectRegistry(Path.Combine(projectTestDir, "projects.json"), projectStorage)
                    .GetPending(reservedInProject.Name).WindowHandle == 42);
                var wrongTabRejected = false;
                try { projectStore.MarkOpened(reservedInProject.Name, 100, "runtime.new",
                    projectPrefix); }
                catch (InvalidOperationException ex)
                { wrongTabRejected = ex.Message.Contains("NEW_CHAT_TAB_IDENTITY_CHANGED"); }
                Check(wrongTabRejected);
                // Guia original fechada: a mesma reserva é reautorizada
                // sem incrementar o próximo GPTBOT do projeto.
                var closed = projectStore.Reserve("Guia fechada", linkedProject.Id);
                Check(closed.Name == "PRO_0002-GPTBOT_0000");
                Check(projectStore.MarkTabCreated(closed.Name, 44, "runtime.closed")
                    .State == "TAB_CREATED");
                var wrongClosedPinBlocked = false;
                try { projectStore.AuthorizeClosedTabReopen(
                    closed.Name, 44, "runtime.unrelated"); }
                catch (InvalidOperationException ex)
                { wrongClosedPinBlocked = ex.Message.Contains("NEW_CHAT_REOPEN_NOT_ALLOWED"); }
                Check(wrongClosedPinBlocked);
                var reopened = projectStore.AuthorizeClosedTabReopen(
                    closed.Name, 44, "runtime.closed");
                Check(reopened.State == "REOPEN_REQUESTED");
                Check(reopened.ReopenCount == 1);
                Check(reopened.WindowHandle is null && reopened.TabRuntimeId is null);
                Check(reopened.PreviousWindowHandle == 44);
                Check(reopened.PreviousTabRuntimeId == "runtime.closed");
                Check(reopened.Name == closed.Name);
                var prematurePinBlocked = false;
                try { projectStore.MarkTabCreated(closed.Name, 45, "premature"); }
                catch (InvalidOperationException ex)
                { prematurePinBlocked = ex.Message.Contains("NEW_CHAT_TAB_ALREADY_ASSIGNED"); }
                Check(prematurePinBlocked);
                Check(projectStore.ListProjects().Single(p => p.Id == linkedProject.Id)
                    .NextChatNumber == 1);
                var duplicateReopenBlocked = false;
                try { projectStore.AuthorizeClosedTabReopen(
                    closed.Name, 44, "runtime.closed"); }
                catch (InvalidOperationException ex)
                { duplicateReopenBlocked = ex.Message.Contains("NEW_CHAT_REOPEN_NOT_ALLOWED"); }
                Check(duplicateReopenBlocked);
                projectStore.MarkReopenAttemptStarted(closed.Name);
                Check(projectStore.GetPending(closed.Name).State == "REOPEN_ATTEMPT_STARTED");
                var duplicateStartBlocked = false;
                try { projectStore.MarkReopenAttemptStarted(closed.Name); }
                catch (InvalidOperationException ex)
                { duplicateStartBlocked = ex.Message.Contains("NEW_CHAT_REOPEN_ALREADY_STARTED"); }
                Check(duplicateStartBlocked);
                var reopenedPinned = projectStore.MarkTabCreated(
                    closed.Name, 45, "runtime.new.closed");
                Check(reopenedPinned.State == "TAB_CREATED");
                Check(reopenedPinned.WindowHandle == 45 &&
                    reopenedPinned.TabRuntimeId == "runtime.new.closed");
                Check(reopenedPinned.PreviousTabRuntimeId == "runtime.closed");
                Check(reopenedPinned.ReopenCount == 1);
                Check(new ProjectRegistry(Path.Combine(projectTestDir, "projects.json"),
                    projectStorage).GetPending(closed.Name).ReopenCount == 1);

                // Retry após ação de criar chat no projeto: a intenção
                // persistida não permite um segundo clique na mesma reserva.
                var retryCandidate = projectStore.Reserve("Retomar CTA", secondProject.Id);
                Check(retryCandidate.Name == "PRO_0001-GPTBOT_0001");
                Check(projectStore.MarkTabCreated(retryCandidate.Name, 43, "runtime.43")
                    .State == "TAB_CREATED");
                var previousProjectUrl = secondProject.UrlPrefix +
                    "/c/f3a84c1f-0f6b-c98b-55fa-d16db6c04947";
                Check(projectStore.MarkProjectActionAttempted(
                    retryCandidate.Name, previousProjectUrl).State ==
                    "PROJECT_ACTION_ATTEMPTED");
                var attempted = new ProjectRegistry(
                    Path.Combine(projectTestDir, "projects.json"), projectStorage)
                    .GetPending(retryCandidate.Name);
                Check(attempted.LastObservedUrl == previousProjectUrl);
                Check(attempted.WindowHandle == 43);
                Check(attempted.TabRuntimeId == "runtime.43");
                var unsafeReopenBlocked = false;
                try
                {
                    projectStore.AuthorizeClosedTabReopen(
                        retryCandidate.Name, 43, "runtime.43");
                }
                catch (InvalidOperationException ex)
                { unsafeReopenBlocked = ex.Message.Contains("NEW_CHAT_REOPEN_NOT_ALLOWED"); }
                Check(unsafeReopenBlocked);
                var doubleActionBlocked = false;
                try { projectStore.MarkProjectActionAttempted(
                    retryCandidate.Name, previousProjectUrl); }
                catch (InvalidOperationException ex)
                {
                    doubleActionBlocked = ex.Message.Contains(
                        "PROJECT_ACTION_ALREADY_ATTEMPTED");
                }
                Check(doubleActionBlocked);
                Check(projectStore.MarkOpened(retryCandidate.Name, 43, "runtime.43",
                    secondProject.UrlPrefix).State == "OPENED_PENDING_URL");
                var finalizedRegistry = new ChatRegistry(projectStorage, migrateLegacy: false);
                var finalBinding = new ChatBinding(projectChat, DateTimeOffset.UtcNow, "self-test");
                Check(finalizedRegistry.InsertNewChat(reservedInProject.Name, finalBinding,
                    project.Id, "Com projeto").ProjectId == project.Id);
                Check(finalizedRegistry.InsertNewChat(reservedInProject.Name, finalBinding,
                    project.Id, "Com projeto").Name == reservedInProject.Name);
                var collisionBlocked = false;
                try
                {
                    finalizedRegistry.InsertNewChat(reservedInProject.Name,
                        new ChatBinding("https://chatgpt.com/c/different-id", DateTimeOffset.UtcNow, "test"),
                        project.Id, "Com projeto");
                }
                catch (InvalidOperationException ex)
                { collisionBlocked = ex.Message.Contains("NEW_CHAT_NAME_COLLISION"); }
                Check(collisionBlocked);
                var duplicateUrlBlocked = false;
                try { finalizedRegistry.InsertNewChat("PRO_0000-GPTBOT_9999",
                    finalBinding, project.Id, null); }
                catch (InvalidOperationException ex)
                { duplicateUrlBlocked = ex.Message.Contains("NEW_CHAT_URL_COLLISION"); }
                Check(duplicateUrlBlocked);
                projectStore.MarkCompleted(reservedInProject.Name);
                Check(project.ProjectListChats().Any(c => c.Name == reservedInProject.Name &&
                    c.State == "REGISTERED" && c.DisplayTitle == "Com projeto"));
                // Captura anterior à criação da classe Project: reconhecida por URL.
                var existingProjectChatUrl = projectPrefix +
                    "/c/77647d2e-3009-a49a-71cc-b19fb262079d";
                new ChatRegistry(projectStorage, migrateLegacy: false).Upsert("GPTBOT_0008",
                    new ChatBinding(existingProjectChatUrl, DateTimeOffset.UtcNow, "legacy"));
                Check(project.ProjectListChats().Any(c => c.Name == "GPTBOT_0008" &&
                    c.Url == existingProjectChatUrl && c.State == "REGISTERED"));
                Check(projectWeb.CurrentProjectId == project.Id);
                // Comandos do shell de projetos: nenhuma seleção de chat é alterada.
                var originalStdout = Console.Out;
                try
                {
                    using var output = new StringWriter();
                    Console.SetOut(output);
                    ConsoleShell.Dispatch(projectWeb, "use project None");
                    Check(projectWeb.CurrentProjectId == "None");
                    ConsoleShell.Dispatch(projectWeb, "use project \"PRO_0000\"");
                    Check(projectWeb.CurrentProjectId == project.Id);
                    ConsoleShell.Dispatch(projectWeb, "projectListChats()");
                    Check(output.ToString().Contains("PRO_0000-GPTBOT_0000"));
                    ConsoleShell.Dispatch(projectWeb, "web.useProject(None)");
                    Check(projectWeb.CurrentProjectId == "None");
                    ConsoleShell.Dispatch(projectWeb, "web.projectListChats(\"PRO_0000\")");
                    Check(projectWeb.CurrentProjectId == "None");
                    ConsoleShell.Dispatch(projectWeb, "listProjects()");
                    Check(output.ToString().Contains("RM0"));
                }
                finally { Console.SetOut(originalStdout); }
            }

            Check(!web.EnableSendHumanText);
            web.EnableSendHumanText = true;
            Check(new AutomationSettings(settingsPath).EnableSendHumanText);
            web.EnableSendHumanText = false;
            var created = web.AddChat("chat02", "https://chatgpt.com/c/Cd");
            Check(!created.IsAtMaxLimit);
            Check(created.Name == "chat02");
            Check(ReferenceEquals(web.GetChat("chat02"), created));
            Check(new ChatRegistry(storage, migrateLegacy: false).Get("chat02").Url == created.Url);
            var rejectedWithoutSelection = false;
            try { ConsoleShell.Dispatch(web, "getText()"); }
            catch (InvalidOperationException) { rejectedWithoutSelection = true; }
            Check(rejectedWithoutSelection);
            var savedOutput = Console.Out;
            try
            {
                using var captured = new StringWriter();
                Console.SetOut(captured);
                ConsoleShell.Dispatch(web, "USE \"chat02\";");
                Check(web.CurrentChatName == "chat02");
                ConsoleShell.Dispatch(web,
                    "setFullName(\"Controlar UIA com C# 2\")");
                Check(web.GetChat("chat02").FullName == "Controlar UIA com C# 2");
                Check(ConsoleShell.FormatPrompt(
                    web.CurrentProject?.FullName, web.CurrentChatName,
                    web.CurrentProjectId, web.CurrentChat?.FullName) ==
                    "None[chat02 - Controlar UIA com C# 2|None]> ");
                Check(new ChatRegistry(storage, migrateLegacy: false)
                    .Get("chat02").FullNameSource == "MANUAL");
                ConsoleShell.Dispatch(web, "getFullName()");
                Check(captured.ToString().Contains("Controlar UIA com C# 2"));
                ConsoleShell.Dispatch(web, "use");
                Check(captured.ToString().Contains("chat02"));
                ConsoleShell.Dispatch(web, "web.use(\"chat02\")");
                ConsoleShell.Dispatch(web, "use chat02;");
                ConsoleShell.Dispatch(web, "isAtMaxLimit");
                ConsoleShell.Dispatch(web, "chat02.isAtMaxLimit");
                Check(captured.ToString().Contains("isAtMaxLimit"));
                ConsoleShell.Dispatch(web, "enableSendHumanText = true");
                Check(web.EnableSendHumanText);
                ConsoleShell.Dispatch(web, "web.enableSendHumanText()");
                ConsoleShell.Dispatch(web, "web.setEnableSendHumanText(false)");
                Check(!web.EnableSendHumanText);
                ConsoleShell.Dispatch(web, "enableSendHumanText(true)");
                Check(new AutomationSettings(settingsPath).EnableSendHumanText);
                ConsoleShell.Dispatch(web, "enableSendHumanText = false");
                Check(!new AutomationSettings(settingsPath).EnableSendHumanText);
            }
            finally { Console.SetOut(savedOutput); }
            Check(ReferenceEquals(web.RequireCurrentChat(), created));
            var external = new ChatRegistry(storage, migrateLegacy: false);
            external.Upsert("chat03", new ChatBinding("https://chatgpt.com/c/De", DateTimeOffset.UtcNow, "self-test"));
            external.MarkMaxLimit("chat01", "https://chatgpt.com/c/Changed");
            Check(!web.GetChat("chat01").IsAtMaxLimit); // Cache local ainda não recarregado.
            Check(web.ListChats().Count == 2);
            Check(web.Refresh().Count == 3);
            Check(web.GetChat("chat01").IsAtMaxLimit);
            var stoppedOnMax = false;
            try { web.GetChat("chat01").SendMessage("não enviar"); }
            catch (InvalidOperationException ex) { stoppedOnMax = ex.Message.Contains("CHAT_AT_MAX_LIMIT"); }
            Check(stoppedOnMax); // Bloqueio sem abrir o Edge/UIA.
            Check(web.GetChat("chat03").Url == "https://chatgpt.com/c/De");
            Check(web.CurrentChatName == "chat02");
            Check(ReferenceEquals(web.GetChat("chat02"), created));
            Check(ReferenceEquals(web.AddChat("chat02", "https://chatgpt.com/c/Cd"), created));
            var rejected = false;
            try { web.AddChat("chat02", "https://chatgpt.com/c/Different"); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
            web.RemoveChat("chat02");
            Check(web.CurrentChatName is null);
            Check(web.ListChats().Count == 2);
            Check(new ChatRegistry(storage, migrateLegacy: false).ListChats().Count == 2);
            web.UseChat("chat03");
            new ChatRegistry(storage, migrateLegacy: false).Upsert("chat03",
                new ChatBinding("https://chatgpt.com/c/NewUrl", DateTimeOffset.UtcNow, "external-change"));
            Check(web.CurrentChatName == "chat03");
            web.Refresh();
            Check(web.CurrentChatName is null); // URL alterada externamente não recebe contexto antigo.
            Check(web.GetChat("chat03").Url == "https://chatgpt.com/c/NewUrl");
            // Capture registra sem selecionar e sem escrever o estado global legado.
            web.UseChat("chat03");
            var legacyBefore = CurrentBinding.currentBindedChat;
            var capturedChat = web.CaptureChat("chat04", "https://chatgpt.com/c/NewCaptured");
            Check(capturedChat.Name == "chat04");
            Check(web.CurrentChatName == "chat03");
            Check(ReferenceEquals(legacyBefore, CurrentBinding.currentBindedChat));
            Check(new ChatRegistry(storage, migrateLegacy: false).Get("chat04").Url ==
                "https://chatgpt.com/c/NewCaptured");
            Check(ReferenceEquals(web.CaptureChat("chat04",
                "https://chatgpt.com/c/NewCaptured"), capturedChat));
            var noCaptureOverwrite = false;
            try { web.CaptureChat("chat04", "https://chatgpt.com/c/Wrong"); }
            catch (InvalidOperationException ex)
            { noCaptureOverwrite = ex.Message.Contains("CAPTURE_URL_CONFLICT"); }
            Check(noCaptureOverwrite);
            var bindShellDeprecated = false;
            try { ConsoleShell.Dispatch(web, "bindToChat(\"chat04\")"); }
            catch (InvalidOperationException ex)
            { bindShellDeprecated = ex.Message.Contains("BIND_TO_CHAT_DEPRECATED"); }
            Check(bindShellDeprecated);
            web.UseChat("chat04");
            Check(web.RequireCurrentChat().Url == capturedChat.Url);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
    private static void Check(bool condition,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int lineNumber = 0)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"SELF_TEST_ASSERTION_FAILED: linha {lineNumber}: {expression}");
    }
}
