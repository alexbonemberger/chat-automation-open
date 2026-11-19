using System.Diagnostics;
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

public enum ChatOpenMode { NewTab, NewWindow }

public sealed record ComposerInfo(string ControlType, string Name, string AutomationId);
public sealed record ChatOpenResult(string Status, string Mode, string TargetUrl,
    long WindowHandle, int TabIndex);

public static class ChatFeatures
{
    // Campo real: nos clientes web modernos geralmente é um contenteditable/role=textbox,
    // exposto ao UIA como Edit (algumas versões: Document), NÃO HTML textarea necessariamente.
    private static readonly string[] ComposerIds = ["prompt-textarea", "prompt-textbox", "composer-input"];
    private static readonly string[] ComposerNames =
    [
        "message chatgpt", "message", "mensagem", "ask anything", "ask chatgpt",
        "pergunte", "digite uma mensagem", "type a message", "send a message",
        "chat with chatgpt", "talk to qwen", "message qwen"
    ];
    private static readonly string[] SendNames =
    ["send prompt", "send message", "send", "enviar mensagem", "enviar", "enviar prompt"];

    // Indicadores de geração expostos pela acessibilidade do ChatGPT.
    // Seletores conservadores: "Stop" somente no conteúdo da página (nunca
    // o botão de interromper carregamento do chrome do navegador).
    private static readonly string[] StopControlIds =
    ["stop-button", "stop-generating", "stop-response", "stop-streaming"];
    private static readonly string[] StopControlNames =
    [
        "stop", "stop generating", "stop generating response", "stop response",
        "stop streaming", "stop streaming response", "stop generation",
        "parar", "parar geração", "parar de gerar", "parar resposta",
        "interromper", "interromper geração", "interromper resposta"
    ];

