using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatAutomation.State;

public sealed record ChatRecord(
    string Name,
    string Url,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long? PreferredWindowHandle = null,
    string? ProfileDirectory = null,
    string? PreferredTabRuntimeId = null,
    bool IsAtMaxLimit = false,
    string? ProjectId = null,
    string? DisplayTitle = null,
    string? FullName = null,
    string? FullNameSource = null);

// Catálogo persistente independente do HWND, que é efêmero após reinicializações.
public sealed class ChatRegistry
{
    private readonly Dictionary<string, ChatRecord> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatAutomation", "chats.json");

    public ChatRegistry(string? filePath = null, bool migrateLegacy = true)
    {
        _filePath = filePath ?? FilePath;
        Load();
        if (migrateLegacy) MigrateLegacyCurrentBinding();
    }

    public IReadOnlyList<ChatRecord> ListChats() => _byName.Values
        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public ChatRecord Get(string name) => _byName.TryGetValue(name, out var value) ? value
        : throw new KeyNotFoundException($"Chat '{name}' não registrado. Execute list-chats.");

    public bool TryGet(string name, out ChatRecord? chat) => _byName.TryGetValue(name, out chat);

    public ChatRecord Upsert(string? name, ChatBinding binding,
        string? projectId = null, string? displayTitle = null)
    {
        if (!UrlIdentity.TryNormalize(binding.Url, out var url))
            throw new ArgumentException("URL inválida para registro.");
        using var writeLock = EnterExclusive();
        ReloadFromDisk(); // Evita sobrescrever limite/chats alterados por outro processo.
        var preexisting = _byName.Values.FirstOrDefault(r => UrlIdentity.Exact(r.Url, url));
        name ??= preexisting?.Name ?? NextAvailableName();
        ValidateName(name);
        if (preexisting is not null && !preexisting.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URL já registrada como '{preexisting.Name}'. Evitando alias duplicado.");
        var now = DateTimeOffset.UtcNow;
        _byName.TryGetValue(name, out var old);
        var sameUrl = old is not null && UrlIdentity.Exact(old.Url, url);
        var retainedLimit = sameUrl && old!.IsAtMaxLimit;
        var next = new ChatRecord(name, url, old?.CreatedAtUtc ?? now, now,
            binding.PreferredWindowHandle, binding.ProfileDirectory,
            binding.PreferredTabRuntimeId, retainedLimit,
            projectId ?? (sameUrl ? old!.ProjectId : null),
            displayTitle ?? (sameUrl ? old!.DisplayTitle : null),
            sameUrl ? old!.FullName : null,
            sameUrl ? old!.FullNameSource : null);
        var oldList = new Dictionary<string, ChatRecord>(_byName, StringComparer.OrdinalIgnoreCase);
        _byName[name] = next;
        try { Save(); }
        catch
        {
            _byName.Clear();
            foreach (var pair in oldList) _byName.Add(pair.Key, pair.Value);
            throw;
        }
        return next;
    }

    // O nome visível é metadado do chat, diferente do ID imutável Name
    // e do DisplayTitle informado ao solicitar a criação. Preservar ambos
    // mesmo em refresh/rebind; nunca atualizar a URL por causa do título.
    internal static string NormalizeFullName(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 200 ||
            normalized.Any(char.IsControl))
            throw new ArgumentException(
                "CHAT_FULL_NAME_INVALID: informe título de 1 a 200 caracteres, " +
                "sem quebras de linha ou caracteres de controle.");
        return normalized;
    }

