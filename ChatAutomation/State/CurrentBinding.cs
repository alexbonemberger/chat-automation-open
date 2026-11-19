using System.Text.Json;

namespace ChatAutomation.State;

public sealed record ChatBinding(
    string Url,
    DateTimeOffset BoundAtUtc,
    string Origin,
    long? PreferredWindowHandle = null,
    string? ProfileDirectory = null,
    string? PreferredTabRuntimeId = null);

public static class CurrentBinding
{
    // Nome solicitado: estado global carregado no início de cada processo.
    // A persistência também permite comandos CLI em processos independentes.
    public static ChatBinding? currentBindedChat { get; private set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatAutomation", "current-binded-chat.json");

    public static ChatBinding Require() => currentBindedChat
        ?? throw new InvalidOperationException("Não há vínculo. Execute bind-to-chat primeiro.");

    public static void Load()
    {
        if (!File.Exists(FilePath))
        {
            currentBindedChat = null;
            return;
        }

        var parsed = JsonSerializer.Deserialize<ChatBinding>(File.ReadAllText(FilePath));
        if (parsed is null || !UrlIdentity.TryNormalize(parsed.Url, out _))
            throw new InvalidDataException("Arquivo de vínculo inválido: " + FilePath);
        currentBindedChat = parsed;
    }

    public static void Set(ChatBinding binding)
    {
        if (!UrlIdentity.TryNormalize(binding.Url, out var canonical))
            throw new ArgumentException("URL de vínculo inválida.");
        binding = binding with { Url = canonical };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempFile = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(binding, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            File.Move(tempFile, FilePath, overwrite: true);
            currentBindedChat = binding;
        }
        catch
        {
            File.Delete(tempFile);
            throw;
        }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        currentBindedChat = null;
    }
}

public static class UrlIdentity
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        // A barra de endereço pode ocultar o esquema em algumas versões do Edge.
        if (!value.Contains("://", StringComparison.Ordinal) &&
            value.Contains('.') && !value.Contains(' ')) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    // Compara esquema, host, porta, caminho, query e fragmento. Nunca aceita prefixo.
    public static bool Exact(string candidate, string expected) =>
        TryNormalize(candidate, out var left) &&
        TryNormalize(expected, out var right) &&
        StringComparer.Ordinal.Equals(left, right);
}