    internal static bool IsStopControl(string? name, string? automationId) =>
        StopControlIds.Any(x => string.Equals(x, automationId, StringComparison.OrdinalIgnoreCase)) ||
        StopControlNames.Any(x => string.Equals(x, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool InsidePageDocument(AutomationElement element)
    {
        try
        {
            var parent = element.Parent;
            for (var depth = 0; parent is not null && depth < 32; depth++, parent = parent.Parent)
            {
                if (parent.ControlType == ControlType.Document) return true;
                if (parent.ControlType == ControlType.Window) return false;
            }
        }
        catch { /* Elemento desapareceu; não é evidência positiva. */ }
        return false;
    }

    // O Edge pode expor o botão Parar como irmão de Document:ChatGPT.
    // Não confundir um controle do chrome do navegador com geração ativa:
    // aceitar apenas um Stop nomeado, página de conversa legítima e único
    // documento ChatGPT na janela já validada pelo pin.
    internal static bool CanUseDetachedBusyStop(
        bool persistentConversationUrl, int documentCount, int namedStopCount) =>
        persistentConversationUrl && documentCount == 1 && namedStopCount == 1;

    // Totalmente passivo: não muda a guia nem rouba foco. Se não for possível
    // verificar a GUIA e o compositor, lança erro em vez de presumir false.
    public static bool IsGenerating(UIA3Automation uia, ChatBinding binding)
    {
        var selected = EdgeBrowser.ResolvePassiveExact(uia, binding);
        var hwnd = new IntPtr(selected.Handle);
        var root = EdgeBrowser.FromHandle(uia, hwnd);
        var buttons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        var namedStops = buttons.Where(button =>
        {
            try
            {
                return button.Properties.IsEnabled.ValueOrDefault &&
                    !button.Properties.IsOffscreen.ValueOrDefault &&
                    IsStopControl(button.Properties.Name.ValueOrDefault,
                        button.Properties.AutomationId.ValueOrDefault);
            }
            catch { return false; }
        }).ToArray();
        var stopVisible = namedStops.Any(InsidePageDocument);
        if (!stopVisible && namedStops.Length > 0)
        {
            var documents = root.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Document))
                .Where(d =>
                {
                    try { return d.Properties.IsEnabled.ValueOrDefault &&
                        !d.Properties.IsOffscreen.ValueOrDefault &&
                        string.Equals(d.Properties.Name.ValueOrDefault,
                            "ChatGPT", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }).ToArray();
            if (CanUseDetachedBusyStop(
                ChatUrlScope.IsPersistentConversation(binding.Url, null) ||
                IsSupportedComposerPageUrl(binding.Url) &&
                    !ProjectNameInspector.IsProjectPage(binding.Url),
                documents.Length, namedStops.Length))
                stopVisible = true;
            else
                // Há evidência de Stop mas a UIA não permitiu ligá-la
                // à única página ChatGPT. Não interpretar como idle.
                throw new InvalidOperationException(
                    "BUSY_STOP_UNVERIFIED: botão(s) de interrupção visível(is), " +
                    "mas a identidade do documento/controle é ambígua.");
        }
        var confirm = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (confirm is null || confirm.TabRuntimeId != selected.TabRuntimeId ||
            confirm.TabIndex != selected.TabIndex)
            throw new InvalidOperationException(
                "BUSY_TARGET_CHANGED: guia/URL mudou durante a leitura. Resultado descartado.");
        if (stopVisible) return true;
        // A ausência do botão, sozinha, não demonstra estado ocioso se a
        // página ainda estiver carregando ou o editor não estiver disponível.
        _ = FindComposer(uia, hwnd);
        confirm = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (confirm is null || confirm.TabRuntimeId != selected.TabRuntimeId ||
            confirm.TabIndex != selected.TabIndex)
            throw new InvalidOperationException("BUSY_TARGET_CHANGED: URL/guia mudou durante a leitura.");
        return false;
    }

    public static TabMatch FocusCurrentChat(UIA3Automation uia) =>
        FocusChat(uia, CurrentBinding.Require());

    public static TabMatch FocusChat(UIA3Automation uia, ChatBinding binding) =>
        EdgeBrowser.FocusExact(uia, binding);

    public static ComposerInfo DetectTextArea(UIA3Automation uia) =>
        DetectTextArea(uia, CurrentBinding.Require());

    public static ComposerInfo DetectTextArea(UIA3Automation uia, ChatBinding binding)
    {
        var match = FocusChat(uia, binding);
        var editor = FindComposer(uia, new IntPtr(match.Handle));
        return new ComposerInfo(editor.ControlType.ToString(),
            editor.Properties.Name.ValueOrDefault ?? "",
            editor.Properties.AutomationId.ValueOrDefault ?? "");
    }

    // Consulta passiva do editor: NUNCA ativa janela, guia ou campo.
    // Antes/depois confirma a mesma URL exata + RuntimeId selecionado.
    // Usada somente no fluxo de descoberta de chats novos, sem digitação.
    public static ComposerInfo DetectTextAreaPassive(UIA3Automation uia, ChatBinding binding)
    {
        if (binding.PreferredWindowHandle is not { } windowHandle ||
            string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId))
            throw new InvalidOperationException(
                "PASSIVE_COMPOSER_PIN_REQUIRED: HWND e RuntimeId obrigatórios.");

        var hwnd = new IntPtr(windowHandle);
        var before = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (before is null ||
            !string.Equals(before.TabRuntimeId, binding.PreferredTabRuntimeId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PASSIVE_COMPOSER_TAB_NOT_SELECTED: URL/RuntimeId não confirmados.");
        var editor = FindComposer(uia, hwnd);
        var info = new ComposerInfo(editor.ControlType.ToString(),
            editor.Properties.Name.ValueOrDefault ?? "",
            editor.Properties.AutomationId.ValueOrDefault ?? "");
        var after = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (after is null || after.Handle != before.Handle ||
            after.TabIndex != before.TabIndex ||
            !string.Equals(after.TabRuntimeId, before.TabRuntimeId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PASSIVE_COMPOSER_TAB_CHANGED: seleção mudou durante a consulta.");
        return info;
    }

    // Em /project o Edge pode nomear o editor como "Novo chat em <projeto>".
    // O reconhecimento não amplia a busca para botões, apenas Edit/Document.
    internal static bool MatchesComposerIdentity(string? name, string? id) =>
        ComposerIds.Any(s => s.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
        ComposerNames.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (s.Length > 6 && (name ?? "").Contains(s, StringComparison.OrdinalIgnoreCase))) ||
        ProjectNameInspector.NameFromComposeLabel(name) is not null;

    private static bool MatchesComposer(AutomationElement item) =>
        MatchesComposerIdentity(item.Properties.Name.ValueOrDefault,
            item.Properties.AutomationId.ValueOrDefault);

    // Regra pura para o fallback: jamais inferir compositor pelo primeiro
    // Edit global do Edge ou em uma conversa já aberta (/c/...).
    internal static bool CanUseUnnamedProjectComposer(bool isProjectPage,
        int chatGptDocumentCount, int visiblePageEditCount,
        string? name, string? automationId) =>
        isProjectPage && chatGptDocumentCount == 1 &&
        visiblePageEditCount == 1 &&
        string.IsNullOrWhiteSpace(name) &&
        string.IsNullOrWhiteSpace(automationId);

    // Layout Edge/UIA 157 (18/11/2026): alguns contenteditables da página
    // aparecem como Edit irmãos do Document:ChatGPT, e NÃO descendentes
    // do documento. A falta de IsKeyboardFocusable também é conhecida.
    // Aceitar somente a assinatura inteira observada: 1 documento ChatGPT,
    // 1 Edit anônimo, 1 Edit de barra de endereço e mais nenhum Edit visível.
    // A URL exata e o pin da guia são comprovados por DetectTextAreaPassive.
    internal static bool IsBrowserAddressBar(string? name, string? automationId) =>
        string.Equals(automationId, "view_1017", StringComparison.OrdinalIgnoreCase) ||
        new[] {
            "Barra de pesquisa e endereços",
            "Search and address bar",
            "Search or enter web address",
            "Address and search bar",
        }.Any(x => string.Equals(name?.Trim(), x, StringComparison.OrdinalIgnoreCase));

    internal static bool CanUseDetachedUnnamedComposer(
        bool supportedPage, int chatGptDocuments, int visibleEdits,
        int unnamedEdits, int addressBars, string? name, string? automationId) =>
        supportedPage && chatGptDocuments == 1 && visibleEdits == 2 &&
        unnamedEdits == 1 && addressBars == 1 &&
        string.IsNullOrWhiteSpace(name) &&
        string.IsNullOrWhiteSpace(automationId);

    // O mesmo layout achatado pode expor Send fora do Document:ChatGPT.
    // Apenas um botão Send inequívoco e visível dentro de uma rota de
    // compositor validada é aceito como fallback; nunca escolher por índice.
    internal static bool CanUseDetachedSendButton(bool supportedPage,
        int documentCount, int visibleNamedSendButtons) =>
        supportedPage && documentCount == 1 && visibleNamedSendButtons == 1;

    // Uso restrito à página de composição do projeto ou a uma conversa
    // com URL persistente, nunca a uma rota arbitrária do navegador.
    // O pin HWND/RuntimeId e a URL exata são conferidos pelos chamadores.
    internal static bool IsSupportedComposerPageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (ProjectNameInspector.IsProjectPage(url)) return true;
        if (ChatUrlScope.IsPersistentConversation(url, null)) return true;
        try
        {
            var projectPrefix = ChatUrlScope.ProjectPrefix(url);
            return ChatUrlScope.IsPersistentConversation(url, projectPrefix);
        }
        catch (ArgumentException) { return false; }
    }

    private static AutomationElement FindComposer(UIA3Automation uia, IntPtr hwnd)
    {
        var root = EdgeBrowser.FromHandle(uia, hwnd);
        var controls = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.Document)));
        var options = controls.Where(e =>
            e.Properties.IsEnabled.ValueOrDefault &&
            !e.Properties.IsOffscreen.ValueOrDefault && MatchesComposer(e)).ToArray();

        // Evidência UIA do Edge 157 (18/11/2026): o editor pode
        // ser um Edit sem Name/AutomationId dentro de Document:ChatGPT
        // ou um irmão do documento. Não confiar em IsKeyboardFocusable.
        // Os fallbacks exigem rota válida, documento único e ausência de
        // ambiguidade; foco, pin, URL e readback são verificados em separado.
        if (options.Length == 0)
        {
            var supportedPage = false;
            try
            {
                supportedPage = IsSupportedComposerPageUrl(
                    EdgeBrowser.ReadActiveUrl(uia, hwnd));
            }
            catch (InvalidOperationException) { /* sem prova de rota */ }
            if (supportedPage)
            {
                var documents = controls.Where(e =>
                {
                    try
                    {
                        return e.ControlType == ControlType.Document &&
                            e.Properties.IsEnabled.ValueOrDefault &&
                            !e.Properties.IsOffscreen.ValueOrDefault &&
                            string.Equals(e.Properties.Name.ValueOrDefault,
                                "ChatGPT", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                }).ToArray();

                if (documents.Length == 1)
                {
                    var pageEdits = documents[0].FindAllDescendants(
                        cf => cf.ByControlType(ControlType.Edit))
                        .Where(e =>
                        {
                            try { return e.Properties.IsEnabled.ValueOrDefault &&
                                !e.Properties.IsOffscreen.ValueOrDefault; }
                            catch { return false; }
                        }).ToArray();
                    if (pageEdits.Length == 1 &&
                        CanUseUnnamedProjectComposer(supportedPage,
                            documents.Length, pageEdits.Length,
                            pageEdits[0].Properties.Name.ValueOrDefault,
                            pageEdits[0].Properties.AutomationId.ValueOrDefault))
                        options = pageEdits;

                    // Fallback adicional apenas quando nenhum compositor foi
                    // encontrado dentro do Document. Essa disposição da árvore
                    // é observada em /project; não adivinhar o primeiro Edit.
                    if (options.Length == 0 && pageEdits.Length == 0)
                    {
                        var visibleEdits = controls.Where(e =>
                        {
                            try { return e.ControlType == ControlType.Edit &&
                                e.Properties.IsEnabled.ValueOrDefault &&
                                !e.Properties.IsOffscreen.ValueOrDefault; }
                            catch { return false; }
                        }).ToArray();
                        var unnamed = visibleEdits.Where(e =>
                        {
                            try { return string.IsNullOrWhiteSpace(e.Properties.Name.ValueOrDefault) &&
                                string.IsNullOrWhiteSpace(e.Properties.AutomationId.ValueOrDefault); }
                            catch { return false; }
                        }).ToArray();
                        var address = visibleEdits.Where(e =>
                        {
                            try { return IsBrowserAddressBar(
                                e.Properties.Name.ValueOrDefault,
                                e.Properties.AutomationId.ValueOrDefault); }
                            catch { return false; }
                        }).ToArray();
                        if (unnamed.Length == 1 &&
                            CanUseDetachedUnnamedComposer(supportedPage,
                                documents.Length, visibleEdits.Length,
                                unnamed.Length, address.Length,
                                unnamed[0].Properties.Name.ValueOrDefault,
                                unnamed[0].Properties.AutomationId.ValueOrDefault))
                            options = unnamed;
                    }
                }
            }
        }
        if (options.Length != 1)
        {
            // Expor rótulos UIA dos possíveis editores, sem selecionar um
            // controle ambíguo nem tentar enviar conteúdo.
            var candidates = controls.Where(e =>
            {
                try { return e.Properties.IsEnabled.ValueOrDefault &&
                    !e.Properties.IsOffscreen.ValueOrDefault; }
                catch { return false; }
            }).Take(12).Select(e =>
            {
                try { return e.ControlType + ":" +
                    (e.Properties.Name.ValueOrDefault ?? "") + "#" +
                    (e.Properties.AutomationId.ValueOrDefault ?? "") +
                    " insideDocument=" + InsidePageDocument(e) +
                    " keyboardFocusable=" +
                    e.Properties.IsKeyboardFocusable.ValueOrDefault; }
                catch { return "UIA_TRANSIENT"; }
            }).ToArray();
            throw new InvalidOperationException(
                "PROJECT_COMPOSER_NOT_UNIQUE: editor de chat não identificado " +
                $"(candidatos={options.Length}); VisibleEditors=[" +
                string.Join(" | ", candidates) + "]. Nenhum envio realizado.");
        }
        return options[0];
    }

    private static AutomationElement FindSendButton(UIA3Automation uia, IntPtr hwnd)
    {
        var root = EdgeBrowser.FromHandle(uia, hwnd);
        var buttons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        var namedSend = buttons.Where(b =>
        {
            try
            {
                var name = b.Properties.Name.ValueOrDefault ?? "";
                return SendNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)) &&
                    b.Properties.IsEnabled.ValueOrDefault &&
                    !b.Properties.IsOffscreen.ValueOrDefault;
            }
            catch { return false; }
        }).ToArray();
        var options = namedSend.Where(InsidePageDocument).ToArray();
        if (options.Length == 0 && namedSend.Length == 1)
        {
            // Edge 157 pode expor botões de /project fora de Document.
            // Exigir rota legítima e único Document:ChatGPT, além de um
            // único botão de envio nomeado, sem tentar ação em outros casos.
            var documents = root.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Document)).Where(d =>
            {
                try { return d.Properties.IsEnabled.ValueOrDefault &&
                    !d.Properties.IsOffscreen.ValueOrDefault &&
                    string.Equals(d.Properties.Name.ValueOrDefault,
                        "ChatGPT", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToArray();
            var supportedPage = false;
            try { supportedPage = IsSupportedComposerPageUrl(
                EdgeBrowser.ReadActiveUrl(uia, hwnd)); }
            catch (InvalidOperationException) { /* sem URL comprovada */ }
            if (CanUseDetachedSendButton(supportedPage,
                documents.Length, namedSend.Length))
                options = namedSend;
        }
        if (options.Length != 1)
            throw new InvalidOperationException(
                "SEND_BUTTON_NOT_UNIQUE: botão de envio não identificado " +
                $"(inDocument={options.Length}, namedSend={namedSend.Length}). " +
                "O texto permanece como rascunho; nenhuma ação de envio foi executada.");
        return options[0];
    }

    // Valida a identidade da guia em TODA recuperação de foco: URL igual
    // sozinha não é suficiente quando existem cópias da conversa no Edge.
    internal static bool SameTabIdentity(TabMatch expected, TabMatch actual) =>
        expected.Handle == actual.Handle &&
        UrlIdentity.Exact(expected.Url, actual.Url) &&
        (!string.IsNullOrWhiteSpace(expected.TabRuntimeId)
            ? string.Equals(expected.TabRuntimeId, actual.TabRuntimeId, StringComparison.Ordinal)
            : expected.TabIndex == actual.TabIndex);

    private static void VerifyTargetForegroundWithRecovery(
        UIA3Automation uia, ChatBinding binding, TabMatch target)
    {
        var hwnd = new IntPtr(target.Handle);
        var current = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);

        // Alternar manualmente para outra guia não invalida o vínculo. Ao
        // invés de abortar, reativamos a MESMA guia por RuntimeId — nunca
        // outra guia com URL idêntica. Sem RuntimeId, fail-closed.
        if (current is null || !SameTabIdentity(target, current) ||
            Win32.GetForegroundWindow() != hwnd)
            _ = EdgeBrowser.ActivateExactTab(uia, target);

        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var verified = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (verified is null || !SameTabIdentity(target, verified))
            throw new InvalidOperationException(
                "TARGET_TAB_CHANGED: identidade da guia divergiu após a recuperação; nenhuma tecla enviada.");
    }

    // Reobtém o foco automaticamente quando o console PowerShell esteve em primeiro plano.
    // O foco UIA por si só pode não bastar para um editor contenteditable do Edge:
    // tenta ativação/foco programático, depois clique físico limitado e verificado.
    // Não envia teclas até comprovar novamente janela, URL e foco do editor.
    private static AutomationElement FocusComposerForKeyboard(
        UIA3Automation uia, ChatBinding binding, TabMatch target)
    {
        var hwnd = new IntPtr(target.Handle);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            VerifyTargetForegroundWithRecovery(uia, binding, target);
            var editor = FindComposer(uia, hwnd);
            if (editor.Properties.HasKeyboardFocus.ValueOrDefault) return editor;

            // O proxy UIA pode invalidar-se durante a primeira ativação da janela.
            // Falhas transitórias de foco não devem impedir as tentativas seguintes.
            try { editor.Focus(); }
            catch (System.Runtime.InteropServices.COMException) { /* nova tentativa */ }
            catch (InvalidOperationException) { /* nova tentativa */ }
            Thread.Sleep(170);
            VerifyTargetForegroundWithRecovery(uia, binding, target);
            editor = FindComposer(uia, hwnd);
            if (editor.Properties.HasKeyboardFocus.ValueOrDefault) return editor;

            // Contenteditable pode aceitar o foco somente após clique. Fazemos
            // isso apenas no editor identificado de forma única e URL verificada.
            if (attempt >= 1)
            {
                try { editor.Click(); }
                catch (System.Runtime.InteropServices.COMException) { /* nova tentativa */ }
                catch (InvalidOperationException) { /* nova tentativa */ }
                Thread.Sleep(170);
                VerifyTargetForegroundWithRecovery(uia, binding, target);
                editor = FindComposer(uia, hwnd);
                if (editor.Properties.HasKeyboardFocus.ValueOrDefault) return editor;
            }

            // Reativa a MESMA janela (não escolhe outra guia ou outro chat).
            VerifyTargetForegroundWithRecovery(uia, binding, target);
            Win32.ForegroundOrThrow(hwnd);
            Thread.Sleep(130);
        }

        throw new InvalidOperationException(
            "EDITOR_FOCUS_NOT_VERIFIED: tentativas automáticas de foco esgotadas. " +
            "Nenhuma tecla enviada. Verifique se a página está carregada e se " +
            "algum diálogo do Edge está bloqueando o editor.");
    }

    // Editores contenteditable podem expor TextPattern, sem suporte a SetValue.
    // Nunca pressiona teclas sem comprovar URL exata e foco no editor.
    public static object SetText(UIA3Automation uia, ChatBinding binding, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return ClearComposerText(uia, binding);
        text = MessageTimestamp.Apply(text); // Uma vez, inclusive via CLI legada.
        var target = FocusChat(uia, binding);
        var hwnd = new IntPtr(target.Handle);
        var before = ReadComposerValue(FindComposer(uia, hwnd));
        string? observed = before;
        var methods = new[] { "keyboard", "value-pattern" };

        foreach (var method in methods)
        {
            EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
            var editor = FindComposer(uia, hwnd); // Nova referência, nunca reciclar cache UIA após navegação.
            if (method == "value-pattern")
            {
                // Fallback exclusivamente quando UIA fornece controle de valor gravável.
                try
                {
                    if (!editor.Patterns.Value.IsSupported || editor.Patterns.Value.Pattern is not { } value ||
                        value.IsReadOnly.ValueOrDefault) break;
                    value.SetValue(text);
                }
                catch { break; }
            }
            else
            {
                // setText/clear/sendMessage não exigem setFocus() prévio.
                // Retorna somente após validar o foco de teclado do compositor.
                editor = FocusComposerForKeyboard(uia, binding, target);
                EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
                if (!editor.Properties.HasKeyboardFocus.ValueOrDefault)
                    throw new InvalidOperationException("Editor perdeu o foco antes da digitação; nenhuma tecla enviada.");
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(VirtualKeyShort.BACK);
                if (text.Length > 0)
                {
                    var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0)
                            Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RETURN);
                        if (lines[i].Length > 0) Keyboard.Type(lines[i]);
                    }
                }
            }
            for (var attempt = 0; attempt < 7; attempt++)
            {
                Thread.Sleep(130);
                EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
                observed = ReadComposerValue(FindComposer(uia, hwnd));
                if (observed is not null && TextEquivalent(observed, text))
                    return new { status = "DRAFT_VERIFIED", targetUrl = binding.Url,
                        windowHandle = target.Handle, characters = text.Length,
                        textReadback = "MATCHED", inputMethod = method, sent = false };
            }
        }
        throw new InvalidOperationException(
            $"READBACK_MISMATCH: texto não confirmado após reidentificar o editor. " +
            $"Esperado={text.Length} caracteres; observado={(observed is null ? "indisponível" : observed.Length.ToString())}; " +
            $"valor anterior={(before is null ? "indisponível" : before.Length.ToString())}. " +
            "Nenhuma mensagem foi enviada. Execute detectTextArea() e inspect-edge para diagnóstico.");
    }

    // O TextPattern de contenteditable pode expor a dica de texto (placeholder)
    // depois de apagar o valor real. Reconhecemos somente rótulos conhecidos;
    // nunca assumimos que qualquer resíduo significa editor vazio.
    internal static bool IsKnownPlaceholder(string text)
    {
        var content = NormalizeEditorText(text).Trim();
        return new[]
        {
            "Pergunte ao ChatGPT", "Pergunte alguma coisa", "Pergunte qualquer coisa",
            "Digite uma mensagem", "Escreva uma mensagem", "Ask anything",
            "Ask ChatGPT", "Message ChatGPT", "Type a message", "Send a message"
        }.Any(s => content.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            ProjectNameInspector.NameFromComposeLabel(content) is not null;
    }

    // clear(): múltiplas tentativas limitadas, sem usar teclas fora do editor e sem
    // confundir a dica do compositor com um rascunho que permaneceu após apagar.
    private static object ClearComposerText(UIA3Automation uia, ChatBinding binding)
    {
        var target = FocusChat(uia, binding);
        var hwnd = new IntPtr(target.Handle);
        var before = ReadComposerValue(FindComposer(uia, hwnd));
        if (before is null)
            throw new InvalidOperationException("READBACK_UNSUPPORTED: limpeza bloqueada; editor não expõe leitura UIA.");
        if (TextEquivalent(before, string.Empty))
            return ClearResult(binding, target, "already-empty", "MATCHED", "DRAFT_VERIFIED");

        string? observed = before;

        // Primeiro usa ValuePattern quando o UIA disponibiliza escrita.
        // Não requer foco de teclado e evita a corrida com o PowerShell em primeiro plano.
        VerifyTargetForegroundWithRecovery(uia, binding, target);
        var valueEditor = FindComposer(uia, hwnd);
        if (valueEditor.Patterns.Value.IsSupported &&
            valueEditor.Patterns.Value.Pattern is { } writable &&
            !writable.IsReadOnly.ValueOrDefault)
        {
            try { writable.SetValue(string.Empty); }
            catch (System.Runtime.InteropServices.COMException) { /* fallback para teclado */ }
            for (var wait = 0; wait < 6; wait++)
            {
                Thread.Sleep(120);
                VerifyTargetForegroundWithRecovery(uia, binding, target);
                observed = ReadComposerValue(FindComposer(uia, hwnd));
                if (observed is not null && TextEquivalent(observed, string.Empty))
                    return ClearResult(binding, target, "value-pattern", "MATCHED", "DRAFT_VERIFIED");
                if (observed is not null && IsKnownPlaceholder(observed))
                    return ClearResult(binding, target, "value-pattern", "KNOWN_PLACEHOLDER",
                        "CLEARED_PLACEHOLDER_READBACK");
            }
        }

        for (var pass = 0; pass < 3; pass++)
        {
            var editor = FocusComposerForKeyboard(uia, binding, target);
            VerifyTargetForegroundWithRecovery(uia, binding, target);
            if (!editor.Properties.HasKeyboardFocus.ValueOrDefault)
                throw new InvalidOperationException("Editor sem foco verificado; limpeza cancelada.");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(VirtualKeyShort.BACK);
            for (var wait = 0; wait < 6; wait++)
            {
                Thread.Sleep(120);
                VerifyTargetForegroundWithRecovery(uia, binding, target);
                observed = ReadComposerValue(FindComposer(uia, hwnd));
                if (observed is not null && TextEquivalent(observed, string.Empty))
                    return ClearResult(binding, target, "keyboard", "MATCHED", "DRAFT_VERIFIED");
                if (observed is not null && IsKnownPlaceholder(observed))
                    return ClearResult(binding, target, "keyboard", "KNOWN_PLACEHOLDER",
                        "CLEARED_PLACEHOLDER_READBACK");
            }
        }

        throw new InvalidOperationException(
            $"CLEAR_READBACK_MISMATCH: limpeza não confirmada. " +
            $"Antes={before.Length}, depois={(observed is null ? "indisponível" : observed.Length.ToString())}. " +
            "Nenhuma mensagem foi enviada. Execute detectTextArea() e inspect-edge.");
    }

    private static object ClearResult(ChatBinding binding, TabMatch target, string method,
        string readback, string status) => new
    {
        status,
        targetUrl = binding.Url,
        windowHandle = target.Handle,
        characters = 0,
        textReadback = readback,
        inputMethod = method,
        sent = false
    };

    // O provedor UIA pode retornar o placeholder como se fosse conteúdo do
    // contenteditable quando o editor está vazio. Não usamos esse valor como
    // rascunho real. Para qualquer mensagem diferente, preservamos a leitura.
    // Nota: o provedor pode não distinguir placeholder de texto digitado com
    // exatamente o mesmo conteúdo; por segurança, o comando sem argumento
    // considera esses rótulos como editor vazio.
    internal static string EditorTextOrEmpty(string readback) =>
        string.IsNullOrWhiteSpace(readback) || IsKnownPlaceholder(readback)
            ? string.Empty : readback;

    // UIA TextPattern costuma incluir uma quebra de linha estrutural no fim do editor.
    // Normalizar somente para comparação e detecção de placeholder.
    public static bool TextEquivalent(string observed, string expected) =>
        NormalizeEditorText(observed) == NormalizeEditorText(expected);

    private static string NormalizeEditorText(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    // Tenta a leitura acessível sem usar Ctrl+C/clipboard ou conteúdo em cache.
    // Contenteditables do Edge podem expor TextPattern, não ValuePattern.
    private static string? ReadComposerValue(AutomationElement editor)
    {
        try
        {
            if (editor.Patterns.Value.IsSupported &&
                editor.Patterns.Value.Pattern is { } valuePattern)
            {
                var value = valuePattern.Value.ValueOrDefault;
                if (value is not null) return value;
            }
        }
        catch { /* Consulta outro padrão de acessibilidade. */ }
        try
        {
            if (editor.Patterns.Text.IsSupported &&
                editor.Patterns.Text.Pattern is { } textPattern)
                return textPattern.DocumentRange.GetText(-1);
        }
        catch { /* Provider UIA não disponibilizou o texto. */ }
        return null;
    }

    // Lê apenas o campo de entrada da conversa vinculada, não o histórico completo.
    public static string GetText(UIA3Automation uia, ChatBinding binding)
    {
        var target = FocusChat(uia, binding);
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var editor = FindComposer(uia, new IntPtr(target.Handle));
        var text = ReadComposerValue(editor);
        if (text is null)
            throw new NotSupportedException(
                "READBACK_UNSUPPORTED: o editor não expõe texto via ValuePattern ou TextPattern nesta sessão.");
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        return EditorTextOrEmpty(text);
    }

    // Resultado somente observacional, nunca prova de recebimento no servidor.
    // Um clique/invoke/Enter é executado UMA vez; após essa fronteira somente
    // leituras na mesma janela, guia e URL são permitidas.
    internal static string ClassifySendReadback(string? readback, string expectedText)
    {
        if (readback is null) return "SEND_OBSERVATION_UNAVAILABLE";
        var current = EditorTextOrEmpty(readback);
        if (TextEquivalent(current, expectedText))
            return "SEND_DRAFT_RETAINED_UNCONFIRMED";
        if (current.Length == 0)
            return "SEND_EDITOR_CLEARED_SERVER_UNCONFIRMED";
        return "SEND_EDITOR_CHANGED_UNCONFIRMED";
    }

    private static object ActivateOnceAndObserve(
        UIA3Automation uia, ChatBinding binding, TabMatch target,
        AutomationElement button, string expectedText,
        string inputSource, bool humanTextAllowed)
    {
        var hwnd = new IntPtr(target.Handle);
        var method = "button-click";
        // O envio inicial em /project já foi validado com clique físico.
        // Em /c/ o Click() pode retornar sem disparar a ação: preferir
        // InvokePattern, e usar Enter uma única vez se o padrão faltar.
        if (!ProjectNameInspector.IsProjectPage(binding.Url) &&
            button.Patterns.Invoke.IsSupported &&
            button.Patterns.Invoke.Pattern is { } invoke)
        {
            EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
            method = "uia-invoke";
            invoke.Invoke();
        }
        else if (!ProjectNameInspector.IsProjectPage(binding.Url))
        {
            // Nunca simular Enter sem provar foco e texto do MESMO editor.
            var editor = FocusComposerForKeyboard(uia, binding, target);
            VerifyTargetForegroundWithRecovery(uia, binding, target);
            var verify = ReadComposerValue(editor);
            if (verify is null || !TextEquivalent(verify, expectedText))
                throw new InvalidOperationException(
                    "SEND_ENTER_PRECONDITION_FAILED: rascunho ou guia mudaram. Nenhum Enter enviado.");
            method = "keyboard-enter";
            Keyboard.Type(VirtualKeyShort.RETURN);
        }
        else
        {
            EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
            button.Click();
        }

        string status = "SEND_OBSERVATION_UNAVAILABLE";
        var reads = 0;
        var transientErrors = 0;
        for (var i = 0; i < 14; i++)
        {
            Thread.Sleep(210);
            try
            {
                var selected = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
                if (selected is null || !SameTabIdentity(target, selected))
                {
                    status = "SEND_TARGET_CHANGED_UNCONFIRMED";
                    break;
                }
                var readback = ReadComposerValue(FindComposer(uia, hwnd));
                status = ClassifySendReadback(readback, expectedText);
                reads++;
                if (status == "SEND_EDITOR_CLEARED_SERVER_UNCONFIRMED")
                    break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                transientErrors++;
                // Após ação com efeito externo, apenas consultar novamente.
            }
            catch (InvalidOperationException)
            {
                transientErrors++;
            }
        }
        return new
        {
            status,
            targetUrl = binding.Url,
            windowHandle = target.Handle,
            tabRuntimeId = target.TabRuntimeId,
            characters = expectedText.Length,
            textReadback = "MATCHED_BEFORE_ACTION",
            dispatchAction = method,
            postActionUiReads = reads,
            postActionUiErrors = transientErrors,
            inputSource,
            humanTextAllowed,
            serverDeliveryConfirmed = false,
            note = status == "SEND_DRAFT_RETAINED_UNCONFIRMED"
                ? "O rascunho permaneceu no editor; nenhum segundo envio automático foi tentado."
                : "A ação foi tentada uma vez; confirmar recebimento na conversa antes de qualquer repetição."
        };
    }

    public static object SendPrepared(UIA3Automation uia, ChatBinding binding, string expectedText) =>
        SendPreparedCore(uia, binding, expectedText, "prepared", false);

    private static object SendPreparedCore(UIA3Automation uia, ChatBinding binding,
        string expectedText, string inputSource, bool humanTextAllowed)
    {
        if (!MessageTimestamp.HasMessageBody(expectedText))
            throw new ArgumentException("Mensagem vazia.");
        expectedText = MessageTimestamp.Apply(expectedText);
        var target = FocusChat(uia, binding);
        var hwnd = new IntPtr(target.Handle);
        var composer = FindComposer(uia, hwnd);
        var observed = ReadComposerValue(composer);
        if (observed is null || !TextEquivalent(observed, expectedText))
            throw new InvalidOperationException(
                "READBACK_NOT_VERIFIED: o editor não confirmou exatamente o rascunho. Envio cancelado.");
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var button = FindSendButton(uia, hwnd);
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        return ActivateOnceAndObserve(uia, binding, target, button,
            expectedText, inputSource, humanTextAllowed);
    }

    public static object SendCurrentEditor(UIA3Automation uia, ChatBinding binding)
    {
        var target = FocusChat(uia, binding);
        var hwnd = new IntPtr(target.Handle);
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var initial = ReadComposerValue(FindComposer(uia, hwnd));
        if (initial is null)
            throw new InvalidOperationException(
                "READBACK_UNSUPPORTED: não foi possível ler o texto atual. Envio bloqueado.");
        var original = EditorTextOrEmpty(initial);
        if (!MessageTimestamp.HasMessageBody(original))
            throw new InvalidOperationException(
                "EDITOR_EMPTY: não há texto para enviar. Envio bloqueado.");

        if (!MessageTimestamp.HasPrefix(original))
        {
            EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
            var latest = ReadComposerValue(FindComposer(uia, hwnd));
            if (latest is null || !TextEquivalent(latest, initial))
                throw new InvalidOperationException(
                    "EDITOR_CHANGED_DURING_STAMP: o texto humano mudou; nenhuma substituição realizada.");
            var stamped = MessageTimestamp.Apply(original);
            _ = SetText(uia, binding, stamped);
            return SendPreparedCore(uia, binding, stamped,
                "current-editor-stamped", true);
        }

        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var button = FindSendButton(uia, hwnd);
        EdgeBrowser.VerifyForeground(uia, binding, target.Handle);
        var confirmed = ReadComposerValue(FindComposer(uia, hwnd));
        if (confirmed is null || !TextEquivalent(confirmed, initial))
            throw new InvalidOperationException(
                "EDITOR_CHANGED_DURING_SEND: conteúdo mudou após leitura inicial. Envio bloqueado.");
        if (EditorTextOrEmpty(confirmed).Length == 0)
            throw new InvalidOperationException(
                "EDITOR_EMPTY: editor vazio. Envio bloqueado.");
        return ActivateOnceAndObserve(uia, binding, target, button,
            confirmed, "current-editor", true);
    }

    public static object SendMessage(UIA3Automation uia, ChatBinding binding, string message)
    {
        var stamped = MessageTimestamp.Apply(message);
        SetText(uia, binding, stamped);
        return SendPrepared(uia, binding, stamped);
    }

    // Compatibilidade com CLI V2: não sobrescrever rascunhos detectáveis.
    public static object SendMessage(UIA3Automation uia, string message)
    {
        var binding = CurrentBinding.Require();
        if (ChatLimitDetector.Probe(uia, binding).Detected)
            throw new InvalidOperationException(
                "CHAT_AT_MAX_LIMIT: conversa chegou à duração máxima; envio legado bloqueado.");
        var target = FocusChat(uia, binding);
        var editor = FindComposer(uia, new IntPtr(target.Handle));
        var existing = ReadComposerValue(editor);
        if (!string.IsNullOrWhiteSpace(existing))
            throw new InvalidOperationException("O editor não está vazio. Envio cancelado para preservar o rascunho da V2.");
        return SendMessage(uia, binding, message);
    }

    public static object OpenCurrentChat(UIA3Automation uia, ChatOpenMode mode, long? windowHandle = null,
        string? profileDirectory = null)
    {
        var binding = CurrentBinding.Require();
        var result = OpenChat(uia, binding, mode, windowHandle, profileDirectory);
        CurrentBinding.Set(binding with { PreferredWindowHandle = result.WindowHandle });
        return result;
    }

    private static void WaitForComposer(UIA3Automation uia, IntPtr hwnd, string expectedUrl)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (Win32.GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("O Edge perdeu o foco enquanto carregava a conversa.");
            if (EdgeBrowser.TryActiveExact(uia, hwnd, expectedUrl) is not null)
            {
                try
                {
                    var composer = FindComposer(uia, hwnd);
                    if (composer.Properties.IsEnabled.ValueOrDefault) return;
                }
                catch (InvalidOperationException) { /* Página ainda carregando. */ }
            }
            Thread.Sleep(170);
        }
        throw new InvalidOperationException("OPEN_COMPOSER_NOT_READY: URL confirmada, mas o editor " +
            "não ficou acessível por UIA. A janela permanece aberta; nenhuma tecla foi enviada. " +
            "Verifique se a conversa carregou e se a conta está autenticada.");
    }

    public static ChatOpenResult OpenChat(UIA3Automation uia, ChatBinding binding, ChatOpenMode mode,
        long? windowHandle = null, string? profileDirectory = null)
    {
        var url = binding.Url;
        if (mode == ChatOpenMode.NewWindow || EdgeBrowser.Windows().Count == 0)
        {
            if (windowHandle is not null && mode == ChatOpenMode.NewWindow)
                throw new ArgumentException("--window-hwnd não é utilizado ao criar uma janela nova.");
            var previous = EdgeBrowser.Windows().Select(h => h.ToInt64()).ToHashSet();
            var edgeExe = FindEdgeExecutable();
            var info = new ProcessStartInfo(edgeExe) { UseShellExecute = false };
            info.ArgumentList.Add("--new-window");
            var profile = profileDirectory ?? binding.ProfileDirectory;
            if (!string.IsNullOrWhiteSpace(profile))
                info.ArgumentList.Add("--profile-directory=" + ValidateProfile(profile));
            info.ArgumentList.Add(url);
            _ = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o Edge.");

            // Edge cria HWND e só depois expõe a omnibox e a URL via UIA.
            // A operação retorna sucesso apenas após evidência de URL exata.
            for (var attempt = 0; attempt < 36; attempt++)
            {
                Thread.Sleep(200);
                var created = EdgeBrowser.Windows().Where(h => !previous.Contains(h.ToInt64())).ToArray();
                foreach (var hwnd in created)
                {
                    // Algumas versões do Edge só expõem a barra de endereço
                    // completamente depois da janela receber foco.
                    try { Win32.ForegroundOrThrow(hwnd); }
                    catch (InvalidOperationException) { continue; }
                    if (EdgeBrowser.TryActiveExact(uia, hwnd, url) is { } active)
                    {
                        WaitForComposer(uia, hwnd, url);
                        return new ChatOpenResult("OPENED_VERIFIED",
                            mode == ChatOpenMode.NewWindow ? "new-window" : "new-window-fallback",
                            url, active.Handle, active.TabIndex);
                    }
                }
            }
            throw new InvalidOperationException("OPEN_UNVERIFIED: Edge recebeu a solicitação para abrir " +
                "uma nova janela, mas nenhuma janela recém-criada confirmou a URL exata via UIA. " +
                "Não atualizei o vínculo; confira list-edge-windows e inspect-edge.");
        }

        if (!string.IsNullOrWhiteSpace(profileDirectory))
            throw new ArgumentException("--profile-directory é suportado em new-window; " +
                "para new-tab escolha a janela com --window-hwnd.");
        var effectiveHandle = windowHandle;
        // Uma nova guia pertence à janela vinculada, ainda que nela haja outra
        // guia ativa. Não redirecionar silenciosamente para outra instância Edge.
        if (effectiveHandle is null && binding.PreferredWindowHandle is { } preferred &&
            EdgeBrowser.Windows().Any(h => h.ToInt64() == preferred))
            effectiveHandle = preferred;
        var hwndTab = ResolveTargetWindow(effectiveHandle, uia, binding.Url);
        Win32.ForegroundOrThrow(hwndTab);
        var tabsBefore = EdgeBrowser.CountBrowserTabs(uia, hwndTab);
        if (tabsBefore < 1)
            throw new InvalidOperationException("Sem enumeração confiável de guias; nenhuma URL foi digitada.");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        var tabCreated = false;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            Thread.Sleep(120);
            if (Win32.GetForegroundWindow() != hwndTab) break;
            if (EdgeBrowser.CountBrowserTabs(uia, hwndTab) > tabsBefore)
            {
                tabCreated = true;
                break;
            }
        }
        if (!tabCreated || Win32.GetForegroundWindow() != hwndTab)
            throw new InvalidOperationException("Nova guia não comprovada por UIA. Nenhuma URL digitada.");
        // Ctrl+L garante que Keyboard.Type jamais escreva a URL no compositor.
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
        Keyboard.Type(url);
        Keyboard.Type(VirtualKeyShort.RETURN);
        for (var attempt = 0; attempt < 35; attempt++)
        {
            Thread.Sleep(180);
            if (Win32.GetForegroundWindow() != hwndTab) break;
            if (EdgeBrowser.TryActiveExact(uia, hwndTab, url) is { } active)
            {
                WaitForComposer(uia, hwndTab, url);
                return new ChatOpenResult("OPENED_VERIFIED", "new-tab", url,
                    active.Handle, active.TabIndex);
            }
        }
        throw new InvalidOperationException("OPEN_UNVERIFIED: guia criada, mas URL exata não " +
            "confirmada via UIA. Não atualizei o vínculo; execute setFocus() após carregar a página.");
    }

    private static IntPtr ResolveTargetWindow(long? requested, UIA3Automation uia, string url)
    {
        var windows = EdgeBrowser.Windows();
        if (requested is not null)
        {
            var picked = windows.FirstOrDefault(h => h.ToInt64() == requested.Value);
            if (picked == IntPtr.Zero)
                throw new InvalidOperationException("--window-hwnd não corresponde a uma janela Edge existente.");
            return picked;
        }
        var foreground = Win32.GetForegroundWindow();
        if (windows.Contains(foreground) && EdgeBrowser.TryActiveExact(uia, foreground, url) is not null)
            return foreground;
        if (windows.Count == 1) return windows[0];
        throw new InvalidOperationException("AMBIGUOUS_EDGE_WINDOWS: janela de destino não pode ser inferida. " +
            "Use setFocus() no chat desejado ou informe --window-hwnd para open(Mode.NewTab).");
    }

    private static string FindEdgeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("msedge.exe não encontrado nos caminhos padrão.");
    }

    private static string ValidateProfile(string profile)
    {
        if (profile.Length is < 1 or > 80 || profile.Any(c =>
            !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '_' or '-')))
            throw new ArgumentException("Identificador de perfil inválido.");
        return profile;
    }
}
