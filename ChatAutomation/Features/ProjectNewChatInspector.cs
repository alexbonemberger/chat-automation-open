using ChatAutomation.Browser;
using ChatAutomation.State;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ChatAutomation.Features;

public sealed record ProjectActionUiEvidence(
    string ControlType, string Name, string AutomationId, string HelpText,
    bool InsideDocument, bool Enabled, bool Visible, bool InvokeSupported,
    IReadOnlyList<string> ParentChain);

public sealed record ProjectActionInspection(
    string Status, string ProjectPrefix, string ObservedUrl,
    long WindowHandle, string TabRuntimeId, bool ProjectNameVisible,
    int MatchingControls, IReadOnlyList<ProjectActionUiEvidence> Controls);

// E2E do fluxo REAL: descobre a acessibilidade dos controles da página
// de projeto, sem criar/reservar chat, mover guias, navegar ou clicar.
public static class ProjectNewChatInspector
{
    private static readonly string[] Relevant =
    [
        "chat", "new", "novo", "nova", "criar", "create", "start",
        "iniciar", "convers", "project", "projet"
    ];

    internal static bool IsRelevantControl(string? name, string? id, string? help) =>
        Relevant.Any(term =>
            (name ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (id ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (help ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));

    // Controles de criação antes de links de chats antigos/menus do projeto.
    // A classificação é só para EXIBIÇÃO, não autoriza clique ou seleção.
    internal static int ActionPriority(string? name, string? id, string? help)
    {
        var label = string.Join(" ", name ?? "", id ?? "", help ?? "").ToLowerInvariant();
        if (new[] { "new chat", "novo chat", "nova conversa",
                    "criar chat", "criar conversa", "start chat",
                    "new-chat", "create chat" }.Any(term =>
                        label.Contains(term, StringComparison.Ordinal)))
            return 0;
        if (label.Contains("chat") || label.Contains("convers")) return 1;
        return 2;
    }

    internal static bool IsProjectUrl(string? observed, string prefix)
    {
        if (observed is null ||
            !Uri.TryCreate(observed, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
            return false;
        if (UrlIdentity.Exact(observed, prefix) ||
            ChatUrlScope.IsPersistentConversation(observed, prefix))
            return true;
        // Projeto vazio: a guia pode estar na rota /project, ainda sem /c/ID.
        // Não aceitar URL de outro projeto nem rotas arbitrárias semelhantes.
        var projectPage = new Uri(prefix).AbsolutePath.TrimEnd('/') + "/project";
        return string.Equals(uri.AbsolutePath.TrimEnd('/'), projectPage,
            StringComparison.Ordinal);
    }

    public static ProjectActionInspection Inspect(UIA3Automation uia,
        string projectPrefix, string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        // Só inspecionar UMA guia já selecionada num Edge, sem alterar foco.
        var found = new List<(IntPtr Handle, SelectedTabState Tab, string Url)>();
        foreach (var hwnd in EdgeBrowser.Windows())
        {
            try
            {
                var tab = EdgeBrowser.SelectedTab(uia, hwnd);
                var url = EdgeBrowser.ReadActiveUrl(uia, hwnd);
                if (IsProjectUrl(url, projectPrefix))
                    found.Add((hwnd, tab, url));
            }
            catch (InvalidOperationException) { /* janela sem URL/guia estável */ }
        }
        if (found.Count == 0)
            throw new InvalidOperationException(
                "PROJECT_INSPECT_TAB_NOT_SELECTED: selecione no Edge uma guia " +
                "do projeto " + projectName + " antes de inspecionar. " +
                "O comando não altera guias nem reserva chats.");
        if (found.Count != 1)
            throw new InvalidOperationException(
                "PROJECT_INSPECT_AMBIGUOUS: várias janelas Edge têm uma " +
                "guia selecionada deste projeto; feche ou desative as duplicatas.");

        var target = found[0];
        var root = EdgeBrowser.FromHandle(uia, target.Handle);
        var nodes = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
            .Or(cf.ByControlType(ControlType.Hyperlink))
            .Or(cf.ByControlType(ControlType.MenuItem)));
        var controls = new List<ProjectActionUiEvidence>();
        foreach (var node in nodes)
        {
            try
            {
                var name = node.Properties.Name.ValueOrDefault ?? "";
                var id = node.Properties.AutomationId.ValueOrDefault ?? "";
                var help = node.Properties.HelpText.ValueOrDefault ?? "";
                if (!IsRelevantControl(name, id, help)) continue;
                controls.Add(new ProjectActionUiEvidence(
                    node.ControlType.ToString(), name, id, help,
                    InsideDocument(node),
                    node.Properties.IsEnabled.ValueOrDefault,
                    !node.Properties.IsOffscreen.ValueOrDefault,
                    node.Patterns.Invoke.IsSupported,
                    DescribeParents(node)));
            }
            catch { /* elemento transitório removido durante renderização */ }
        }
        var projectNameVisible = false;
        try
        {
            projectNameVisible = root.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Text))
                .Take(350).Any(item =>
                    string.Equals(item.Properties.Name.ValueOrDefault?.Trim(),
                        projectName, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* nome da página é evidência auxiliar, não identidade */ }

        // Confirmação read-only depois de enumerar a UIA.
        var afterTab = EdgeBrowser.SelectedTab(uia, target.Handle);
        var afterUrl = EdgeBrowser.ReadActiveUrl(uia, target.Handle);
        if (afterTab.RuntimeId != target.Tab.RuntimeId ||
            !UrlIdentity.Exact(afterUrl, target.Url))
            throw new InvalidOperationException(
                "PROJECT_INSPECT_TAB_CHANGED: URL/RuntimeId mudou durante " +
                "a inspeção; resultado descartado.");

        return new ProjectActionInspection(
            "INSPECTED_READONLY", projectPrefix, target.Url,
            target.Handle.ToInt64(), target.Tab.RuntimeId, projectNameVisible,
            controls.Count, controls
                .OrderBy(c => ActionPriority(c.Name, c.AutomationId, c.HelpText))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(50).ToArray());
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
        catch { /* UIA mudou durante leitura */ }
        return false;
    }

    private static IReadOnlyList<string> DescribeParents(AutomationElement element)
    {
        var parents = new List<string>();
        try
        {
            var parent = element.Parent;
            for (var i = 0; i < 6 && parent is not null; i++, parent = parent.Parent)
            {
                var name = parent.Properties.Name.ValueOrDefault ?? "";
                if (name.Length > 120) name = name[..120];
                parents.Add(parent.ControlType + ":" + name);
                if (parent.ControlType == ControlType.Window) break;
            }
        }
        catch { /* cadeia parcial ainda é útil */ }
        return parents;
    }
}
