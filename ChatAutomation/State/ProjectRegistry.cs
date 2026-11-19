using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatAutomation.Domain;

namespace ChatAutomation.State;

public sealed record ProjectRecord(
    string Id,
    string FullName,
    string UrlPrefix,
    int NextChatNumber,
    DateTimeOffset CreatedAtUtc);

public sealed record PendingNewChatRecord(
    string Name,
    string? DisplayTitle,
    string? ProjectId,
    string LandingUrl,
    string State,
    DateTimeOffset CreatedAtUtc,
    long? WindowHandle = null,
    string? TabRuntimeId = null,
    string? LastObservedUrl = null,
    long? PreviousWindowHandle = null,
    string? PreviousTabRuntimeId = null,
    int ReopenCount = 0);

public sealed record ProjectChatEntry(
    string Name, string? DisplayTitle, string? Url, string State,
    string ProjectId, string? FullName = null,
    string? FullNameSource = null);

public sealed record NewChatPreview(
    string Name, string? DisplayTitle, string? ProjectId, string LandingUrl,
    string Status = "PREVIEW_ONLY");

internal sealed class ProjectState
{
    public ProjectState() { }

    public int NextProjectNumber { get; set; }
    public int NextGlobalChatNumber { get; set; } = Chat.InitialStandaloneSequence;
    public List<ProjectRecord> Projects { get; set; } = [];
    public List<PendingNewChatRecord> PendingChats { get; set; } = [];
}

