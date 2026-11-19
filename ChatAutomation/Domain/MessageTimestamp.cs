using System.Globalization;
using System.Text.RegularExpressions;

namespace ChatAutomation.Domain;

// Timestamp de cada mensagem escrita pelo programa (não dos eventos JSON).
// Relógio fixo UTC-03:00, independente da timezone configurada no Windows.
public static class MessageTimestamp
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-3);
    private static readonly Regex ExistingHeader = new(
        @"^\[(?<value>\d{2}/\d{2}/\d{4}-\d{2}:\d{2})\](?:\r?\n| |$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool HasPrefix(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var match = ExistingHeader.Match(text);
        return match.Success && DateTime.TryParseExact(
            match.Groups["value"].Value, "dd/MM/yyyy-HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    public static bool HasMessageBody(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = ExistingHeader.Match(text);
        if (match.Success && HasPrefix(text))
            return !string.IsNullOrWhiteSpace(text[match.Length..]);
        return true;
    }

    public static string Apply(string text) => Apply(text, DateTimeOffset.UtcNow);

    // Sobrecarga determinística para testes; captura uma única vez o instante
    // de criação e o mantém ao longo do caminho setText -> sendMessage.
    internal static string Apply(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text) || HasPrefix(text)) return text;
        var local = now.ToUniversalTime().ToOffset(Offset);
        return "[" + local.ToString("dd/MM/yyyy-HH:mm", CultureInfo.InvariantCulture) +
            "]\n" + text;
    }
}
