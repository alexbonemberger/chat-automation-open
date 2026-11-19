using ChatAutomation.Infrastructure;
using ChatAutomation.State;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ChatAutomation.Browser;

public sealed record TabMatch(long Handle, int TabIndex, string Url, string WindowTitle, string? TabRuntimeId = null);
public sealed record ScanReport(IReadOnlyList<TabMatch> Matches, IReadOnlyList<string> Warnings);
public sealed record SelectedTabState(long WindowHandle, int TabIndex, string RuntimeId);

public static class EdgeBrowser
{
    public static IReadOnlyList<IntPtr> Windows() => Win32.ListEdgeWindows();

    public static IntPtr GetForegroundEdge()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (!Win32.IsEdgeWindow(hwnd))
            throw new InvalidOperationException("A janela em primeiro plano não é o Microsoft Edge.");
        return hwnd;
    }

    // Comandos de leitura não dependem de o Edge estar em primeiro plano.
    // Com múltiplas janelas, nunca escolhe uma arbitrariamente.
    public static IntPtr ResolveReadableEdgeWindow(long? requestedHandle = null)
    {
        var windows = Windows();
        if (requestedHandle is not null)
        {
            var selected = windows.FirstOrDefault(h => h.ToInt64() == requestedHandle.Value);
            if (selected == IntPtr.Zero)
                throw new InvalidOperationException(
                    "--window-hwnd não corresponde a uma janela visível do Microsoft Edge.");
            return selected;
        }

        var foreground = Win32.GetForegroundWindow();
        if (windows.Contains(foreground)) return foreground;
        if (windows.Count == 1) return windows[0];
        if (windows.Count == 0)
            throw new InvalidOperationException("Nenhuma janela visível do Microsoft Edge foi encontrada.");
        throw new InvalidOperationException(
            "Há múltiplas janelas Edge e o primeiro plano não é Edge. " +
            "Informe --window-hwnd (consulte list-edge-windows) para evitar seleção arbitrária.");
    }

    public static AutomationElement FromHandle(UIA3Automation uia, IntPtr hwnd) =>
        uia.FromHandle(hwnd) ?? throw new InvalidOperationException("Janela Edge inacessível por UIA3.");

    public static string ReadActiveUrl(UIA3Automation uia, IntPtr hwnd)
    {
        var root = FromHandle(uia, hwnd);
        // A barra do Edge pode ser exposta como Edit ou ComboBox dependendo da versão.
        // Primeiro identifica pelo nome/ID; nunca usa controles internos da página.
        var elements = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.ComboBox)));
        var chrome = elements.Where(e => !IsInsideDocument(e)).ToArray();
        var named = chrome.Where(IsAddressBox).ToArray();
        // Edit e ComboBox podem representar o MESMO controle; só permite
        // candidatos cuja leitura efetivamente forneça uma URL e compara os valores.
        var candidates = named.Select(ReadValue).Where(v => UrlIdentity.TryNormalize(v, out _))
            .ToArray();
        if (candidates.Length == 0)
            candidates = chrome.Select(ReadValue).Where(v => UrlIdentity.TryNormalize(v, out _))
                .ToArray();
        var urls = candidates.Select(v => UrlIdentity.TryNormalize(v, out var parsed) ? parsed : "")
            .Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length != 1)
            throw new InvalidOperationException(
                $"Barra de endereço não identificada de forma única (candidatos={candidates.Length}, urls={urls.Length}). Execute inspect-edge.");
        return urls[0];
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.Pattern?.Value.ValueOrDefault ?? ""; }
        catch { return ""; }
    }

    private static bool IsAddressBox(AutomationElement element)
    {
        var id = element.Properties.AutomationId.ValueOrDefault ?? "";
        var name = element.Properties.Name.ValueOrDefault ?? "";
        var help = element.Properties.HelpText.ValueOrDefault ?? "";
        return id.Contains("address", StringComparison.OrdinalIgnoreCase)
            || id.Contains("omnibox", StringComparison.OrdinalIgnoreCase)
            || id.Equals("urlbar", StringComparison.OrdinalIgnoreCase)
            || name.Contains("address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("endereç", StringComparison.OrdinalIgnoreCase)
            || name.Contains("omnibox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("web address", StringComparison.OrdinalIgnoreCase)
            || help.Contains("address", StringComparison.OrdinalIgnoreCase)
            || help.Contains("endereç", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideDocument(AutomationElement element)
    {
        // Não confundir abas e campos do conteúdo HTML com o chrome do navegador.
        var parent = element;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                parent = parent.Parent;
                if (parent is null) return false;
                if (parent.ControlType == ControlType.Document) return true;
                if (parent.ControlType == ControlType.Window) return false;
            }
            catch { return false; }
        }
        return false;
    }

    // RuntimeId é uma identidade da guia durante a vida do elemento UIA.
    // Índices/nomes/URLs não identificam uma aba quando há duplicatas.
    private static string? TabRuntimeId(AutomationElement element)
    {
        try
        {
            var ids = element.Properties.RuntimeId.ValueOrDefault;
            return ids is { Length: > 0 } ? string.Join(".", ids) : null;
        }
        catch { return null; }
    }

    // Regra pura para seleção determinística da guia; usada também pelo
    // self-test sem depender de Windows/UIA.
    internal static int PinnedTabIndex(IReadOnlyList<string?> runtimeIds, string pinnedRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(pinnedRuntimeId))
            throw new InvalidOperationException("TAB_IDENTITY_UNAVAILABLE: pin ausente.");
        var found = -1;
        for (var i = 0; i < runtimeIds.Count; i++)
        {
            if (!string.Equals(runtimeIds[i], pinnedRuntimeId, StringComparison.Ordinal))
                continue;
            if (found >= 0)
                throw new InvalidOperationException(
                    "AMBIGUOUS_TAB_RUNTIME_ID: mais de uma guia corresponde à identidade fixada.");
            found = i;
        }
        if (found < 0)
            throw new InvalidOperationException(
                "PINNED_TAB_NOT_FOUND: guia original ausente. Outra cópia não será escolhida.");
        return found;
    }

    // Leitura da guia ativa sem depender de URL (útil antes que o ChatGPT
    // atribua a URL de uma nova conversa). RuntimeId é obrigatório.
    public static SelectedTabState SelectedTab(UIA3Automation uia, IntPtr hwnd)
    {
        var tabs = BrowserTabs(FromHandle(uia, hwnd));
        var indexes = Enumerable.Range(0, tabs.Count).Where(i => IsSelected(tabs[i])).ToArray();
        if (indexes.Length != 1)
            throw new InvalidOperationException("NEW_CHAT_TAB_AMBIGUOUS: guia selecionada não é única.");
        var runtime = TabRuntimeId(tabs[indexes[0]]);
        if (string.IsNullOrWhiteSpace(runtime))
            throw new InvalidOperationException("NEW_CHAT_TAB_RUNTIME_ID_MISSING");
        return new SelectedTabState(hwnd.ToInt64(), indexes[0], runtime);
    }

    // Usa apenas a identidade UIA capturada ao abrir a guia, mesmo se
    // a URL tiver mudado após o primeiro envio. Não escolhe por título/URL.
    public static TabMatch ActivateTabAndReadCurrentUrl(UIA3Automation uia,
        long windowHandle, string runtimeId)
    {
        var hwnd = new IntPtr(windowHandle);
        if (!Windows().Contains(hwnd))
            throw new InvalidOperationException("NEW_CHAT_WINDOW_CLOSED");
        var tabs = BrowserTabs(FromHandle(uia, hwnd));
        var index = PinnedTabIndex(tabs.Select(TabRuntimeId).ToArray(), runtimeId);
        Win32.ForegroundOrThrow(hwnd);
        if (!IsSelected(tabs[index])) SelectTab(tabs[index]);
        var selected = SelectedTab(uia, hwnd);
        if (!string.Equals(selected.RuntimeId, runtimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("NEW_CHAT_TAB_CHANGED: RuntimeId divergiu.");
        var url = ReadActiveUrl(uia, hwnd);
        var verified = TryActiveExact(uia, hwnd, url);
        if (verified is null || verified.TabRuntimeId != runtimeId)
            throw new InvalidOperationException("NEW_CHAT_TAB_CHANGED: URL/RuntimeId instável.");
        return verified;
    }

    // Retoma SOMENTE a guia fixada por RuntimeId. Quando a navegação muda
    // a guia selecionada, selecionar o pin é seguro; escolher pelo índice ou
    // pela URL do projeto não é. Nenhuma tecla ou clique de página é enviado.
    public static SelectedTabState FocusPinnedNewChatTab(UIA3Automation uia,
        long windowHandle, string runtimeId)
    {
        var hwnd = new IntPtr(windowHandle);
        if (!Windows().Contains(hwnd))
            throw new InvalidOperationException("NEW_CHAT_WINDOW_CLOSED");
        Win32.ForegroundOrThrow(hwnd);
        // O Edge pode reconstruir temporariamente a árvore UIA durante
        // navegação. Reconsultar o MESMO RuntimeId por um intervalo limitado
        // é seguro; nunca selecionamos por índice ou por URL.
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var tabs = BrowserTabs(FromHandle(uia, hwnd));
            int index;
            try { index = PinnedTabIndex(tabs.Select(TabRuntimeId).ToArray(), runtimeId); }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("PINNED_TAB_NOT_FOUND", StringComparison.Ordinal))
            {
                Thread.Sleep(140);
                continue;
            }
            if (!IsSelected(tabs[index])) SelectTab(tabs[index]);
            try
            {
                var selected = SelectedTab(uia, hwnd);
                if (string.Equals(selected.RuntimeId, runtimeId, StringComparison.Ordinal))
                    return selected;
            }
            catch (InvalidOperationException) { /* UIA ainda em transição. */ }
            Thread.Sleep(140);
        }
        throw new InvalidOperationException(
            "NEW_CHAT_TAB_REFOCUS_UNVERIFIED: a guia fixada não confirmou " +
            "seleção pela identidade UIA após tentativas limitadas. " +
            InspectNewChatPin(uia, windowHandle, runtimeId));
    }

    // Diagnóstico passivo para RuntimeId divergente. O índice aparece apenas
    // como evidência, NUNCA como seletor alternativo da guia.
    // Verifica a ausência do pin em TODAS as janelas Edge inspecionáveis.
    // A ausência do RuntimeId não prova fechamento: uma recriação do
    // elemento UIA pode mudar essa identidade. Por isso esta verificação
    // somente habilita um comando EXPLÍCITO de reabertura, nunca um retry.
    public static bool IsNewChatPinAbsent(UIA3Automation uia, long originalHandle,
        string runtimeId)
    {
        if (originalHandle <= 0 || string.IsNullOrWhiteSpace(runtimeId))
            throw new ArgumentException("NEW_CHAT_PIN_INVALID");
        var windows = Windows();
        if (windows.Count == 0)
            throw new InvalidOperationException(
                "NEW_CHAT_EDGE_UNAVAILABLE: abra uma janela Edge antes da reabertura.");
        foreach (var hwnd in windows)
        {
            // Se uma janela não puder ser inspecionada, não afirmar ausência.
            IReadOnlyList<AutomationElement> tabs;
            try { tabs = BrowserTabs(FromHandle(uia, hwnd)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "NEW_CHAT_PIN_AUDIT_INCOMPLETE: janela Edge inacessível " +
                    "por UIA; reabertura bloqueada.", ex);
            }
            if (tabs.Count == 0)
                throw new InvalidOperationException(
                    "NEW_CHAT_PIN_AUDIT_INCOMPLETE: janela Edge sem guias UIA enumeráveis.");
            foreach (var tab in tabs)
            {
                var observedRuntime = TabRuntimeId(tab);
                if (string.IsNullOrWhiteSpace(observedRuntime))
                    throw new InvalidOperationException(
                        "NEW_CHAT_PIN_AUDIT_INCOMPLETE: aba sem RuntimeId, " +
                        "não é seguro confirmar ausência do pin.");
                if (string.Equals(observedRuntime, runtimeId, StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    public static string InspectNewChatPin(UIA3Automation uia,
        long windowHandle, string runtimeId)
    {
        var hwnd = new IntPtr(windowHandle);
        if (!Windows().Contains(hwnd)) return "windowClosed=true";
        try
        {
            var tabs = BrowserTabs(FromHandle(uia, hwnd));
            var selected = tabs.Select((tab, i) => new
                { Index = i, Selected = IsSelected(tab), RuntimeId = TabRuntimeId(tab) })
                .Where(t => t.Selected).ToArray();
            var pinned = tabs.Select(TabRuntimeId)
                .Count(id => string.Equals(id, runtimeId, StringComparison.Ordinal));
            var selection = selected.Length == 1
                ? selected[0].RuntimeId ?? "null" : "ambiguous";
            return "tabCount=" + tabs.Count +
                "; pinnedMatches=" + pinned +
                "; selectedCount=" + selected.Length +
                "; selectedRuntimeId=" + selection +
                "; expectedRuntimeId=" + runtimeId +
                "; foreground=" + (Win32.GetForegroundWindow() == hwnd);
        }
        catch (Exception ex)
        {
            return "inspectionError=" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    public static int CountBrowserTabs(UIA3Automation uia, IntPtr hwnd) =>
        BrowserTabs(FromHandle(uia, hwnd)).Count;

    private static IReadOnlyList<AutomationElement> BrowserTabs(AutomationElement root)
    {
        return root.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .Where(e => !IsInsideDocument(e))
            .Where(e => e.Patterns.SelectionItem.IsSupported)
            .ToArray();
    }

    private static bool IsSelected(AutomationElement tab)
    {
        try { return tab.Patterns.SelectionItem.Pattern?.IsSelected.ValueOrDefault == true; }
        catch { return false; }
    }

    private static void SelectTab(AutomationElement tab)
    {
        var pattern = tab.Patterns.SelectionItem.Pattern
            ?? throw new InvalidOperationException("TabItem não suporta SelectionItem.");
        pattern.Select();
        Thread.Sleep(160);
    }

    public static ScanReport ScanExact(UIA3Automation uia, string url, long? preferredHandle = null)
    {
        var matches = new List<TabMatch>();
        var warnings = new List<string>();
        var windows = Windows().OrderBy(h => preferredHandle == h.ToInt64() ? 0 : 1).ToList();
        foreach (var hwnd in windows)
        {
            try
            {
                var root = FromHandle(uia, hwnd);
                var tabs = BrowserTabs(root);
                var selected = tabs.FirstOrDefault(IsSelected);
                // Se a UIA não expõe as guias, ainda testa a guia ativa.
                if (tabs.Count == 0)
                {
                    var active = ReadActiveUrl(uia, hwnd);
                    if (UrlIdentity.Exact(active, url))
                        matches.Add(new TabMatch(hwnd.ToInt64(), -1, active, Win32.Title(hwnd)));
                    continue;
                }
                if (selected is null)
                {
                    warnings.Add($"HWND={hwnd.ToInt64()}: guia originalmente selecionada não identificada. Inspeção de outras guias cancelada.");
                    var active = ReadActiveUrl(uia, hwnd);
                    if (UrlIdentity.Exact(active, url))
                        matches.Add(new TabMatch(hwnd.ToInt64(), -1, active, Win32.Title(hwnd)));
                    continue;
                }
                try
                {
                    for (var index = 0; index < tabs.Count; index++)
                    {
                        var tab = tabs[index];
                        try
                        {
                            if (!IsSelected(tab)) SelectTab(tab);
                            var observed = ReadActiveUrl(uia, hwnd);
                            if (UrlIdentity.Exact(observed, url))
                                matches.Add(new TabMatch(hwnd.ToInt64(), index, observed, Win32.Title(hwnd), TabRuntimeId(tab)));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"HWND={hwnd.ToInt64()}, tab={index}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    // Desfaz a inspeção, mesmo que alguma guia tenha falhado.
                    if (selected is not null && !IsSelected(selected))
                    {
                        try { SelectTab(selected); }
                        catch (Exception ex) { warnings.Add($"Restauração HWND={hwnd.ToInt64()}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"HWND={hwnd.ToInt64()}: {ex.Message}");
            }
        }
        return new ScanReport(matches, warnings);
    }

    // Regra pura e testável para duplicatas: pin exato > seleção explícita no
    // Edge > ocorrência única > preferência antiga de janela > falha fechada.
    internal static TabMatch ChooseExactMatch(IReadOnlyList<TabMatch> matches,
        ChatBinding binding, long foregroundHandle, TabMatch? selectedForeground)
    {
        if (matches.Count == 0)
            throw new InvalidOperationException("A URL não foi encontrada nas guias inspecionadas.");
        if (binding.PreferredWindowHandle is { } hwnd &&
            !string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId))
        {
            var pinned = matches.Where(m => m.Handle == hwnd &&
                string.Equals(m.TabRuntimeId, binding.PreferredTabRuntimeId, StringComparison.Ordinal))
                .ToArray();
            if (pinned.Length == 1) return pinned[0];
        }
        if (selectedForeground is not null && selectedForeground.Handle == foregroundHandle &&
            matches.Any(m => m == selectedForeground))
            return selectedForeground;
        if (matches.Count == 1) return matches[0];
        // Compatibilidade com registros V6.4: HWND antigo só escolhe quando
        // identifica UMA guia, nunca "a primeira" entre várias.
        if (string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId) &&
            binding.PreferredWindowHandle is { } legacyHandle)
        {
            var legacy = matches.Where(m => m.Handle == legacyHandle).ToArray();
            if (legacy.Length == 1) return legacy[0];
        }
        throw new InvalidOperationException(
            $"AMBIGUOUS_CHAT_TABS: URL presente em {matches.Count} guias. " +
            "Selecione explicitamente a guia desejada no Edge e execute setFocus(). " +
            "Não será escolhida uma guia aleatória.");
    }

    private static TabMatch ResolveMatch(UIA3Automation uia, IReadOnlyList<TabMatch> matches,
        ChatBinding binding)
    {
        var foreground = Win32.GetForegroundWindow().ToInt64();
        var selected = SelectedAmong(uia, foreground, matches);
        return ChooseExactMatch(matches, binding, foreground, selected);
    }

    private static TabMatch? SelectedAmong(UIA3Automation uia, long handle,
        IReadOnlyList<TabMatch> matches)
    {
        var candidates = matches.Where(x => x.Handle == handle).ToArray();
        if (candidates.Length == 0) return null;
        try
        {
            var hwnd = new IntPtr(handle);
            var tabs = BrowserTabs(FromHandle(uia, hwnd));
            if (tabs.Count == 0)
                return candidates.Length == 1 && UrlIdentity.Exact(ReadActiveUrl(uia, hwnd), candidates[0].Url)
                    ? candidates[0] : null;
            var selectedIndex = -1;
            for (var i = 0; i < tabs.Count; i++)
                if (IsSelected(tabs[i]))
                {
                    selectedIndex = i;
                    break;
                }
            return candidates.FirstOrDefault(x => x.TabIndex == selectedIndex);
        }
        catch { return null; }
    }

    // Lê somente o TabItem SELECIONADO, nunca o título da janela Edge:
    // a janela pode ter várias guias e sufixos do perfil do navegador.
    // O TabItem precisa coincidir com HWND, RuntimeId e URL exata.
    // Não muda foco nem escolhe a guia pelo texto do título.
    public static string? ReadSelectedTabTitle(UIA3Automation uia, ChatBinding binding)
    {
        var selected = ResolvePassiveExact(uia, binding);
        if (string.IsNullOrWhiteSpace(selected.TabRuntimeId))
            throw new InvalidOperationException(
                "CHAT_TITLE_RUNTIME_ID_REQUIRED: guia sem identidade UIA.");
        var hwnd = new IntPtr(selected.Handle);
        var tabs = BrowserTabs(FromHandle(uia, hwnd));
        var index = PinnedTabIndex(tabs.Select(TabRuntimeId).ToArray(),
            selected.TabRuntimeId);
        if (!IsSelected(tabs[index]) || selected.TabIndex != index)
            throw new InvalidOperationException(
                "CHAT_TITLE_TAB_CHANGED: guia não está selecionada.");
        var title = tabs[index].Properties.Name.ValueOrDefault;
        var verified = TryActiveExact(uia, hwnd, binding.Url);
        if (verified is null || verified.TabIndex != index ||
            !string.Equals(verified.TabRuntimeId,
                selected.TabRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "CHAT_TITLE_TARGET_CHANGED: URL ou RuntimeId alterado.");
        return title?.Trim();
    }

    // Via rápida: se a guia JÁ está ativa e sua URL está comprovada, não percorre
    // outras janelas — inclusive janelas ainda carregando, com address bar ausente.
    public static TabMatch? TryActiveExact(UIA3Automation uia, IntPtr hwnd, string url)
    {
        if (!Windows().Contains(hwnd)) return null;
        try
        {
            if (!UrlIdentity.Exact(ReadActiveUrl(uia, hwnd), url)) return null;
            var tabs = BrowserTabs(FromHandle(uia, hwnd));
            var selected = -1;
            if (tabs.Count > 0)
            {
                selected = -1;
                for (var i = 0; i < tabs.Count; i++)
                    if (IsSelected(tabs[i])) { selected = i; break; }
                if (selected < 0) return null; // Sem evidência de qual guia está ativa.
            }
            return new TabMatch(hwnd.ToInt64(), selected, url, Win32.Title(hwnd),
                selected >= 0 ? TabRuntimeId(tabs[selected]) : null);
        }
        catch { return null; }
    }

    // Reativa exclusivamente a guia já identificada. Nunca seleciona pela
    // URL ou pelo índice quando existe RuntimeId: cópias idênticas não são
    // intercambiáveis. Chamado pelas operações que precisam do teclado.
    public static TabMatch ActivateExactTab(UIA3Automation uia, TabMatch target)
    {
        var hwnd = new IntPtr(target.Handle);
        if (!Windows().Contains(hwnd))
            throw new InvalidOperationException(
                "TARGET_WINDOW_CLOSED: janela original não existe mais; revalide o vínculo.");

        var tabs = BrowserTabs(FromHandle(uia, hwnd));
        if (!string.IsNullOrWhiteSpace(target.TabRuntimeId))
        {
            var index = PinnedTabIndex(tabs.Select(TabRuntimeId).ToArray(), target.TabRuntimeId);

            Win32.ForegroundOrThrow(hwnd);
            if (!IsSelected(tabs[index])) SelectTab(tabs[index]);
        }
        else
        {
            // Sem RuntimeId não é seguro mudar de guia por índice.
            var selected = TryActiveExact(uia, hwnd, target.Url);
            if (selected is null || selected.TabIndex != target.TabIndex)
                throw new InvalidOperationException(
                    "TAB_IDENTITY_UNAVAILABLE: guia sem RuntimeId não pode ser " +
                    "reativada automaticamente quando outra guia está selecionada.");
            Win32.ForegroundOrThrow(hwnd);
        }

        var verified = TryActiveExact(uia, hwnd, target.Url);
        if (verified is null ||
            (!string.IsNullOrWhiteSpace(target.TabRuntimeId) &&
             !string.Equals(verified.TabRuntimeId, target.TabRuntimeId, StringComparison.Ordinal)) ||
            (string.IsNullOrWhiteSpace(target.TabRuntimeId) && verified.TabIndex != target.TabIndex))
            throw new InvalidOperationException(
                "TARGET_TAB_CHANGED: a URL/identidade da guia não confirmou após o foco.");
        return verified;
    }

    public static TabMatch FocusExact(UIA3Automation uia, ChatBinding binding)
    {
        var available = Windows();
        // O pin tem precedência, MESMO que outra guia esteja ativa no Edge.
        // Se a janela existe mas o RuntimeId desapareceu, não "adota"
        // silenciosamente outra cópia da URL. Exige novo vínculo explícito.
        if (binding.PreferredWindowHandle is { } pinHwnd &&
            !string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId) &&
            available.Any(h => h.ToInt64() == pinHwnd))
        {
            var hwnd = new IntPtr(pinHwnd);
            var tabs = BrowserTabs(FromHandle(uia, hwnd));
            var pinnedIndex = PinnedTabIndex(tabs.Select(TabRuntimeId).ToArray(),
                binding.PreferredTabRuntimeId);

            return ActivateExactTab(uia, new TabMatch(pinHwnd, pinnedIndex,
                binding.Url, Win32.Title(hwnd), binding.PreferredTabRuntimeId));
        }
        // Seleção explícita pelo usuário no Edge: prefere a guia efetivamente
        // ativa, não a primeira janela enumerada pelo Win32.
        var foreground = Win32.GetForegroundWindow();
        if (available.Contains(foreground) &&
            TryActiveExact(uia, foreground, binding.Url) is { } selected)
        {
            Win32.ForegroundOrThrow(foreground);
            return TryActiveExact(uia, foreground, binding.Url) ??
                throw new InvalidOperationException("Guia escolhida mudou durante a validação.");
        }
        var result = ScanExact(uia, binding.Url, binding.PreferredWindowHandle);
        if (result.Matches.Count == 0)
            throw new InvalidOperationException("URL exata não encontrada nas guias inspecionáveis do Edge. " +
                string.Join(" | ", result.Warnings.Take(5)));
        var match = ResolveMatch(uia, result.Matches, binding);
        var hwndChosen = new IntPtr(match.Handle);
        Win32.ForegroundOrThrow(hwndChosen);
        if (match.TabIndex >= 0)
        {
            var tabs = BrowserTabs(FromHandle(uia, hwndChosen));
            // Índice pode mudar durante o scan; localiza pelo RuntimeId quando há pin.
            var tab = !string.IsNullOrWhiteSpace(match.TabRuntimeId)
                ? tabs.FirstOrDefault(t => TabRuntimeId(t) == match.TabRuntimeId)
                : (match.TabIndex < tabs.Count ? tabs[match.TabIndex] : null);
            if (tab is null)
                throw new InvalidOperationException("TARGET_TAB_CHANGED: guia mudou durante a operação.");
            SelectTab(tab);
        }
        var final = TryActiveExact(uia, hwndChosen, binding.Url)
            ?? throw new InvalidOperationException("URL ou seleção da guia mudou após o foco.");
        if (!string.IsNullOrWhiteSpace(match.TabRuntimeId) &&
            final.TabRuntimeId != match.TabRuntimeId)
            throw new InvalidOperationException("TARGET_TAB_CHANGED: RuntimeId mudou após o foco.");
        return final;
    }

    // Consulta passiva: NÃO troca o foco nem percorre guias inativas.
    // Uma guia pinned inativa retorna erro verificável, nunca a cópia vizinha.
    public static TabMatch ResolvePassiveExact(UIA3Automation uia, ChatBinding binding)
    {
        var windows = Windows();
        if (binding.PreferredWindowHandle is { } preferred &&
            !string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId) &&
            windows.Any(h => h.ToInt64() == preferred))
        {
            var active = TryActiveExact(uia, new IntPtr(preferred), binding.Url);
            if (active is not null && active.TabRuntimeId == binding.PreferredTabRuntimeId)
                return active;
            throw new InvalidOperationException(
                "BUSY_TAB_NOT_ACTIVE: guia fixada está em segundo plano ou foi substituída. " +
                "Use setFocus() antes de iniciar o monitor.");
        }
        var candidates = windows.Select(h => TryActiveExact(uia, h, binding.Url))
            .Where(m => m is not null).Cast<TabMatch>().ToArray();
        var foreground = Win32.GetForegroundWindow().ToInt64();
        var foregroundMatches = candidates.Where(c => c.Handle == foreground).ToArray();
        if (foregroundMatches.Length == 1) return foregroundMatches[0];
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length == 0)
            throw new InvalidOperationException("BUSY_TAB_NOT_VISIBLE: a conversa não está ativa em nenhuma janela Edge. " +
                "Use setFocus() antes de monitorar.");
        throw new InvalidOperationException("AMBIGUOUS_CHAT_TABS: várias instâncias ativas da mesma URL. " +
            "Use setFocus() para fixar uma janela e guia específicas.");
    }

    public static void VerifyForeground(UIA3Automation uia, ChatBinding binding, long hwndValue)
    {
        var hwnd = new IntPtr(hwndValue);
        if (Win32.GetForegroundWindow() != hwnd ||
            !UrlIdentity.Exact(ReadActiveUrl(uia, hwnd), binding.Url))
            throw new InvalidOperationException("Foco ou URL divergiu do vínculo; operação cancelada.");

        // Cópias da mesma URL dentro da MESMA janela não são intercambiáveis:
        // para vínculos pinados, a guia selecionada deve manter o RuntimeId.
        // Essencial antes de digitar/clicar; URL+HWND não bastam para envio.
        if (binding.PreferredWindowHandle == hwndValue &&
            !string.IsNullOrWhiteSpace(binding.PreferredTabRuntimeId))
        {
            var active = TryActiveExact(uia, hwnd, binding.Url);
            if (active is null || !string.Equals(active.TabRuntimeId,
                    binding.PreferredTabRuntimeId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "PINNED_TAB_MISMATCH: a guia selecionada mudou. " +
                    "Nenhuma interação de teclado/envio é permitida nesta cópia.");
        }
    }

    public static IReadOnlyList<object> Inspect(UIA3Automation uia, long? windowHandle = null)
    {
        var hwnd = ResolveReadableEdgeWindow(windowHandle);
        var root = FromHandle(uia, hwnd);
        var items = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.ComboBox))
                .Or(cf.ByControlType(ControlType.TabItem)));
        return items.Take(120).Select(e => (object)new
        {
            controlType = e.ControlType.ToString(),
            name = e.Properties.Name.ValueOrDefault,
            automationId = e.Properties.AutomationId.ValueOrDefault,
            helpText = e.Properties.HelpText.ValueOrDefault,
            hasKeyboardFocus = e.Properties.HasKeyboardFocus.ValueOrDefault,
            value = e.ControlType is ControlType.Edit or ControlType.ComboBox ? ReadValue(e) : null,
            insideDocument = IsInsideDocument(e)
        }).ToArray();
    }
}
