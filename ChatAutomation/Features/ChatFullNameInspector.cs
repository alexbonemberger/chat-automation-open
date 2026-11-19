using ChatAutomation.Browser;
using ChatAutomation.State;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Text.RegularExpressions;

namespace ChatAutomation.Features;

// O título exibido pelo ChatGPT é metadado, não a identidade do chat.
// O identificador GPTBOT_XXXX continua sendo a chave estável.
public sealed record ChatFullNamePreview(
    string Status,
    string? FullName,
    string? BrowserTabTitle,
    int MatchingPageElements,
    string TargetUrl,
    string TabRuntimeId,
    string Note);

public static class ChatFullNameInspector
{
    // Não derivar do texto da janela Edge ("mais 1 página", perfil, etc.).
    // O TabItem da guia exata costuma corresponder ao título do chat.
    // Acessibilidade do Edge acrescenta telemetria ao nome do TabItem:
    // "Continuidade de sistemas - Uso de memória - 597 MB".
    // A extração é ancorada no FIM da string, para não apagar o título real.
    private static readonly Regex EdgeMemorySuffix = new(
        @"(?:\s+|^)-\s+(?:Uso de memória|Uso da memória|Memory usage|Memory use)\s+-\s+\d+(?:[.,]\d+)?\s*(?:KB|MB|GB)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? NormalizeTabTitle(string? tabTitle)
    {
        var title = tabTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = EdgeMemorySuffix.Replace(title, "").Trim();
        foreach (var suffix in new[]
        {
            " - ChatGPT", " | ChatGPT", " — ChatGPT", " – ChatGPT"
        })
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length].Trim();
                break;
            }
        }
        if (title.Length is < 1 or > 200 ||
            title.Any(char.IsControl) ||
            new[] { "ChatGPT", "New chat", "Novo chat", "Nova conversa",
                "Untitled" }.Any(x => string.Equals(title, x,
                    StringComparison.OrdinalIgnoreCase)) ||
            title.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return null;
        return title;
    }

    internal static bool IsConfirmedTitle(string? tabTitle,
        IReadOnlyList<string> visiblePageLabels)
    {
        var normalized = NormalizeTabTitle(tabTitle);
        return normalized is not null && visiblePageLabels.Any(
            name => string.Equals(name.Trim(), normalized, StringComparison.Ordinal));
    }

    // Textos filhos do próprio TabItem do Edge não são evidência
    // independente de que o ChatGPT exibe esse título no conteúdo.
    private static bool IsInsideBrowserTab(AutomationElement item)
    {
        try
        {
            var parent = item.Parent;
            for (var depth = 0; parent is not null && depth < 32;
                depth++, parent = parent.Parent)
            {
                if (parent.ControlType == ControlType.TabItem) return true;
                if (parent.ControlType == ControlType.Window) return false;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return true; // Sem árvore íntegra, não usar como evidência.
        }
        return false;
    }

    // Leitura PASSIVA da guia já selecionada; exige URL exata e RuntimeId
    // fixado. Um rótulo isolado em Recentes não basta, pois a lista pode
    // estar ordenada diferentemente da guia ativa. Exigir concordância
    // entre o TabItem correspondente à URL e um elemento da página.
    public static ChatFullNamePreview Inspect(UIA3Automation uia, ChatBinding binding)
    {
        var selected = EdgeBrowser.ResolvePassiveExact(uia, binding);
        if (string.IsNullOrWhiteSpace(selected.TabRuntimeId))
            throw new InvalidOperationException(
                "CHAT_TITLE_PIN_REQUIRED: RuntimeId da guia obrigatório.");

        var tabTitle = EdgeBrowser.ReadSelectedTabTitle(uia, binding);
        var candidate = NormalizeTabTitle(tabTitle);
        var root = EdgeBrowser.FromHandle(uia, new IntPtr(selected.Handle));
        var nodes = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Text)
              .Or(cf.ByControlType(ControlType.Button))
              .Or(cf.ByControlType(ControlType.Hyperlink))
              .Or(cf.ByControlType(ControlType.ListItem))
              .Or(cf.ByControlType(ControlType.Group)));
        var labels = new List<string>();
        foreach (var node in nodes)
        {
            try
            {
                // Text não é controle interativo: alguns providers o
                // expõem como IsEnabled=false mesmo quando está visível.
                if (!node.Properties.IsOffscreen.ValueOrDefault &&
                    !IsInsideBrowserTab(node))
                {
                    var text = node.Properties.Name.ValueOrDefault;
                    if (!string.IsNullOrWhiteSpace(text)) labels.Add(text.Trim());
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Elemento descartado pelo Edge: não é evidência positiva.
            }
        }

        var matches = candidate is null ? 0 :
            labels.Count(x => string.Equals(x, candidate, StringComparison.Ordinal));
        // Título e URL devem permanecer estáveis durante toda a inspeção.
        var after = EdgeBrowser.ReadSelectedTabTitle(uia, binding);
        if (!string.Equals(tabTitle, after, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "CHAT_TITLE_CHANGED_DURING_INSPECTION: título UIA mudou.");

        // Mesmo sem label da página, o TabItem está correlacionado com a
        // URL/RuntimeId exatos antes e depois da leitura. Classificar a
        // evidência de aba separadamente de duas fontes concordantes.
        var status = candidate is null ? "UIA_TITLE_UNCONFIRMED" :
            matches > 0 ? "AUTO_DETECTED" : "AUTO_DETECTED_TAB_ONLY";
        return new ChatFullNamePreview(
            status,
            candidate,
            tabTitle,
            matches,
            binding.Url,
            selected.TabRuntimeId,
            matches > 0
                ? "Nome confirmado pela guia vinculada e por elemento visível da página."
                : candidate is not null
                    ? "Nome obtido da guia vinculada por URL/RuntimeId. " +
                      "A página não expôs o título como texto UIA independente."
                    : "Título genérico ou indisponível: não cadastrar automaticamente.");
    }
}
