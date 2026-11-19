using System.Text.Json;

namespace ChatAutomation.State;

// Estado global da interface, salvo fora da pasta de código e do catálogo de chats.
// A ausência do arquivo sempre significa false (não envia rascunhos não preparados).
public sealed class AutomationSettings
{
    private sealed record SettingsDocument(bool EnableSendHumanText);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private bool _enableSendHumanText;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatAutomation", "settings.json");

    public AutomationSettings(string? filePath = null)
    {
        _filePath = filePath ?? FilePath;
        Reload();
    }

    public bool EnableSendHumanText
    {
        get => _enableSendHumanText;
        set
        {
            if (_enableSendHumanText == value) return;
            Save(value); // Persistência antes de publicar o novo estado em memória.
            _enableSendHumanText = value;
        }
    }

    public bool Reload()
    {
        if (!File.Exists(_filePath))
        {
            _enableSendHumanText = false;
            return _enableSendHumanText;
        }
        var persisted = JsonSerializer.Deserialize<SettingsDocument>(
            File.ReadAllText(_filePath), JsonOptions)
            ?? throw new InvalidDataException("settings.json inválido: configuração nula.");
        _enableSendHumanText = persisted.EnableSendHumanText;
        return _enableSendHumanText;
    }

    private void Save(bool newValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SettingsDocument(newValue), JsonOptions));
        try { File.Move(temp, _filePath, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