    public ChatRecord SetFullName(string name, string expectedUrl, string fullName,
        string source = "MANUAL")
    {
        if (source is not ("MANUAL" or "UIA_TAB_AND_PAGE" or "UIA_TAB_VERIFIED"))
            throw new ArgumentException("CHAT_FULL_NAME_SOURCE_INVALID");
        var value = NormalizeFullName(fullName);
        using var writeLock = EnterExclusive();
        ReloadFromDisk();
        var previous = Get(name);
        if (!UrlIdentity.Exact(previous.Url, expectedUrl))
            throw new InvalidOperationException(
                "CHAT_FULL_NAME_URL_CHANGED: URL do chat mudou, nome não alterado.");
        if (string.Equals(previous.FullName, value, StringComparison.Ordinal) &&
            string.Equals(previous.FullNameSource, source, StringComparison.Ordinal))
            return previous;
        var updated = previous with
        {
            FullName = value,
            FullNameSource = source,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _byName[name] = updated;
        try { Save(); }
        catch { _byName[name] = previous; throw; }
        return updated;
    }

    // A guia vinculada pode ser convertida em uma nova conversa do mesmo
    // projeto. Preserva a URL histórica do chat antigo e desassocia apenas
    // seu pin efêmero, sob o lock do catálogo. Não altera outros chats.
    public ChatRecord ReleaseTabPin(string name, string expectedUrl,
        long expectedWindowHandle, string expectedRuntimeId)
    {
        using var writeLock = EnterExclusive();
        ReloadFromDisk();
        var current = Get(name);
        if (!UrlIdentity.Exact(current.Url, expectedUrl))
            throw new InvalidOperationException(
                "PROJECT_SOURCE_RECORD_CHANGED: URL histórica mudou.");
        if (current.PreferredWindowHandle is null &&
            string.IsNullOrWhiteSpace(current.PreferredTabRuntimeId))
            return current; // Operação idempotente após retomada.
        if (current.PreferredWindowHandle != expectedWindowHandle ||
            !string.Equals(current.PreferredTabRuntimeId, expectedRuntimeId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PROJECT_SOURCE_PIN_CHANGED: não desassociar outro vínculo.");
        var detached = current with
        {
            PreferredWindowHandle = null,
            PreferredTabRuntimeId = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _byName[name] = detached;
        try { Save(); }
        catch { _byName[name] = current; throw; }
        return detached;
    }

    // Confirma uma nova conversa sob um nome previamente reservado. A
    // checagem e a escrita ocorrem dentro do mesmo mutex, impedindo que
    // outro processo tenha seu registro sobrescrito por uma finalização.
    public ChatRecord InsertNewChat(string name, ChatBinding binding,
        string? projectId, string? displayTitle)
    {
        if (!UrlIdentity.TryNormalize(binding.Url, out var url))
            throw new ArgumentException("NEW_CHAT_INVALID_URL");
        using var writeLock = EnterExclusive();
        ReloadFromDisk();
        if (_byName.TryGetValue(name, out var existing))
        {
            if (!UrlIdentity.Exact(existing.Url, url) ||
                !string.Equals(existing.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "NEW_CHAT_NAME_COLLISION: nome já associado a outra conversa.");
            return existing; // Finalização idempotente após falha parcial.
        }
        if (_byName.Values.Any(c => UrlIdentity.Exact(c.Url, url)))
            throw new InvalidOperationException(
                "NEW_CHAT_URL_COLLISION: URL já cadastrada sob outro nome.");
        ValidateName(name);
        var now = DateTimeOffset.UtcNow;
        var record = new ChatRecord(name, url, now, now,
            binding.PreferredWindowHandle, binding.ProfileDirectory,
            binding.PreferredTabRuntimeId, false, projectId, displayTitle);
        _byName[name] = record;
        try { Save(); }
        catch { _byName.Remove(name); throw; }
        return record;
    }

    // O limite é um evento terminal para a URL. Não é apagado por relink
    // do mesmo chat, foco, reinício nem refresh do catálogo.
    public ChatRecord MarkMaxLimit(string name, string expectedUrl)
    {
        using var writeLock = EnterExclusive();
        ReloadFromDisk();
        var previous = Get(name);
        if (!UrlIdentity.Exact(previous.Url, expectedUrl))
            throw new InvalidOperationException(
                "MAX_LIMIT_URL_CHANGED: URL do chat mudou antes da gravação; estado não alterado.");
        if (previous.IsAtMaxLimit) return previous;
        var updated = previous with { IsAtMaxLimit = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _byName[name] = updated;
        try { Save(); }
        catch { _byName[name] = previous; throw; }
        return updated;
    }

    public void Remove(string name)
    {
        using var writeLock = EnterExclusive();
        ReloadFromDisk();
        var old = Get(name);
        _byName.Remove(name);
        try { Save(); }
        catch { _byName[name] = old; throw; }
    }

    private void ReloadFromDisk()
    {
        _byName.Clear();
        Load();
    }

    // Exclusão mútua nomeada por caminho: processos do console e busyWindow()
    // preservam a escrita transacional do catálogo e a marca de limite.
    private IDisposable EnterExclusive()
    {
        var id = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(_filePath).ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, @"Local\ChatAutomation.Registry." + id);
        try
        {
            var acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new TimeoutException("CHAT_REGISTRY_LOCK_TIMEOUT: catálogo em uso por outro processo.");
            return new RegistryLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class RegistryLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }

    private string NextAvailableName()
    {
        for (var i = 0; i <= 9999; i++)
        {
            var candidate = $"chat{i:00}";
            if (!_byName.ContainsKey(candidate)) return candidate;
        }
        throw new InvalidOperationException("Catálogo cheio: escolha um nome manualmente.");
    }

    private static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_-]{0,49}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Nome inválido. Use letras ASCII no início, seguidas por letras, números, _ ou - (até 50 caracteres).");
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        var parsed = JsonSerializer.Deserialize<List<ChatRecord>>(File.ReadAllText(_filePath))
            ?? throw new InvalidDataException("Arquivo chats.json inválido: lista nula.");
        foreach (var chat in parsed)
        {
            ValidateName(chat.Name);
            if (!UrlIdentity.TryNormalize(chat.Url, out var canonical))
                throw new InvalidDataException($"URL inválida no chat '{chat.Name}'.");
            if (!_byName.TryAdd(chat.Name, chat with { Url = canonical }))
                throw new InvalidDataException($"Nome repetido no catálogo: '{chat.Name}'.");
            if (_byName.Values.Count(c => UrlIdentity.Exact(c.Url, canonical)) > 1)
                throw new InvalidDataException($"URL repetida no catálogo: '{canonical}'.");
        }
    }

    private void MigrateLegacyCurrentBinding()
    {
        var current = CurrentBinding.currentBindedChat;
        if (current is null) return;
        if (_byName.Values.Any(x => UrlIdentity.Exact(x.Url, current.Url))) return;
        Upsert(null, current);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ListChats(),
            new JsonSerializerOptions { WriteIndented = true }));
        try { File.Move(temp, _filePath, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
