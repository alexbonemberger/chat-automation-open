using System.Globalization;
using System.Text;
using ChatAutomation.Browser;
using ChatAutomation.State;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ChatAutomation.Features;

public sealed record MaxLimitEvidence(
    bool Detected,
    bool WarningFound,
    bool NewChatButtonFound,
    int InspectedElements,
    long WindowHandle,
    string? TabRuntimeId,
    string TargetUrl);

// Observador PASSIVO: não seleciona outras guias, não clica no CTA e
// não depende do nome da aba. Busca aviso e CTA na árvore UIA do documento.
public static class ChatLimitDetector
{
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(ch));
        }
        return string.Join(" ", result.ToString().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Não aceitar "limite" isolado: usuários também podem escrever sobre
    // limites nas mensagens. Texto indicativo da mensagem de bloqueio real.
    public static bool IsMaxLimitWarning(string? text)
    {
        var s = Normalize(text);
        return s.Contains("duracao maxima desta conversa", StringComparison.Ordinal)
            || s.Contains("limite maximo desta conversa", StringComparison.Ordinal)
            || s.Contains("maximum length for this conversation", StringComparison.Ordinal)
            || s.Contains("maximum length of this conversation", StringComparison.Ordinal)
            || s.Contains("conversation has reached its maximum length", StringComparison.Ordinal)
            || s.Contains("reached the maximum conversation length", StringComparison.Ordinal);
    }

    public static bool IsNewChatAction(string? label)
    {
        var s = Normalize(label);
        return s is "iniciar novo chat" or "start new chat" or
            "start a new chat" or "begin new chat";
    }

    internal static bool EvidenceConfirmed(bool warning, bool button) => warning && button;

    private static bool InsidePageDocument(AutomationElement element)
    {
        try
        {
            for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.ControlType == ControlType.Document) return true;
                if (parent.ControlType == ControlType.Window) return false;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }

    // Layout Edge/UIA achatado: aviso e CTA podem ser irmãos de
    // Document:ChatGPT. Exigir ambos, UM botão e UM documento único,
    // nunca inferir limite por texto solto ou CTA genérico.
    internal static bool CanConfirmDetachedMaxLimit(bool persistentChatUrl,
        int visibleChatGptDocuments, bool warningFound, int enabledActions) =>
        persistentChatUrl && visibleChatGptDocuments == 1 &&
        warningFound && enabledActions == 1;

    public static MaxLimitEvidence Probe(UIA3Automation uia, ChatBinding binding)
    {
        var selected = EdgeBrowser.ResolvePassiveExact(uia, binding);
        var hwnd = new IntPtr(selected.Handle);
        var root = EdgeBrowser.FromHandle(uia, hwnd);
        var controls = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Text)
                .Or(cf.ByControlType(ControlType.Group))
                .Or(cf.ByControlType(ControlType.Button)));
        if (controls.Length == 0)
            throw new InvalidOperationException(
                "MAX_LIMIT_UIA_NOT_READY: controles da página não estão acessíveis.");

        var warning = false;
        var action = false;
        var pageElements = 0;
        var detachedWarning = false;
        var detachedActionCount = 0;
        foreach (var control in controls)
        {
            try
            {
                if (control.Properties.IsOffscreen.ValueOrDefault) continue;

                var insideDocument = InsidePageDocument(control);
                var name = control.Properties.Name.ValueOrDefault;
                var isAction = control.ControlType == ControlType.Button &&
                    control.Properties.IsEnabled.ValueOrDefault &&
                    IsNewChatAction(name);
                var isWarning = control.ControlType != ControlType.Button &&
                    (IsMaxLimitWarning(name) ||
                     IsMaxLimitWarning(control.Properties.HelpText.ValueOrDefault));

                if (insideDocument)
                {
                    pageElements++;
                    if (isAction) action = true;
                    if (isWarning) warning = true;
                }
                else
                {
                    if (isAction) detachedActionCount++;
                    if (isWarning) detachedWarning = true;
                }
                if (EvidenceConfirmed(warning, action)) break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Elemento descartado pelo Edge entre enumeração e leitura.
                // Sem as duas evidências, o resultado não será positivo.
            }
        }

        if (!EvidenceConfirmed(warning, action) &&
            (detachedWarning || detachedActionCount > 0))
        {
            var docCount = root.FindAllDescendants(
                cf => cf.ByControlType(ControlType.Document))
                .Count(doc =>
                {
                    try { return !doc.Properties.IsOffscreen.ValueOrDefault &&
                        string.Equals(doc.Properties.Name.ValueOrDefault,
                            "ChatGPT", StringComparison.OrdinalIgnoreCase); }
                    catch (System.Runtime.InteropServices.COMException)
                    { return false; }
                });
            // A URL exata da GUIA continua comprovada por
            // ResolvePassiveExact e TryActiveExact; aqui só permitimos
            // conversas persistentes, nunca a rota /project.
            var validConversation = ChatUrlScope.IsPersistentConversation(
                    binding.Url, null) ||
                ChatFeatures.IsSupportedComposerPageUrl(binding.Url) &&
                !ProjectNameInspector.IsProjectPage(binding.Url);
            if (CanConfirmDetachedMaxLimit(validConversation, docCount,
                warning || detachedWarning,
                (action ? 1 : 0) + detachedActionCount))
            {
                warning = true;
                action = true;
            }
        }

        if (pageElements == 0 && !EvidenceConfirmed(warning, action))
            throw new InvalidOperationException(
                "MAX_LIMIT_UIA_NOT_READY: documento da conversa não expôs " +
                "controles visíveis nem aviso/CTA externos comprovados.");

        var verified = EdgeBrowser.TryActiveExact(uia, hwnd, binding.Url);
        if (verified is null ||
            verified.TabIndex != selected.TabIndex ||
            !string.Equals(verified.TabRuntimeId, selected.TabRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MAX_LIMIT_TARGET_CHANGED: URL/guia mudou durante a observação; estado não atualizado.");

        return new MaxLimitEvidence(EvidenceConfirmed(warning, action), warning, action,
            controls.Length, selected.Handle, selected.TabRuntimeId, binding.Url);
    }
}
