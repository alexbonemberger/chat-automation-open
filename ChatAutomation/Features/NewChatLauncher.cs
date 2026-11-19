using ChatAutomation.Browser;
using ChatAutomation.Domain;
using ChatAutomation.Infrastructure;
using ChatAutomation.State;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace ChatAutomation.Features;

public sealed record NewChatOpenEvidence(
    string Status, string ObservedUrl, long WindowHandle, string TabRuntimeId);

// Somente criação de nova guia e navegação para a página de composição.
// Não escreve prompt, não clica em Enviar e não inventa o ID remoto do chat.
public static class NewChatLauncher
{
    internal static bool InExpectedScope(string currentUrl, string? projectPrefix)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
            return false;
        if (projectPrefix is null)
            return uri.AbsolutePath == "/" ||
                ChatUrlScope.IsPersistentConversation(currentUrl, null);
        var prefixPath = new Uri(projectPrefix).AbsolutePath.TrimEnd('/');
        return uri.AbsolutePath.Equals(prefixPath, StringComparison.Ordinal) ||
            uri.AbsolutePath.Equals(prefixPath + "/project", StringComparison.Ordinal) ||
            ChatUrlScope.IsPersistentConversation(currentUrl, projectPrefix);
    }

    // A página /project de um projeto vazio tem compositor próprio. O
    // primeiro envio na página cria a conversa do projeto; navegar à raiz
    // pode redirecionar para uma conversa já existente e não deve ser usado
    // como confirmação de criação.
    internal static bool IsProjectComposePage(string url, string projectPrefix) =>
        UrlIdentity.Exact(url, projectPrefix) ||
        UrlIdentity.Exact(url, ChatUrlScope.ProjectPage(projectPrefix));

    // Política testável, separada das leituras UIA:
    // observar guia selecionada é passivo; selecionar aba/acionar CTA exige foco.
    internal static bool ShouldPauseForSelectedTabChange(bool selectedIsPinned) =>
        !selectedIsPinned;

    internal static bool CanInvokeProjectAction(
        bool edgeForeground, bool selectedIsPinned) =>
        edgeForeground && selectedIsPinned;

    // Ao navegar à raiz de um projeto, o ChatGPT pode mostrar uma página
    // de projeto em vez de um compositor. Acionamos somente um CTA de
    // criação especificamente nomeado para o projeto; o "Novo chat"
    // genérico da barra lateral NÃO é prova de associação ao projeto.
    internal static bool IsProjectNewChatAction(string? label)
    {
        var normalized = (label ?? "").Trim().ToLowerInvariant();
        return normalized is
            "new chat in project" or "new chat in this project" or
            "start chat in project" or "start a chat in this project" or
            "novo chat no projeto" or "novo chat neste projeto" or
            "iniciar chat no projeto" or "iniciar um chat neste projeto" or
            "criar chat no projeto" or "criar conversa no projeto";
    }

    private static bool InsideDocument(AutomationElement element)
    {
        try
        {
            var parent = element.Parent;
            for (var i = 0; parent is not null && i < 32; i++, parent = parent.Parent)
            {
                if (parent.ControlType == ControlType.Document) return true;
                if (parent.ControlType == ControlType.Window) return false;
            }
        }
        catch { /* UIA pode invalidar um elemento durante navegação. */ }
        return false;
    }

    private static bool TryStartProjectChat(UIA3Automation uia, IntPtr hwnd,
        string currentUrl, string expectedRuntimeId,
        Action<string>? onProjectActionAttempted)
    {
        var root = EdgeBrowser.FromHandle(uia, hwnd);
        var actions = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
            .Or(cf.ByControlType(ControlType.Hyperlink)))
            .Where(e =>
            {
                try
                {
                    return InsideDocument(e) &&
                        e.Properties.IsEnabled.ValueOrDefault &&
                        !e.Properties.IsOffscreen.ValueOrDefault &&
                        IsProjectNewChatAction(e.Properties.Name.ValueOrDefault) &&
                        e.Patterns.Invoke.IsSupported;
                }
                catch { return false; }
            }).ToArray();

        if (actions.Length == 0) return false; // Página ainda carregando.
        if (actions.Length != 1)
            throw new InvalidOperationException(
                "PROJECT_NEW_CHAT_AMBIGUOUS: múltiplas ações de criação de chat.");
        var control = actions[0].Patterns.Invoke.Pattern
            ?? throw new InvalidOperationException("PROJECT_NEW_CHAT_NOT_INVOCABLE");
        // Consultas UIA podem acontecer sem foco; INVOKE exige que o usuário
        // não tenha mudado para outra janela e que o mesmo pin esteja ativo.
        if (Win32.GetForegroundWindow() != hwnd) return false;
        SelectedTabState selected;
        try { selected = EdgeBrowser.SelectedTab(uia, hwnd); }
        catch (InvalidOperationException) { return false; }
        if (!CanInvokeProjectAction(Win32.GetForegroundWindow() == hwnd,
            string.Equals(selected.RuntimeId, expectedRuntimeId,
                StringComparison.Ordinal)))
            return false;
        onProjectActionAttempted?.Invoke(currentUrl); // Persistência ANTES do clique.
        control.Invoke(); // Uma única ação, nunca retry de clique.
        return true;
    }

    // Reutiliza uma guia de conversa do MESMO projeto. Não usa Ctrl+T e
    // não descobre outra guia por título/índice. O chamador deve ter
    // confirmado que não existe rascunho no chat de origem.
    internal static SelectedTabState NavigatePinnedProjectConversationToComposer(
        UIA3Automation uia, long windowHandle, string runtimeId,
        string currentUrl, string projectPrefix)
    {
        var composeUrl = ChatUrlScope.ProjectPage(projectPrefix);
        if (!ChatUrlScope.IsPersistentConversation(currentUrl, projectPrefix))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SOURCE_SCOPE_INVALID: origem não é uma conversa do projeto.");
        var observed = EdgeBrowser.ActivateTabAndReadCurrentUrl(
            uia, windowHandle, runtimeId);
        if (!UrlIdentity.Exact(observed.Url, currentUrl) ||
            !string.Equals(observed.TabRuntimeId, runtimeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SOURCE_CHANGED: URL/RuntimeId de origem divergiram.");

        var hwnd = new IntPtr(windowHandle);
        Win32.ForegroundOrThrow(hwnd);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
        var selected = EdgeBrowser.SelectedTab(uia, hwnd);
        if (!string.Equals(selected.RuntimeId, runtimeId, StringComparison.Ordinal) ||
            Win32.GetForegroundWindow() != hwnd)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_FOCUS_CHANGED_BEFORE_NAVIGATION: não digitou URL.");

        Keyboard.Type(composeUrl);
        selected = EdgeBrowser.SelectedTab(uia, hwnd);
        if (!string.Equals(selected.RuntimeId, runtimeId, StringComparison.Ordinal) ||
            Win32.GetForegroundWindow() != hwnd)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_FOCUS_CHANGED_BEFORE_ENTER: não navegou.");
        Keyboard.Type(VirtualKeyShort.RETURN);

        for (var attempt = 0; attempt < 45; attempt++)
        {
            Thread.Sleep(160);
            try
            {
                selected = EdgeBrowser.SelectedTab(uia, hwnd);
                if (!string.Equals(selected.RuntimeId, runtimeId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "PROJECT_COMPOSE_TAB_CHANGED: guia originalmente vinculada mudou.");
                if (UrlIdentity.Exact(EdgeBrowser.ReadActiveUrl(uia, hwnd), composeUrl))
                    return selected;
            }
            catch (InvalidOperationException ex) when (
                !ex.Message.StartsWith("PROJECT_COMPOSE_TAB_CHANGED",
                    StringComparison.Ordinal))
            {
                // UIA transitória durante carregamento. Nunca reenviar Enter.
            }
        }
        throw new InvalidOperationException(
            "PROJECT_COMPOSE_NAVIGATION_UNVERIFIED: não foi possível confirmar " +
            "o editor do projeto na mesma guia. Nenhum envio realizado.");
    }

    public static NewChatOpenEvidence Open(UIA3Automation uia, string landingUrl,
        string? projectPrefix, long? preferredWindowHandle = null,
        Action<SelectedTabState>? onTabCreated = null,
        Action<string>? onProjectActionAttempted = null,
        Action? onBeforeTabOpen = null)
    {
        var hwnd = EdgeBrowser.ResolveReadableEdgeWindow(preferredWindowHandle);
        Win32.ForegroundOrThrow(hwnd);
        var before = EdgeBrowser.CountBrowserTabs(uia, hwnd);
        if (before <= 0)
            throw new InvalidOperationException("NEW_CHAT_TABS_UNAVAILABLE: nenhuma guia enumerável.");
        var previous = EdgeBrowser.SelectedTab(uia, hwnd);
        onBeforeTabOpen?.Invoke(); // Persiste intenção antes do efeito Ctrl+T.
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);

        SelectedTabState? created = null;
        for (var i = 0; i < 25; i++)
        {
            Thread.Sleep(120);
            if (Win32.GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("NEW_CHAT_FOCUS_CHANGED_AFTER_CTRL_T");
            try
            {
                if (EdgeBrowser.CountBrowserTabs(uia, hwnd) <= before) continue;
                var candidate = EdgeBrowser.SelectedTab(uia, hwnd);
                if (candidate.RuntimeId != previous.RuntimeId)
                {
                    created = candidate;
                    break;
                }
            }
            catch (InvalidOperationException) { /* aguardar UIA atualizar */ }
        }
        if (created is null)
            throw new InvalidOperationException(
                "NEW_CHAT_TAB_UNVERIFIED: nova guia não comprovada; nenhuma URL digitada.");

        // Persistir HWND/RuntimeId ANTES de navegar. Se a UIA falhar mais
        // tarde, a retomada usa esta mesma guia, não abre outra.
        onTabCreated?.Invoke(created);

        // Alguns estados de carregamento mudam a seleção do Edge depois de
        // Ctrl+T. Retomar somente pelo RuntimeId antes de usar o teclado.
        _ = EdgeBrowser.FocusPinnedNewChatTab(uia, created.WindowHandle, created.RuntimeId);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
        Keyboard.Type(landingUrl);
        var tabBeforeEnter = EdgeBrowser.SelectedTab(uia, hwnd);
        if (tabBeforeEnter.RuntimeId != created.RuntimeId)
            throw new InvalidOperationException(
                "NEW_CHAT_TAB_CHANGED_BEFORE_ENTER: URL digitada mas navegação cancelada. " +
                EdgeBrowser.InspectNewChatPin(uia, created.WindowHandle, created.RuntimeId));
        Keyboard.Type(VirtualKeyShort.RETURN);

        return VerifyNewChatEditor(uia, hwnd, created, projectPrefix,
            onProjectActionAttempted);
    }

    public static NewChatOpenEvidence Resume(UIA3Automation uia,
        long windowHandle, string runtimeId, string? projectPrefix,
        bool actionAttempted = false, string? beforeActionUrl = null)
    {
        // Só retoma uma guia cujo HWND + RuntimeId foram persistidos pelo
        // launcher antes da navegação. Não abre nova guia nem envia texto.
        TabMatch observed;
        try
        {
            observed = EdgeBrowser.ActivateTabAndReadCurrentUrl(
                uia, windowHandle, runtimeId);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "NEW_CHAT_RESUME_PIN_UNVERIFIED: guia persistida não pôde ser " +
                "reativada; nenhuma nova guia foi aberta. " +
                EdgeBrowser.InspectNewChatPin(uia, windowHandle, runtimeId), ex);
        }
        if (!InExpectedScope(observed.Url, projectPrefix))
            throw new InvalidOperationException(
                "NEW_CHAT_RESUME_SCOPE_UNVERIFIED: a guia reservada não está " +
                "no escopo esperado; nenhuma navegação ou envio realizado.");
        var tab = new SelectedTabState(observed.Handle, observed.TabIndex, runtimeId);
        return VerifyNewChatEditor(uia, new IntPtr(windowHandle), tab, projectPrefix,
            onProjectActionAttempted: null, actionAttempted: actionAttempted,
            beforeActionUrl: beforeActionUrl);
    }

    private static IReadOnlyList<string> VisibleNewChatActions(UIA3Automation uia, IntPtr hwnd)
    {
        try
        {
            return EdgeBrowser.FromHandle(uia, hwnd)
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
                    .Or(cf.ByControlType(ControlType.Hyperlink)))
                .Where(InsideDocument)
                .Where(item =>
                {
                    try { return !item.Properties.IsOffscreen.ValueOrDefault; }
                    catch { return false; }
                })
                .Select(item =>
                {
                    try { return item.Properties.Name.ValueOrDefault ?? ""; }
                    catch { return ""; }
                })
                .Where(name => name.Length > 0 && name.Length <= 90 &&
                    (name.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("convers", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("projet", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("project", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        }
        catch { return []; }
    }

    internal static NewChatOpenEvidence FocusPaused(
        SelectedTabState pinned, string observedUrl) =>
        new("PAUSED_FOCUS_CHANGED", observedUrl, pinned.WindowHandle, pinned.RuntimeId);

    private static NewChatOpenEvidence VerifyNewChatEditor(UIA3Automation uia,
        IntPtr hwnd, SelectedTabState pinned, string? projectPrefix,
        Action<string>? onProjectActionAttempted = null,
        bool actionAttempted = false, string? beforeActionUrl = null)
    {
        var projectActionInvoked = actionAttempted;
        string? redirectedExistingUrl = beforeActionUrl;
        var wasAtProjectRoot = false;
        var lastObservedPinnedUrl = "";
        for (var i = 0; i < 45; i++)
        {
            Thread.Sleep(180);
            // Foco é requisito apenas para teclado/CTA. Leitura UIA da guia
            // já selecionada pode continuar em segundo plano, sem roubar foco.
            var edgeForeground = Win32.GetForegroundWindow() == hwnd;
            SelectedTabState currentTab;
            try { currentTab = EdgeBrowser.SelectedTab(uia, hwnd); }
            catch (InvalidOperationException)
            {
                // UIA pode não expor seleção enquanto a guia carrega.
                // Nunca escolhemos uma aba alternativa sem o pin completo.
                continue;
            }
            if (ShouldPauseForSelectedTabChange(
                selectedIsPinned: currentTab.RuntimeId == pinned.RuntimeId))
            {
                // Após Enter, esta fase é SOMENTE de observação: se o
                // usuário trocar de guia (mesmo com Edge foreground), não
                // trazemos a guia antiga à frente, nem selecionamos por pin.
                return FocusPaused(pinned, lastObservedPinnedUrl);
            }
            string observed;
            try { observed = EdgeBrowser.ReadActiveUrl(uia, hwnd); }
            catch (InvalidOperationException) { continue; }
            lastObservedPinnedUrl = observed;
            if (!InExpectedScope(observed, projectPrefix)) continue;

            if (projectPrefix is null &&
                ChatUrlScope.IsPersistentConversation(observed, null))
                throw new InvalidOperationException(
                    "NEW_CHAT_EXISTING_URL_UNVERIFIED: navegação à raiz abriu uma " +
                    "conversa existente, não uma nova. Reserva preservada.");

            if (projectPrefix is not null && !projectActionInvoked)
            {
                if (IsProjectComposePage(observed, projectPrefix))
                {
                    wasAtProjectRoot = true;
                    // A rota /project abre o compositor dentro do projeto,
                    // mesmo quando ainda não há chats. Não aciona "Novo chat".
                    // Comprovar URL + RuntimeId + campo antes de aceitar.
                    try
                    {
                        var binding = new ChatBinding(observed, DateTimeOffset.UtcNow,
                            "new-project-root", hwnd.ToInt64(), null, pinned.RuntimeId);
                        _ = ChatFeatures.DetectTextAreaPassive(uia, binding);
                        return new NewChatOpenEvidence("OPENED_PENDING_URL",
                            observed, hwnd.ToInt64(), pinned.RuntimeId);
                    }
                    catch (InvalidOperationException) { /* Sem editor comprovado. */ }
                }
                else if (ChatUrlScope.IsPersistentConversation(observed, projectPrefix))
                {
                    // A raiz redirecionou para um chat ANTIGO do mesmo projeto.
                    // Essa URL NUNCA é aceita como nova. Porém, podemos
                    // procurar um CTA explícito de nova conversa no projeto.
                    redirectedExistingUrl = observed;
                }
                else
                    throw new InvalidOperationException(
                        "PROJECT_NEW_CHAT_SCOPE_INVALID: redirecionamento não reconhecido.");

                // Sem foco, aguardar o compositor passivamente. Nunca acionar
                // um CTA, clique ou mudar aba no Edge em segundo plano.
                if (!CanInvokeProjectAction(edgeForeground, selectedIsPinned: true))
                    continue;
                if (TryStartProjectChat(uia, hwnd, observed, pinned.RuntimeId,
                    onProjectActionAttempted))
                {
                    projectActionInvoked = true;
                    redirectedExistingUrl = observed;
                    continue;
                }
                continue;
            }

            // Depois do CTA, não aceitar o mesmo chat antigo que a raiz abriu.
            if (redirectedExistingUrl is not null &&
                projectPrefix is not null &&
                ChatUrlScope.IsPersistentConversation(redirectedExistingUrl, projectPrefix) &&
                UrlIdentity.Exact(observed, redirectedExistingUrl))
                continue;

            try
            {
                var binding = new ChatBinding(observed, DateTimeOffset.UtcNow,
                    "new-chat-landing", hwnd.ToInt64(), null, pinned.RuntimeId);
                var editor = ChatFeatures.DetectTextAreaPassive(uia, binding);
                if (string.IsNullOrWhiteSpace(editor.ControlType)) continue;
                return new NewChatOpenEvidence("OPENED_PENDING_URL",
                    observed, hwnd.ToInt64(), pinned.RuntimeId);
            }
            catch (InvalidOperationException) { /* Editor ainda indisponível. */ }
        }
        // Perder foco não é perder a guia. Sem confirmação segura da criação,
        // devolver estado pausado e preservar o pin; sem erro fatal/reabertura.
        if (Win32.GetForegroundWindow() != hwnd)
            return FocusPaused(pinned, lastObservedPinnedUrl);
        if (projectPrefix is not null)
        {
            var names = string.Join(" | ", VisibleNewChatActions(uia, hwnd));
            var reason = projectActionInvoked
                ? "PROJECT_NEW_CHAT_AFTER_ACTION_UNVERIFIED"
                : "PROJECT_NEW_CHAT_ACTION_NOT_FOUND";
            throw new InvalidOperationException(
                reason + ": o projeto redirecionou para uma conversa existente " +
                "ou não mostrou editor de nova conversa; não houve envio. " +
                "ProjectRootSeen=" + wasAtProjectRoot +
                "; CandidateActions=[" + names + "].");
        }
        throw new InvalidOperationException(
            "NEW_CHAT_LANDING_UNVERIFIED: guia aberta, mas compositor de nova " +
            "conversa não comprovado. Nenhuma mensagem enviada.");

    }
}
