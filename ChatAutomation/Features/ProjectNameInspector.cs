using ChatAutomation.Browser;
using ChatAutomation.State;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ChatAutomation.Features;

public sealed record ProjectNamePreview(
    string Status,
    string FullName,
    string ProjectUrl,
    string ObservedUrl,
    string PlaceholderText,
    bool HeadingMatched,
    bool ComposerVerified,
    string NameEvidence,
    long WindowHandle,
    string TabRuntimeId);

// Descobre o nome VISÍVEL de um projeto a partir da interface real.
// Somente leitura: sem selecionar guia, navegar, clicar ou gravar catálogo.
// Nunca converte o slug da URL num nome "inventado".
public static class ProjectNameInspector
{
    private static readonly string[] ComposePrefixes =
    [
        "Novo chat em ",
        "New chat in ",
        "Nova conversa em ",
        "Start a chat in "
    ];

    internal static string? NameFromComposeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var text = label.Trim();
        foreach (var prefix in ComposePrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = text[prefix.Length..].Trim();
            return name.Length is > 0 and <= 200 &&
                !name.Contains('\n') && !name.Contains('\r') ? name : null;
        }
        return null;
    }

    internal static bool IsProjectPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/project", StringComparison.Ordinal))
            return false;
        try
        {
            return string.Equals(uri.AbsolutePath.TrimEnd('/'),
                new Uri(ChatUrlScope.ProjectPage(url!)).AbsolutePath,
                StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    // Aprovação da prévia é vinculada à identidade exata da guia e ao
    // mesmo nome/placeholder, nunca apenas ao slug semelhante de outra aba.
    internal static bool SamePreviewIdentity(
        ProjectNamePreview approved, ProjectNamePreview current) =>
        string.Equals(approved.FullName, current.FullName, StringComparison.Ordinal) &&
        string.Equals(approved.ProjectUrl, current.ProjectUrl, StringComparison.Ordinal) &&
        UrlIdentity.Exact(approved.ObservedUrl, current.ObservedUrl) &&
        string.Equals(approved.PlaceholderText, current.PlaceholderText,
            StringComparison.Ordinal) &&
        approved.WindowHandle == current.WindowHandle &&
        string.Equals(approved.TabRuntimeId, current.TabRuntimeId,
            StringComparison.Ordinal);

    public static ProjectNamePreview Preview(UIA3Automation uia)
    {
        var pages = new List<(IntPtr Hwnd, SelectedTabState Tab, string Url)>();
        foreach (var hwnd in EdgeBrowser.Windows())
        {
            try
            {
                var tab = EdgeBrowser.SelectedTab(uia, hwnd);
                var url = EdgeBrowser.ReadActiveUrl(uia, hwnd);
                if (IsProjectPage(url)) pages.Add((hwnd, tab, url));
            }
            catch (InvalidOperationException) { /* outra janela em transição */ }
        }
        if (pages.Count == 0)
            throw new InvalidOperationException(
                "PROJECT_UIA_PAGE_NOT_SELECTED: selecione a guia /project " +
                "no Edge. Nenhum projeto cadastrado.");
        if (pages.Count != 1)
            throw new InvalidOperationException(
                "PROJECT_UIA_PAGE_AMBIGUOUS: mais de uma janela Edge tem " +
                "página /project selecionada. Nenhum projeto cadastrado.");

        var page = pages[0];
        var root = EdgeBrowser.FromHandle(uia, page.Hwnd);
        var controls = root.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.Text)
                .Or(cf.ByControlType(ControlType.Edit))
                .Or(cf.ByControlType(ControlType.Document))
                .Or(cf.ByControlType(ControlType.Group)));
        var visibleHeadings = new List<string>();
        var directLabels = new List<string>();
        var groupLabels = new List<string>();
        foreach (var control in controls)
        {
            try
            {
                if (control.Properties.IsOffscreen.ValueOrDefault) continue;
                var name = control.Properties.Name.ValueOrDefault?.Trim();
                var help = control.Properties.HelpText.ValueOrDefault?.Trim();
                if (control.ControlType == ControlType.Text &&
                    !string.IsNullOrWhiteSpace(name))
                    visibleHeadings.Add(name);
                // Dar prioridade ao placeholder do campo ou seu Text node.
                // Group pode concatenar filhos; usar só quando não há rótulo
                // direto, evitando aceitar um nome parcialmente composto.
                var labels = control.ControlType == ControlType.Group
                    ? groupLabels : directLabels;
                if (NameFromComposeLabel(name) is not null) labels.Add(name!);
                if (NameFromComposeLabel(help) is not null) labels.Add(help!);
            }
            catch { /* apenas leitura, elementos UIA transitórios */ }
        }
        var candidateLabels = directLabels.Count > 0 ? directLabels : groupLabels;
        var detectedNames = candidateLabels
            .Select(NameFromComposeLabel)
            .Where(name => name is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (detectedNames.Length == 0)
            throw new InvalidOperationException(
                "PROJECT_UIA_NAME_NOT_FOUND: nem título nem placeholder " +
                "'Novo chat em ...' identificados de forma confiável. " +
                "Não será usado o slug da URL.");
        if (detectedNames.Length != 1)
            throw new InvalidOperationException(
                "PROJECT_UIA_NAME_AMBIGUOUS: placeholders de projetos " +
                "divergentes na árvore UIA. Nenhum cadastro realizado.");

        var projectName = detectedNames[0]!;
        var placeholder = candidateLabels.First(label =>
            string.Equals(NameFromComposeLabel(label), projectName,
                StringComparison.Ordinal));
        var headingMatched = visibleHeadings.Any(value =>
            string.Equals(value, projectName, StringComparison.Ordinal));
        var projectUrl = ChatUrlScope.ProjectPrefix(page.Url);
        var composerVerified = false;
        try
        {
            var binding = new ChatBinding(page.Url, DateTimeOffset.UtcNow,
                "project-name-preview", page.Hwnd.ToInt64(), null, page.Tab.RuntimeId);
            _ = ChatFeatures.DetectTextAreaPassive(uia, binding);
            composerVerified = true;
        }
        catch (InvalidOperationException) { /* diagnóstico, sem mudar foco */ }

        var selectedAfter = EdgeBrowser.SelectedTab(uia, page.Hwnd);
        var urlAfter = EdgeBrowser.ReadActiveUrl(uia, page.Hwnd);
        if (!string.Equals(selectedAfter.RuntimeId, page.Tab.RuntimeId,
                StringComparison.Ordinal) ||
            !UrlIdentity.Exact(urlAfter, page.Url))
            throw new InvalidOperationException(
                "PROJECT_UIA_PAGE_CHANGED: seleção ou URL alterada " +
                "durante a leitura. Resultado descartado.");

        return new ProjectNamePreview(
            "PREVIEW_ONLY", projectName, projectUrl, page.Url,
            placeholder, headingMatched, composerVerified,
            headingMatched ? "PLACEHOLDER_AND_HEADING" : "PLACEHOLDER_ONLY",
            page.Hwnd.ToInt64(), page.Tab.RuntimeId);
    }
}
