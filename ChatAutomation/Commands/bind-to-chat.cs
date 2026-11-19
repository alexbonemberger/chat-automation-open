using ChatAutomation.Browser;
using ChatAutomation.State;
using FlaUI.UIA3;

namespace ChatAutomation.Commands;

public static class BindToChat
{
    // Sem --url: vincula a guia selecionada no Edge, mesmo com o terminal em primeiro plano.
    // Se existirem várias janelas, exige --window-hwnd, sem escolha arbitrária.
    // Com --url: apenas grava a URL; nenhum navegador é aberto automaticamente.
    public static ChatBinding Run(UIA3Automation uia, string? explicitUrl = null,
        string? profileDirectory = null, long? windowHandle = null)
    {
        var binding = Capture(uia, explicitUrl, profileDirectory, windowHandle);
        CurrentBinding.Set(binding);
        return binding;
    }

    public static ChatBinding Capture(UIA3Automation uia, string? explicitUrl = null,
        string? profileDirectory = null, long? windowHandle = null)
    {
        string url;
        long? hwnd = null;
        string? runtimeId = null;
        string origin;
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (!UrlIdentity.TryNormalize(explicitUrl, out url))
                throw new ArgumentException("Informe uma URL HTTP(S) absoluta.");
            origin = "explicit-url";
        }
        else
        {
            var active = EdgeBrowser.ResolveReadableEdgeWindow(windowHandle);
            url = EdgeBrowser.ReadActiveUrl(uia, active);
            hwnd = active.ToInt64();
            runtimeId = EdgeBrowser.TryActiveExact(uia, active, url)?.TabRuntimeId;
            origin = "selected-edge-uia3";
        }
        return new ChatBinding(url, DateTimeOffset.UtcNow, origin, hwnd, profileDirectory, runtimeId);
    }
}