// A URL do projeto identifica escopo; NUNCA a confundimos com a URL
// persistente de uma conversa, que o ChatGPT pode atribuir só após o envio.
public static class ChatUrlScope
{
    public static string ProjectPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PROJECT_INVALID_URL: use URL HTTPS do projeto em chatgpt.com.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Contains("/c/", StringComparison.Ordinal))
            path = path[..path.IndexOf("/c/", StringComparison.Ordinal)];
        // Projetos novos podem ser compartilhados como /g/g-p-ID/project.
        // A identidade do projeto é a raiz, nunca a rota de apresentação.
        if (path.EndsWith("/project", StringComparison.Ordinal))
            path = path[..(path.Length - "/project".Length)];
        if (!path.StartsWith("/g/g-p-", StringComparison.Ordinal) ||
            path.Length <= "/g/g-p-".Length || path["/g/g-p-".Length..].Contains('/'))
            throw new ArgumentException("PROJECT_INVALID_URL: informe a URL raiz do projeto ou de um chat do projeto.");
        return "https://chatgpt.com" + path;
    }

    // Rota efetiva da página do projeto que dispõe de um editor de nova
    // conversa, mesmo quando ainda não há nenhuma conversa salva.
    public static string ProjectPage(string projectPrefix) =>
        ProjectPrefix(projectPrefix) + "/project";

    // Na UI do ChatGPT a rota do projeto pode ganhar um slug:
    // g-p-<id>/project -> g-p-<id>-<slug>/c/<id-chat>.
    // Somente o identificador opaco de 32 hexadecimais é invariável.
    // O sufixo é aceito apenas quando começa depois dos mesmos 32 dígitos.
    internal static bool SameProjectSegment(string expected, string observed)
    {
        if (string.Equals(expected, observed, StringComparison.Ordinal)) return true;
        if (!expected.StartsWith("g-p-", StringComparison.Ordinal) ||
            !observed.StartsWith("g-p-", StringComparison.Ordinal) ||
            expected.Length < 36 || observed.Length < 36)
            return false;
        var expectedId = expected[4..36];
        var observedId = observed[4..36];
        if (!expectedId.All(Uri.IsHexDigit) || !observedId.All(Uri.IsHexDigit))
            return false;
        if (expected.Length > 36 && expected[36] != '-') return false;
        if (observed.Length > 36 && observed[36] != '-') return false;
        return string.Equals(expectedId, observedId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConversationId(string? url, string? projectPrefix,
        out string conversationId)
    {
        conversationId = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 ||
            uri.Port != 443 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (projectPrefix is null)
        {
            if (parts.Length != 2 || parts[0] is not ("c" or "uc"))
                return false;
            conversationId = Uri.UnescapeDataString(parts[1]);
            return conversationId.Length >= 8;
        }

        if (!Uri.TryCreate(projectPrefix, UriKind.Absolute, out var root) ||
            root.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(root.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var expectedParts = root.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expectedParts.Length != 2 || expectedParts[0] != "g" ||
            parts.Length != 4 || parts[0] != "g" ||
            !SameProjectSegment(expectedParts[1], parts[1]) ||
            parts[2] is not ("c" or "uc"))
            return false;
        conversationId = Uri.UnescapeDataString(parts[3]);
        return conversationId.Length >= 8;
    }

    // Os IDs local-chatgpt:UUID são rascunhos provisórios durante a
    // persistência remota e não podem entrar em chats.json.
    public static bool IsProvisionalConversation(string? url, string? projectPrefix) =>
        TryConversationId(url, projectPrefix, out var id) &&
        id.StartsWith("local-chatgpt:", StringComparison.OrdinalIgnoreCase);

    public static bool IsPersistentConversation(string? url, string? projectPrefix) =>
        TryConversationId(url, projectPrefix, out var id) &&
        !id.StartsWith("local-chatgpt:", StringComparison.OrdinalIgnoreCase);

}

public sealed class ProjectRegistry
{
    private readonly string _path;
    private readonly string _chatPath;
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatAutomation", "projects.json");

    public ProjectRegistry(string? path = null, string? chatPath = null)
    {
        _path = path ?? FilePath;
        _chatPath = chatPath ?? ChatRegistry.FilePath;
    }

    public IReadOnlyList<ProjectRecord> ListProjects() => Read().Projects
        .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<PendingNewChatRecord> ListPending() => Read().PendingChats
        .OrderBy(p => p.CreatedAtUtc).ToArray();

    public ProjectRecord GetProject(string id) => ListProjects().SingleOrDefault(p =>
        string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        throw new KeyNotFoundException($"PROJECT_NOT_FOUND: '{id}' não cadastrado.");

    public PendingNewChatRecord GetPending(string name) => ListPending().SingleOrDefault(p =>
        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ??
        throw new KeyNotFoundException($"PENDING_CHAT_NOT_FOUND: '{name}' não encontrado.");

    public ProjectRecord AddProject(string fullName, string urlPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        if (fullName.Trim().Length > 200)
            throw new ArgumentException("PROJECT_NAME_TOO_LONG: limite de 200 caracteres.");
        var prefix = ChatUrlScope.ProjectPrefix(urlPrefix);
        using var mutex = Lock();
        var state = Read();
        if (state.Projects.Any(p => string.Equals(p.UrlPrefix, prefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("PROJECT_ALREADY_REGISTERED: URL do projeto duplicada.");
        var id = $"{Project.DefaultIdPrefix}{state.NextProjectNumber:D4}";
        if (state.Projects.Any(p => p.Id == id))
            throw new InvalidOperationException("PROJECT_ID_COLLISION");
        state.NextProjectNumber++;
        var result = new ProjectRecord(id, fullName.Trim(), prefix, 0, DateTimeOffset.UtcNow);
        state.Projects.Add(result);
        Save(state);
        return result;
    }

    // Prévia somente leitura. Outro processo pode reservar o mesmo candidato
    // após a consulta; Reserve() sempre calcula novamente sob mutex.
    public NewChatPreview Preview(string? displayTitle, string? projectId)
    {
        ValidateDisplayTitle(displayTitle);
        var state = Read();
        ProjectRecord? project = null;
        if (projectId is not null)
            project = state.Projects.SingleOrDefault(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase)) ??
                throw new KeyNotFoundException("PROJECT_NOT_FOUND: " + projectId);
        var (name, _) = NextCandidate(state, project);
        return new NewChatPreview(name, displayTitle?.Trim(), project?.Id,
            project is null ? "https://chatgpt.com/"
                : ChatUrlScope.ProjectPage(project.UrlPrefix));
    }

    private static void ValidateDisplayTitle(string? displayTitle)
    {
        if (displayTitle is { Length: > 200 })
            throw new ArgumentException("CHAT_TITLE_TOO_LONG: limite de 200 caracteres.");
    }

    private (string Name, int Next) NextCandidate(ProjectState state, ProjectRecord? project)
    {
        var occupied = new ChatRegistry(_chatPath, migrateLegacy: false)
            .ListChats().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pending in state.PendingChats) occupied.Add(pending.Name);
        var next = project?.NextChatNumber ?? Math.Max(
            state.NextGlobalChatNumber, Chat.InitialStandaloneSequence);
        string name;
        do
        {
            name = project is null
                ? $"{Chat.DefaultIdPrefix}{next:D4}"
                : $"{project.Id}-{Chat.DefaultIdPrefix}{next:D4}";
            next++;
        } while (occupied.Contains(name));
        return (name, next);
    }

    public PendingNewChatRecord Reserve(string? displayTitle, string? projectId)
    {
        ValidateDisplayTitle(displayTitle);
        using var mutex = Lock();
        var state = Read();
        ProjectRecord? project = null;
        var index = -1;
        if (projectId is not null)
        {
            index = state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException("PROJECT_NOT_FOUND: " + projectId);
            project = state.Projects[index];
        }

        // Reserva global serializada. Nunca reutiliza nomes já registrados
        // ou previamente reservados, mesmo em caso de falha de abertura.
        var (name, next) = NextCandidate(state, project);

        if (project is null) state.NextGlobalChatNumber = next;
        else state.Projects[index] = project with { NextChatNumber = next };
        var reserved = new PendingNewChatRecord(name, displayTitle?.Trim(), project?.Id,
            project is null ? "https://chatgpt.com/"
                : ChatUrlScope.ProjectPage(project.UrlPrefix),
            "RESERVED", DateTimeOffset.UtcNow);
        state.PendingChats.Add(reserved);
        Save(state);
        return reserved;
    }

    // Reabertura EXPLÍCITA de aba fechada: apenas TAB_CREATED (antes de
    // qualquer CTA) pode ser substituído por outra guia. Preserva a reserva,
    // título e o contador; a identidade anterior fica no registro para auditoria.
    public PendingNewChatRecord AuthorizeClosedTabReopen(
        string name, long oldHwnd, string oldRuntimeId)
    {
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if (old.State != "TAB_CREATED" ||
            old.WindowHandle != oldHwnd ||
            !string.Equals(old.TabRuntimeId, oldRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "NEW_CHAT_REOPEN_NOT_ALLOWED: somente TAB_CREATED com pin inalterado " +
                "pode ser reaberto. Nunca reexecutar ação de criação do projeto.");
        var requested = old with
        {
            State = "REOPEN_REQUESTED",
            WindowHandle = null,
            TabRuntimeId = null,
            LastObservedUrl = null,
            PreviousWindowHandle = oldHwnd,
            PreviousTabRuntimeId = oldRuntimeId,
            ReopenCount = old.ReopenCount + 1
        };
        state.PendingChats[index] = requested;
        Save(state);
        return requested;
    }

    // Estado de intenção persistido ANTES do novo Ctrl+T. Se houver falha
    // entre Ctrl+T e registro do RuntimeId, não abrir novamente às cegas.
    public void MarkReopenAttemptStarted(string name)
    {
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if (old.State != "REOPEN_REQUESTED" ||
            old.WindowHandle is not null || old.TabRuntimeId is not null)
            throw new InvalidOperationException("NEW_CHAT_REOPEN_ALREADY_STARTED");
        state.PendingChats[index] = old with { State = "REOPEN_ATTEMPT_STARTED" };
        Save(state);
    }

    // Guarda a identidade da guia ASSIM que Ctrl+T é comprovado, antes
    // de qualquer navegação. Em falha posterior, resume a MESMA guia.
    public PendingNewChatRecord MarkTabCreated(string name, long hwnd, string runtimeId)
    {
        if (hwnd <= 0 || string.IsNullOrWhiteSpace(runtimeId))
            throw new ArgumentException("NEW_CHAT_TAB_IDENTITY_INVALID");
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if ((old.State != "RESERVED" && old.State != "REOPEN_ATTEMPT_STARTED") ||
            old.WindowHandle is not null || old.TabRuntimeId is not null)
            throw new InvalidOperationException("NEW_CHAT_TAB_ALREADY_ASSIGNED");
        var pinned = old with
        {
            State = "TAB_CREATED",
            WindowHandle = hwnd,
            TabRuntimeId = runtimeId
        };
        state.PendingChats[index] = pinned;
        Save(state);
        return pinned;
    }

    // Protege o mesmo ID de duas instâncias simultâneas do console.
    // O nome do mutex é derivado do caminho e do ID, não do título humano.
    public IDisposable AcquireChatOperation(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = Path.GetFullPath(_path).ToUpperInvariant() + "|" + name.ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var mutex = new Mutex(false, @"Local\ChatAutomation.NewChat." + id);
        try
        {
            var acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("NEW_CHAT_OPERATION_BUSY");
            return new HeldLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    // Armazena a intenção ANTES do clique em "novo chat no projeto".
    // Em falha posterior, a retomada só comprova a nova guia/URL e não
    // repete o clique, evitando criar duas conversas com a mesma reserva.
    public PendingNewChatRecord MarkProjectActionAttempted(string name, string beforeUrl)
    {
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if (old.State != "TAB_CREATED" ||
            old.WindowHandle is null || string.IsNullOrWhiteSpace(old.TabRuntimeId))
            throw new InvalidOperationException("PROJECT_ACTION_ALREADY_ATTEMPTED_OR_TAB_UNPINNED");
        var attempted = old with
        {
            State = "PROJECT_ACTION_ATTEMPTED",
            LastObservedUrl = beforeUrl
        };
        state.PendingChats[index] = attempted;
        Save(state);
        return attempted;
    }

    public PendingNewChatRecord MarkOpened(string name, long hwnd, string? runtimeId, string observedUrl)
    {
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if (old.State != "TAB_CREATED" &&
            old.State != "PROJECT_ACTION_ATTEMPTED" &&
            old.State != "OPENED_PENDING_URL")
            throw new InvalidOperationException("NEW_CHAT_TAB_NOT_PINNED");
        if (old.WindowHandle != hwnd ||
            !string.Equals(old.TabRuntimeId, runtimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("NEW_CHAT_TAB_IDENTITY_CHANGED");
        var value = old with
        {
            State = "OPENED_PENDING_URL",
            WindowHandle = hwnd,
            TabRuntimeId = runtimeId,
            LastObservedUrl = observedUrl
        };
        state.PendingChats[index] = value;
        Save(state);
        return value;
    }

    // Operação REAL de criação: após preencher e verificar o editor da
    // página /project, persistir o limite transacional ANTES de clicar Enviar.
    // Se o clique falhar, nunca reenviar automaticamente a mesma mensagem.
    public PendingNewChatRecord MarkFirstSendAttempted(
        string name, long hwnd, string tabRuntimeId, string composerUrl)
    {
        using var mutex = Lock();
        var state = Read();
        var index = state.PendingChats.FindIndex(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new KeyNotFoundException("PENDING_CHAT_NOT_FOUND: " + name);
        var old = state.PendingChats[index];
        if (old.State != "OPENED_PENDING_URL" || old.ProjectId is null ||
            old.WindowHandle != hwnd ||
            !string.Equals(old.TabRuntimeId, tabRuntimeId, StringComparison.Ordinal) ||
            old.LastObservedUrl is null ||
            !UrlIdentity.Exact(old.LastObservedUrl, composerUrl))
            throw new InvalidOperationException(
                "NEW_CHAT_FIRST_SEND_NOT_READY: reserva, URL ou pin mudaram; " +
                "não será acionado outro envio.");
        var attempted = old with { State = "FIRST_SEND_ATTEMPTED" };
        state.PendingChats[index] = attempted;
        Save(state);
        return attempted;
    }

    // A conclusão só remove a reserva DEPOIS de comprovar que o chat
    // existe em chats.json com nome, projeto e URL persistente coerentes.
    // Uma falha no registro não pode apagar a única trilha de recuperação.
    public void MarkCompleted(string name)
    {
        using var mutex = Lock();
        var state = Read();
        var pending = state.PendingChats.SingleOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pending is null)
            return; // Idempotente após registro e remoção bem-sucedidos.

        var registry = new ChatRegistry(_chatPath, migrateLegacy: false);
        if (!registry.TryGet(name, out var saved) || saved is null ||
            !string.Equals(saved.ProjectId, pending.ProjectId,
                StringComparison.OrdinalIgnoreCase) ||
            !ChatUrlScope.IsPersistentConversation(saved.Url,
                pending.ProjectId is null ? null :
                    state.Projects.Single(p =>
                        p.Id.Equals(pending.ProjectId,
                            StringComparison.OrdinalIgnoreCase)).UrlPrefix))
            throw new InvalidOperationException(
                "NEW_CHAT_COMPLETION_NOT_PERSISTED: chat não possui URL " +
                "definitiva registrada no projeto correto; reserva mantida.");

        state.PendingChats.Remove(pending);
        Save(state);
    }

    public IReadOnlyList<ProjectChatEntry> ProjectListChats(string projectId)
    {
        var project = GetProject(projectId);
        var allChats = new ChatRegistry(_chatPath, migrateLegacy: false)
            .ListChats();
        if (allChats.Any(c => string.Equals(c.ProjectId, projectId,
                StringComparison.OrdinalIgnoreCase) &&
            !ChatUrlScope.IsPersistentConversation(c.Url, project.UrlPrefix)))
            throw new InvalidOperationException(
                "PROJECT_LIST_SCOPE_MISMATCH: chat declara projeto mas sua " +
                "URL não pertence ao mesmo escopo. Revise chats.json.");
        var completed = allChats.Where(c =>
                string.Equals(c.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) ||
                (c.ProjectId is null &&
                 ChatUrlScope.IsPersistentConversation(c.Url, project.UrlPrefix)))
            .Select(c => new ProjectChatEntry(c.Name, c.DisplayTitle, c.Url,
                "REGISTERED", projectId, c.FullName, c.FullNameSource));
        var pending = ListPending()
            .Where(c => string.Equals(c.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ProjectChatEntry(c.Name, c.DisplayTitle, null, c.State, projectId));
        return completed.Concat(pending).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private ProjectState Read()
    {
        if (!File.Exists(_path)) return new ProjectState();
        var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("PROJECTS_JSON_INVALID: arquivo sem objeto.");
        if (state.Projects is null || state.PendingChats is null ||
            state.NextProjectNumber < 0 ||
            state.NextGlobalChatNumber < Chat.InitialStandaloneSequence)
            throw new InvalidDataException("PROJECTS_JSON_INVALID: contadores ou listas inválidos.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in state.Projects)
        {
            // JSON externo pode conter nulos mesmo que o modelo seja não anulável.
            // Validar ANTES de inserir em HashSet evita CS8604 e corrupção silenciosa.
            if (project is null ||
                string.IsNullOrWhiteSpace(project.Id) ||
                string.IsNullOrWhiteSpace(project.UrlPrefix) ||
                !Regex.IsMatch(project.Id, @"^PRO_\d{4,}$",
                    RegexOptions.CultureInvariant) ||
                project.NextChatNumber < 0)
                throw new InvalidDataException(
                    "PROJECTS_JSON_INVALID: identificador de projeto inválido; esperado PRO_0000.");
            if (!ids.Add(project.Id) ||
                !urls.Add(project.UrlPrefix) ||
                !string.Equals(ChatUrlScope.ProjectPrefix(project.UrlPrefix),
                    project.UrlPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("PROJECTS_JSON_INVALID: projeto duplicado ou URL inválida.");
        }

        // Reabertura não pode perder a identidade anterior nem deixar um pin
        // ativo em estado de intenção (reutilização de reserva após fechamento).
        var pendingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in state.PendingChats)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name))
                throw new InvalidDataException("PROJECTS_JSON_INVALID: nome de reserva vazio.");
            if (item.ReopenCount < 0 ||
                ((item.State is "REOPEN_REQUESTED" or "REOPEN_ATTEMPT_STARTED") &&
                 (item.WindowHandle is not null || item.TabRuntimeId is not null ||
                  item.PreviousWindowHandle is null ||
                  string.IsNullOrWhiteSpace(item.PreviousTabRuntimeId))) ||
                !pendingNames.Add(item.Name) ||
                !Regex.IsMatch(item.Name,
                    item.ProjectId is null
                        ? @"^GPTBOT_\d{4,}$" : @"^PRO_\d{4,}-GPTBOT_\d{4,}$",
                    RegexOptions.CultureInvariant) ||
                (item.ProjectId is not null &&
                 (!ids.Contains(item.ProjectId) ||
                  !item.Name.StartsWith(item.ProjectId + "-GPTBOT_",
                      StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "PROJECTS_JSON_INVALID: reserva com ID incorreto ou projeto inexistente.");
        }
        return state;
    }

    private void Save(ProjectState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state,
            new JsonSerializerOptions { WriteIndented = true }));
        try { File.Move(tmp, _path, overwrite: true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private IDisposable Lock()
    {
        var id = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, @"Local\ChatAutomation.Projects." + id);
        try
        {
            var acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("PROJECT_REGISTRY_LOCK_TIMEOUT");
            return new HeldLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class HeldLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
