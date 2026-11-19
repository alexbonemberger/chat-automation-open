using ChatAutomation.State;

namespace ChatAutomation.Domain;

// Os projetos representam objetos do catálogo; o contexto USE é de sessão.
public sealed class Project
{
    public const string DefaultIdPrefix = "PRO_";
    private readonly WebInterface _owner;
    internal ProjectRecord Record { get; private set; }

    internal Project(WebInterface owner, ProjectRecord record)
    {
        _owner = owner;
        Record = record;
    }

    public string Id => Record.Id;
    public string FullName => Record.FullName;
    public string UrlPrefix => Record.UrlPrefix;
    public string id => Id;
    public string fullName => FullName;
    public string urlPrefix => UrlPrefix;

    // Índice estático de leitura, atualizado das fontes persistidas em cada
    // projectListChats(). Não é origem autoritativa: projects.json/chats.json são.
    private static readonly object SnapshotGate = new();
    private static readonly Dictionary<string, IReadOnlyList<ProjectChatEntry>> Snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<ProjectChatEntry>> RegisteredChats
    {
        get
        {
            lock (SnapshotGate)
                return new Dictionary<string, IReadOnlyList<ProjectChatEntry>>(Snapshots,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    internal void UpdateRecord(ProjectRecord updated) => Record = updated;

    internal static void TrackRegisteredChats(string projectId, IReadOnlyList<ProjectChatEntry> entries)
    {
        lock (SnapshotGate) Snapshots[projectId] = entries;
    }

    public IReadOnlyList<ProjectChatEntry> ProjectListChats() => _owner.ProjectListChats(this);

    public IReadOnlyList<ProjectChatEntry> projectListChats() => ProjectListChats();
}

public sealed record NewChatResult(
    string Status,
    string Name,
    string? DisplayTitle,
    string? ProjectId,
    string? Url,
    string? OpenedUrl,
    long? WindowHandle,
    string? TabRuntimeId,
    string? Note,
    string? FullName = null);
