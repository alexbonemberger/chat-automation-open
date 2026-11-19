using System.Diagnostics;
using System.Runtime.InteropServices;
using ChatAutomation.Browser;
using ChatAutomation.Commands;
using ChatAutomation.Features;
using ChatAutomation.State;
using FlaUI.UIA3;

namespace ChatAutomation.Domain;

public enum Mode { NewTab, NewWindow }

// Catálogo de chats registrados. Não consulta automaticamente o histórico remoto.
public sealed class WebInterface : IDisposable
{
    private readonly Lazy<UIA3Automation> _uia = new(() => new UIA3Automation());
    private readonly string _catalogPath;
    private ChatRegistry _registry;
    private readonly AutomationSettings _settings;
    private readonly Dictionary<string, Chat> _instances = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedChatName; // Seleção temporária da sessão; não muda a guia do Edge.
    private readonly ProjectRegistry _projectRegistry;
    private readonly Dictionary<string, Project> _projects = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedProjectId; // None por padrão; independente de USE de chat.

    public WebInterface(string? catalogPath = null, bool migrateLegacy = false, string? settingsPath = null)
    {
        _catalogPath = catalogPath ?? ChatRegistry.FilePath;
        _settings = new AutomationSettings(settingsPath);
        _registry = new ChatRegistry(_catalogPath, migrateLegacy);
        var projectPath = Path.Combine(Path.GetDirectoryName(_catalogPath)!, "projects.json");
        _projectRegistry = new ProjectRegistry(projectPath, _catalogPath);
        foreach (var record in _registry.ListChats())
            _instances[record.Name] = new Chat(this, record);
        ReloadProjects();
    }

    internal UIA3Automation Automation => _uia.Value;

    // Preferência global da sessão, persistida a cada alteração.
    // Habilitada: sendMessage() sem argumento usa o conteúdo ATUAL do editor.
    // Desabilitada (padrão): exige setText() nesta sessão.
    public bool EnableSendHumanText
    {
        get => _settings.EnableSendHumanText;
        set => _settings.EnableSendHumanText = value;
    }
    public bool enableSendHumanText
    {
        get => EnableSendHumanText;
        set => EnableSendHumanText = value;
    }
    public bool SetEnableSendHumanText(bool enabled)
    {
        EnableSendHumanText = enabled;
        return EnableSendHumanText;
    }

    public IReadOnlyList<Chat> Chats => _instances.Values
        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<ChatRecord> ListChats() => _registry.ListChats();
    public IReadOnlyList<ChatRecord> listChats() => ListChats();

    public Chat GetChat(string name) => _instances.TryGetValue(name, out var chat) ? chat
        : throw new KeyNotFoundException($"Chat '{name}' não registrado. Execute listChats().");
    public Chat this[string name] => GetChat(name);

    // Leitura da UIA de uma guia JÁ selecionada do projeto. Não movimenta o
    // Edge, não abre guia, não reserva IDs e não altera o contexto USE.
    public ProjectActionInspection InspectProjectNewChat()
    {
        ReloadProjects();
        var project = CurrentProject ??
            throw new InvalidOperationException(
                "PROJECT_NOT_SELECTED: execute use project \"PRO_0000\".");
        return ProjectNewChatInspector.Inspect(
            Automation, project.UrlPrefix, project.FullName);
    }
    public ProjectActionInspection inspectProjectNewChat() => InspectProjectNewChat();

    // Equivalente ao USE de um banco: seleciona o contexto para os comandos
    // sem prefixo dentro desta sessão; não altera o foco da janela do Edge.
    public string? CurrentChatName => _selectedChatName;
    public Chat? CurrentChat => _selectedChatName is null ? null : GetChat(_selectedChatName);
    public Chat UseChat(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var chat = GetChat(name);
        ReloadProjects();
        ValidateDeclaredProjectScope(chat.Record);
        // Contexto do console é único: selecionar chat seleciona também
        // seu projeto. Chats legados sem ProjectId usam escopo da URL.
        var projectId = ChatProjectId(chat);
        if (projectId is not null && !_projects.ContainsKey(projectId))
            throw new InvalidOperationException(
                "CHAT_PROJECT_NOT_REGISTERED: projeto do chat não existe no catálogo.");
        _selectedChatName = chat.Name;
        _selectedProjectId = projectId;
        return chat;
    }

    // ProjectId armazenado não pode contradizer o projeto da URL.
    // O nome do projeto é metadado, não autorização para desviar
    // operações para uma conversa fora do seu escopo.
    private void ValidateDeclaredProjectScope(ChatRecord record)
    {
        if (record.ProjectId is null) return; // Legados usam a URL.
        if (!_projects.TryGetValue(record.ProjectId, out var project))
            throw new InvalidOperationException(
                "CHAT_PROJECT_NOT_REGISTERED: registro refere projeto ausente.");
        if (!ChatUrlScope.IsPersistentConversation(record.Url, project.UrlPrefix))
            throw new InvalidOperationException(
                "CHAT_PROJECT_SCOPE_MISMATCH: registro declara " +
                record.ProjectId + " mas a URL pertence a outro escopo.");
    }

    private string? ChatProjectId(Chat chat) =>
        chat.Record.ProjectId ?? ResolveProjectForUrl(chat.Url);
    public Chat use(string name) => UseChat(name);
    public Chat RequireCurrentChat() => CurrentChat ?? throw new InvalidOperationException(
        "Nenhum chat selecionado. Execute use \"chat00\" ou use \"nome\" antes de chamar métodos sem prefixo.");

    // Projetos seguem USE independente do chat. None é o padrão de cada sessão.
    public string CurrentProjectId => _selectedProjectId ?? "None";
    public Project? CurrentProject => _selectedProjectId is null ? null : GetProject(_selectedProjectId);
    private ProjectNamePreview? _projectNamePreview;

    // A UIA preenche o nome para revisão; nenhuma gravação é feita aqui.
    public ProjectNamePreview PreviewProjectFromEdge()
    {
        var preview = ProjectNameInspector.Preview(Automation);
        _projectNamePreview = preview;
        return preview;
    }
    public ProjectNamePreview previewProjectFromEdge() => PreviewProjectFromEdge();

    // Confirma SOMENTE a prévia vista nesta sessão. Reconsulta URL, RuntimeId
    // e nome no Edge antes de persistir; nunca infere nome do slug do projeto.
    public Project AddProjectFromEdge()
    {
        var approved = _projectNamePreview ??
            throw new InvalidOperationException(
                "PROJECT_PREVIEW_REQUIRED: execute web.previewProjectFromEdge() " +
                "e valide o nome antes de confirmar.");
        var current = ProjectNameInspector.Preview(Automation);
        if (!ProjectNameInspector.SamePreviewIdentity(approved, current))
            throw new InvalidOperationException(
                "PROJECT_PREVIEW_CHANGED: nome, URL ou guia alterados " +
                "desde a prévia. Execute web.previewProjectFromEdge() novamente.");
        var registered = AddProject(current.FullName, current.ProjectUrl);
        _projectNamePreview = null;
        return registered;
    }
    public Project addProjectFromEdge() => AddProjectFromEdge();


    public IReadOnlyList<ProjectRecord> ListProjects() => _projectRegistry.ListProjects();
    public IReadOnlyList<ProjectRecord> listProjects() => ListProjects();
    public IReadOnlyList<PendingNewChatRecord> PendingNewChats() => _projectRegistry.ListPending();
    public IReadOnlyList<PendingNewChatRecord> pendingNewChats() => PendingNewChats();

    public Project GetProject(string id)
    {
        ReloadProjects(); // Inclui projetos cadastrados por outro processo.
        return _projects.TryGetValue(id, out var value) ? value
            : throw new KeyNotFoundException("PROJECT_NOT_FOUND: " + id);
    }

    private void ReloadProjects()
    {
        var records = _projectRegistry.ListProjects();
        var ids = records.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _projects.Keys.Where(id => !ids.Contains(id)).ToArray())
            _projects.Remove(id);
        foreach (var record in records)
        {
            if (_projects.TryGetValue(record.Id, out var project)) project.UpdateRecord(record);
            else _projects[record.Id] = new Project(this, record);
        }
        if (_selectedProjectId is not null && !_projects.ContainsKey(_selectedProjectId))
            _selectedProjectId = null;
    }

    public Project AddProject(string fullName, string urlPrefix)
    {
        var added = _projectRegistry.AddProject(fullName, urlPrefix);
        ReloadProjects();
        return GetProject(added.Id);
    }
    public Project addProject(string fullName, string urlPrefix) => AddProject(fullName, urlPrefix);
    // Reaproveita a URL de uma conversa já cadastrada: evita digitação de
    // URLs longas no console e não interage com o Edge.
    public Project AddProjectFromChat(string fullName, string chatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatName);
        Refresh();
        var chat = GetChat(chatName);
        return AddProject(fullName, chat.Url);
    }
    public Project addProjectFromChat(string fullName, string chatName) =>
        AddProjectFromChat(fullName, chatName);


    public Project? UseProject(string? projectId)
    {
        ReloadProjects();
        var project = string.IsNullOrWhiteSpace(projectId) ||
            string.Equals(projectId, "None", StringComparison.OrdinalIgnoreCase)
            ? null : GetProject(projectId);
        // Selecionar outro projeto (ou None) torna incompatível o chat
        // anterior. Desselecionar apenas no console, nunca remover registro.
        if (_selectedChatName is not null &&
            !string.Equals(ChatProjectId(GetChat(_selectedChatName)),
                project?.Id, StringComparison.OrdinalIgnoreCase))
            _selectedChatName = null;
        _selectedProjectId = project?.Id;
        return project;
    }
    public Project? UseProject(Project? project) => UseProject(project?.Id);
    public Project? useProject(string? id) => UseProject(id);

    public IReadOnlyList<ProjectChatEntry> ProjectListChats(Project? project = null)
    {
        var selected = project ?? CurrentProject ??
            throw new InvalidOperationException(
                "PROJECT_NONE: selecione um projeto com use project \"PRO_0000\".");
        ReloadProjects();
        if (!string.Equals(GetProject(selected.Id).UrlPrefix, selected.UrlPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("PROJECT_IDENTITY_CHANGED");
        var chats = _projectRegistry.ProjectListChats(selected.Id);
        Project.TrackRegisteredChats(selected.Id, chats);
        return chats;
    }
    public IReadOnlyList<ProjectChatEntry> projectListChats() => ProjectListChats();

    // Prévia sem abrir Edge e sem consumir contador. A reserva real pode
    // avançar se outro processo criar chats entre preview e addNewChat.
    public NewChatPreview PreviewNewChat(string? displayTitle = null, Project? project = null)
    {
        ReloadProjects();
        var selected = project ?? CurrentProject;
        if (selected is not null &&
            !string.Equals(GetProject(selected.Id).UrlPrefix, selected.UrlPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("PROJECT_IDENTITY_CHANGED");
        return _projectRegistry.Preview(displayTitle, selected?.Id);
    }
    public NewChatPreview previewNewChat() => PreviewNewChat();
    public NewChatPreview previewNewChat(string title) => PreviewNewChat(title);
    public NewChatPreview previewNewChat(Project project) => PreviewNewChat(null, project);
    public NewChatPreview previewNewChat(string title, Project project) => PreviewNewChat(title, project);

    // Sobrecargas pedidas: sem título, com título, com Project, título + Project.
    // Quando não há Project explícito, usa o projeto da sessão (padrão None).
    public NewChatResult AddNewChat() => StartNewChat(null, CurrentProject);
    public NewChatResult AddNewChat(string displayTitle) => StartNewChat(displayTitle, CurrentProject);
    public NewChatResult AddNewChat(Project project) => StartNewChat(null, project);
    public NewChatResult AddNewChat(string displayTitle, Project project) =>
        StartNewChat(displayTitle, project);
    public NewChatResult addNewChat() => AddNewChat();
    public NewChatResult addNewChat(string displayTitle) => AddNewChat(displayTitle);
    public NewChatResult addNewChat(Project project) => AddNewChat(project);
    public NewChatResult addNewChat(string displayTitle, Project project) => AddNewChat(displayTitle, project);

    private NewChatResult StartNewChat(string? displayTitle, Project? project)
    {
        ReloadProjects();
        if (project is not null &&
            !string.Equals(GetProject(project.Id).UrlPrefix, project.UrlPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("PROJECT_IDENTITY_CHANGED");
        var pending = _projectRegistry.Reserve(displayTitle, project?.Id);
        return OpenReservedNewChat(pending.Name);
    }

    // Recuperação explícita da MESMA reserva: não incrementa o contador.
    // Caso RESERVED sem pin (como a falha original), abre uma nova guia com
    // o mesmo ID. Caso TAB_CREATED, reutiliza o HWND + RuntimeId persistidos.
    public NewChatResult ResumeNewChat(string name) => OpenReservedNewChat(name);
    public NewChatResult resumeNewChat(string name) => ResumeNewChat(name);

    // O usuário confirmou que a guia original foi fechada. Esta chamada
    // explicita a intenção de reabrir outra guia com a MESMA reserva.
    // Ausência do RuntimeId não é por si só prova de fechamento; por isso
    // resumeNewChat() jamais toma essa decisão automaticamente.
    public NewChatResult ReopenClosedNewChat(string name) =>
        OpenReservedNewChat(name, closedTabReopen: true);
    public NewChatResult reopenClosedNewChat(string name) => ReopenClosedNewChat(name);

    private NewChatResult OpenReservedNewChat(string name, bool closedTabReopen = false)
    {
        using var lease = _projectRegistry.AcquireChatOperation(name);
        Refresh();
        var pending = _projectRegistry.GetPending(name);
        if (closedTabReopen)
        {
            if (pending.State != "TAB_CREATED" ||
                pending.WindowHandle is null ||
                string.IsNullOrWhiteSpace(pending.TabRuntimeId))
                throw new InvalidOperationException(
                    "NEW_CHAT_REOPEN_NOT_ALLOWED: somente uma reserva TAB_CREATED " +
                    "com pin anterior pode ser reaberta. Nenhum contador alterado.");
            if (!EdgeBrowser.IsNewChatPinAbsent(Automation,
                pending.WindowHandle.Value, pending.TabRuntimeId))
                throw new InvalidOperationException(
                    "NEW_CHAT_PIN_STILL_PRESENT: guia original encontrada; use " +
                    "web.resumeNewChat(\"" + name + "\") em vez de reabrir.");
            pending = _projectRegistry.AuthorizeClosedTabReopen(
                pending.Name, pending.WindowHandle.Value, pending.TabRuntimeId);
        }
        if (pending.State == "REOPEN_REQUESTED" && !closedTabReopen)
            throw new InvalidOperationException(
                "NEW_CHAT_REOPEN_UNCERTAIN: intenção de reabertura já persistida, " +
                "mas ainda sem prova da guia. Não repetir automaticamente.");
        if (pending.State == "REOPEN_ATTEMPT_STARTED")
            throw new InvalidOperationException(
                "NEW_CHAT_REOPEN_UNCERTAIN: Ctrl+T já pode ter sido acionado, " +
                "sem pin confirmado. Não abrir outra guia cegamente.");
        var project = pending.ProjectId is null ? null : GetProject(pending.ProjectId);
        if (project is not null &&
            !string.Equals(pending.LandingUrl, project.UrlPrefix, StringComparison.Ordinal) &&
            !string.Equals(pending.LandingUrl,
                ChatUrlScope.ProjectPage(project.UrlPrefix), StringComparison.Ordinal))
            throw new InvalidOperationException("PROJECT_IDENTITY_CHANGED");
        if (pending.State == "OPENED_PENDING_URL")
            return new NewChatResult("OPENED_PENDING_URL", pending.Name, pending.DisplayTitle,
                pending.ProjectId, null, pending.LastObservedUrl,
                pending.WindowHandle, pending.TabRuntimeId,
                "Guia já aberta. Após a primeira mensagem explícita, " +
                "execute web.completeNewChat(\"" + pending.Name + "\").");

        try
        {
            NewChatOpenEvidence opened;
            if ((pending.State == "RESERVED" ||
                 pending.State == "REOPEN_REQUESTED") &&
                pending.WindowHandle is null && pending.TabRuntimeId is null)
            {
                var targetHwnd = pending.PreviousWindowHandle ??
                    CurrentChat?.Record.PreferredWindowHandle;
                if (targetHwnd is not null && !EdgeBrowser.Windows()
                    .Any(h => h.ToInt64() == targetHwnd))
                    targetHwnd = null;
                opened = NewChatLauncher.Open(Automation, pending.LandingUrl,
                    project?.UrlPrefix, targetHwnd,
                    created => _projectRegistry.MarkTabCreated(pending.Name,
                        created.WindowHandle, created.RuntimeId),
                    beforeUrl => _projectRegistry.MarkProjectActionAttempted(
                        pending.Name, beforeUrl),
                    onBeforeTabOpen: pending.State == "REOPEN_REQUESTED"
                        ? () => _projectRegistry.MarkReopenAttemptStarted(pending.Name)
                        : null);
            }
            else if ((pending.State == "TAB_CREATED" ||
                      pending.State == "PROJECT_ACTION_ATTEMPTED") &&
                pending.WindowHandle is not null &&
                !string.IsNullOrWhiteSpace(pending.TabRuntimeId))
            {
                opened = NewChatLauncher.Resume(Automation,
                    pending.WindowHandle.Value, pending.TabRuntimeId,
                    project?.UrlPrefix,
                    actionAttempted: pending.State == "PROJECT_ACTION_ATTEMPTED",
                    beforeActionUrl: pending.State == "PROJECT_ACTION_ATTEMPTED"
                        ? pending.LastObservedUrl : null);
            }
            else throw new InvalidOperationException(
                "NEW_CHAT_RECOVERY_STATE_INVALID: consulte pendingNewChats().");

            if (opened.Status == "PAUSED_FOCUS_CHANGED")
            {
                // Nenhum URL/editor foi comprovado; mantém TAB_CREATED ou
                // PROJECT_ACTION_ATTEMPTED com HWND/RuntimeId persistidos.
                // Não marca OPENED_PENDING_URL nem consome outro ID.
                var savedPending = _projectRegistry.GetPending(pending.Name);
                return new NewChatResult("PAUSED_FOCUS_CHANGED",
                    savedPending.Name, savedPending.DisplayTitle,
                    savedPending.ProjectId, null,
                    string.IsNullOrWhiteSpace(opened.ObservedUrl)
                        ? null : opened.ObservedUrl,
                    savedPending.WindowHandle, savedPending.TabRuntimeId,
                    "O foco ou a guia selecionada mudou durante a observação. " +
                    "O pin e a reserva foram preservados, sem clique nem envio. " +
                    "Para continuar, execute web.resumeNewChat(\"" +
                    savedPending.Name + "\"); não use addNewChat/reopenClosedNewChat.");
            }
            _projectRegistry.MarkOpened(pending.Name, opened.WindowHandle,
                opened.TabRuntimeId, opened.ObservedUrl);
            return new NewChatResult("OPENED_PENDING_URL", pending.Name,
                pending.DisplayTitle, pending.ProjectId, null, opened.ObservedUrl,
                opened.WindowHandle, opened.TabRuntimeId,
                "A guia correta foi comprovada; nenhuma mensagem enviada. " +
                "Após a primeira mensagem, execute web.completeNewChat(\"" +
                pending.Name + "\").");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"NEW_CHAT_OPEN_UNCERTAIN: reserva {pending.Name} preservada. " +
                "Consulte pendingNewChats(). Use web.resumeNewChat(\"" +
                pending.Name + "\") somente quando o pin existir. Se a " +
                "guia foi fechada e o estado for TAB_CREATED, use " +
                "web.reopenClosedNewChat(\"" + pending.Name +
                "\") de forma explícita. " + ex.Message, ex);
        }
    }

    // HRESULT observado no Edge/UIA durante a segunda criação (18/11).
    // Somente leituras e seleção da MESMA guia podem ser repetidas após
    // esse erro. Digitação, clique e envio jamais são repetidos.
    internal static bool IsTransientUiaCom(COMException ex) =>
        ex.HResult == unchecked((int)0x80040201);

    // Fluxo V7.1: a guia /project existente se transforma na conversa /c/.
    // Não executa Ctrl+T nem navega a outra aba. Uma reserva pendente do
    // mesmo projeto tem precedência; nunca cria outro ID para contornar falha.
    // A chamada com mensagem representa autorização EXPLÍCITA de um envio.
    public NewChatResult CreateProjectChat(string projectId, string firstMessage,
        string? displayTitle = null)
    {
        try { return CreateProjectChatCore(projectId, firstMessage, displayTitle); }
        catch (COMException ex)
        {
            // UIA pode lançar 0x80040201 depois do único clique. Não
            // disparar outra mensagem nem abandonar a reserva persistida.
            var pending = _projectRegistry.ListPending()
                .Where(p => string.Equals(p.ProjectId, projectId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (pending.Length != 1)
                throw new InvalidOperationException(
                    "PROJECT_COMPOSE_COM_UNCERTAIN: HRESULT=0x" +
                    ex.HResult.ToString("X8") + "; consulte pendingNewChats() " +
                    "antes de tentar outra criação.", ex);
            var item = pending[0];
            if (item.State == "FIRST_SEND_ATTEMPTED")
            {
                // Recuperar por leitura da mesma guia; NÃO reenviar.
                try { return ReconcileProjectChat(item.Name); }
                catch (COMException) { /* UIA ainda indisponível. */ }
            }
            return new NewChatResult("UIA_COM_OPERATION_UNCERTAIN",
                item.Name, item.DisplayTitle, item.ProjectId, null,
                item.LastObservedUrl, item.WindowHandle, item.TabRuntimeId,
                "HRESULT=0x" + ex.HResult.ToString("X8") + "; " +
                "reserva " + item.State + " preservada. Não repetir envio. " +
                "Use web.reconcileProjectChat para verificar a guia.");
        }
    }

    internal static bool CanRecoverUntouchedReservedChat(
        PendingNewChatRecord pending) =>
        pending.State == "RESERVED" && pending.WindowHandle is null &&
        string.IsNullOrWhiteSpace(pending.TabRuntimeId) &&
        pending.LastObservedUrl is null;

    private NewChatResult CreateProjectChatCore(string projectId,
        string firstMessage, string? displayTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (string.IsNullOrWhiteSpace(firstMessage))
            throw new ArgumentException("PROJECT_FIRST_MESSAGE_EMPTY");
        ReloadProjects();
        var project = GetProject(projectId);
        var composeUrl = ChatUrlScope.ProjectPage(project.UrlPrefix);

        // Coordena duas instâncias usando o mesmo projeto/catálogo. Não
        // substitui o mutex de cada reserva usado pelas ações de envio.
        using var projectLease = _projectRegistry.AcquireChatOperation(
            "PROJECT_COMPOSE:" + project.Id);
        var candidates = _projectRegistry.ListPending()
            .Where(p => string.Equals(p.ProjectId, project.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length > 1)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_PENDING_AMBIGUOUS: várias reservas neste projeto. " +
                "Resolva pendingNewChats() antes de iniciar outra conversa.");
        var pending = candidates.SingleOrDefault();
        var preexistingReservation = pending is not null;
        if (pending is not null && displayTitle is not null &&
            !string.Equals(pending.DisplayTitle, displayTitle.Trim(),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_PENDING_TITLE_CONFLICT: reserva existente tem outro título.");

        if (pending is null)
        {
            // Só consumir ID depois de localizar um compositor na guia
            // selecionada e confirmar URL/RuntimeId/editor em modo passivo.
            var selected = FindSelectedProjectComposeTab(project);
            var binding = new ChatBinding(composeUrl, DateTimeOffset.UtcNow,
                "project-compose-existing-tab", selected.WindowHandle,
                null, selected.RuntimeId);
            _ = ChatFeatures.DetectTextAreaPassive(Automation, binding);
            pending = _projectRegistry.Reserve(displayTitle, project.Id);
            _projectRegistry.MarkTabCreated(pending.Name, selected.WindowHandle,
                selected.RuntimeId); // "TAB_CREATED" legado = guia vinculada.
        }

        using var chatLease = _projectRegistry.AcquireChatOperation(pending.Name);
        pending = _projectRegistry.GetPending(pending.Name);
        if (pending.State == "FIRST_SEND_ATTEMPTED")
            return ObserveAndRegisterProjectChat(pending.Name, project);

        if (preexistingReservation && CanRecoverUntouchedReservedChat(pending))
        {
            // Corrige estado antes inalcançável: a aplicação pode cair
            // depois de Reserve() e ANTES de gravar o pin. RESERVED sem
            // guia/URL/ação é comprovadamente anterior ao primeiro envio;
            // revalidar /project e editor, sem abrir nova aba.
            var selected = FindSelectedProjectComposeTab(project);
            var binding = new ChatBinding(composeUrl, DateTimeOffset.UtcNow,
                "project-compose-reserved-tab", selected.WindowHandle,
                null, selected.RuntimeId);
            _ = ChatFeatures.DetectTextAreaPassive(Automation, binding);
            pending = _projectRegistry.MarkTabCreated(pending.Name,
                selected.WindowHandle, selected.RuntimeId);
        }
        else if (preexistingReservation)
        {
            // TAB_CREATED/OPENED_PENDING_URL podem ter sofrido interação
            // externa após a falha. Somente observar/reconciliar; não
            // repetir digitação/clique por suposição.
            return ReconcileProjectChatPinned(pending, project);
        }

        if (pending.State == "TAB_CREATED")
        {
            if (pending.WindowHandle is null ||
                string.IsNullOrWhiteSpace(pending.TabRuntimeId))
                throw new InvalidOperationException("PROJECT_COMPOSE_PIN_MISSING");
            // Reusa EXATAMENTE o pin da reserva V7.0; mesmo que outra
            // guia /project esteja aberta, nunca adota outra identidade.
            var tab = EdgeBrowser.ActivateTabAndReadCurrentUrl(Automation,
                pending.WindowHandle.Value, pending.TabRuntimeId);
            if (!UrlIdentity.Exact(tab.Url, composeUrl))
                throw new InvalidOperationException(
                    "PROJECT_COMPOSE_SCOPE_CHANGED: guia reservada não está em /project.");
            var binding = new ChatBinding(composeUrl, DateTimeOffset.UtcNow,
                "project-compose-recovery", tab.Handle, null, tab.TabRuntimeId);
            _ = ChatFeatures.DetectTextAreaPassive(Automation, binding);
            pending = _projectRegistry.MarkOpened(pending.Name, tab.Handle,
                tab.TabRuntimeId, tab.Url);
        }

        if (pending.State != "OPENED_PENDING_URL" ||
            !UrlIdentity.Exact(pending.LastObservedUrl ?? string.Empty, composeUrl))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_STATE_UNSAFE: não enviar. " +
                "Consulte pendingNewChats() e preserve a reserva existente.");

        // Reaproveita readback, timestamp, verificação da guia e persistência
        // FIRST_SEND_ATTEMPTED ANTES do único clique já implementados.
        // Se SendFirst... falhar após esse limite, uma chamada subsequente
        // apenas verifica /c/ e NUNCA repete o envio.
        _ = SendFirstNewChatMessage(pending.Name, firstMessage);
        return ObserveAndRegisterProjectChat(pending.Name, project);
    }
    public NewChatResult createProjectChat(string projectId, string firstMessage,
        string? displayTitle = null) =>
        CreateProjectChat(projectId, firstMessage, displayTitle);

    // Reconciliação sem ENVIO de um envio anterior: apenas observa a UIA,
    // persiste o cadastro após URL definitiva e seleciona o chat registrado.
    // Não aceita mensagem, não abre guia nem reserva outro ID.
    public NewChatResult ReconcileProjectChat(string name)
    {
        using var chatLease = _projectRegistry.AcquireChatOperation(name);
        ReloadProjects();
        var pending = _projectRegistry.GetPending(name);
        if (pending.ProjectId is null)
            throw new InvalidOperationException(
                "PROJECT_RECONCILE_PROJECT_REQUIRED: reserva sem projeto.");
        var project = GetProject(pending.ProjectId);
        if (pending.State == "FIRST_SEND_ATTEMPTED")
            return ObserveAndRegisterProjectChat(name, project);
        return ReconcileProjectChatPinned(pending, project);
    }

    // Quando COM falha antes de gravar FIRST_SEND_ATTEMPTED, a mensagem
    // pode ter sido enviada fora da transação. Apenas uma URL definitiva
    // na MESMA guia e no MESMO projeto permite registrar o chat observado.
    // Nunca navega, digita ou clica no compositor.
    private NewChatResult ReconcileProjectChatPinned(
        PendingNewChatRecord pending, Project project)
    {
        if (pending.State is not ("TAB_CREATED" or "OPENED_PENDING_URL") ||
            pending.WindowHandle is null ||
            string.IsNullOrWhiteSpace(pending.TabRuntimeId))
            throw new InvalidOperationException(
                "PROJECT_RECONCILE_NO_SEND_EVIDENCE: estado ou pin insuficientes. " +
                "Consulte pendingNewChats() sem reenviar mensagem.");
        TabMatch? tab = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                tab = EdgeBrowser.ActivateTabAndReadCurrentUrl(Automation,
                    pending.WindowHandle.Value, pending.TabRuntimeId);
                break;
            }
            catch (COMException ex) when (IsTransientUiaCom(ex))
            {
                Thread.Sleep(180); // Sem teclado, clique ou reenvio.
            }
        }
        if (tab is null)
            return new NewChatResult("PROJECT_RECONCILE_UIA_UNAVAILABLE",
                pending.Name, pending.DisplayTitle, project.Id, null,
                pending.LastObservedUrl, pending.WindowHandle, pending.TabRuntimeId,
                "Erro UIA transitório 0x80040201; reserva preservada. " +
                "Nenhuma mensagem enviada. Reexecute somente a reconciliação.");
        if (!ChatUrlScope.IsPersistentConversation(tab.Url, project.UrlPrefix))
            return new NewChatResult("PROJECT_RECONCILE_URL_NOT_FINAL",
                pending.Name, pending.DisplayTitle, project.Id, null,
                tab.Url, pending.WindowHandle, pending.TabRuntimeId,
                "Não há URL definitiva da conversa na guia reservada. " +
                "Nenhuma mensagem enviada; reserva preservada.");
        if (pending.State == "TAB_CREATED")
            _projectRegistry.MarkOpened(pending.Name, tab.Handle,
                tab.TabRuntimeId, tab.Url);
        // As mesmas exigências de idle da criação automática também
        // valem para reconciliações de uma mensagem enviada fora do fluxo.
        return WaitForResponseAndRegisterProjectChat(pending, project, tab.Url);
    }
    public NewChatResult reconcileProjectChat(string name) => ReconcileProjectChat(name);

    // Localiza /project já selecionado ou reaproveita a guia de uma
    // conversa REGISTRADA do mesmo projeto. Não cria nova guia.
    // Em caso de múltiplas candidatas sem USE explícito, bloqueia.
    // Quando o projeto já foi aberto, o console pode iniciar sem USE
    // e a guia /project pode estar em segundo plano. Examinar URLs
    // exatas das guias e restaurar a seleção original durante a busca.
    // Nunca escolher por título, índice, primeira guia ou navegador ativo.
    internal static bool CanRecoverUniqueProjectComposeScan(ScanReport scan) =>
        scan.Warnings.Count == 0 && scan.Matches.Count == 1 &&
        !string.IsNullOrWhiteSpace(scan.Matches[0].TabRuntimeId);

    private SelectedTabState? RecoverExistingProjectComposeTab(string composeUrl)
    {
        var scan = EdgeBrowser.ScanExact(Automation, composeUrl);
        if (scan.Matches.Count > 1)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_PAGE_AMBIGUOUS: a página /project existe em " +
                "mais de uma guia; nenhuma guia escolhida automaticamente.");
        if (scan.Warnings.Count > 0)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SCAN_INCOMPLETE: não foi possível verificar " +
                "todas as guias com segurança. " +
                string.Join(" | ", scan.Warnings.Take(3)));
        if (scan.Matches.Count == 0) return null;
        if (!CanRecoverUniqueProjectComposeScan(scan))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_TAB_IDENTITY_MISSING: página /project sem " +
                "RuntimeId único; nenhuma navegação ou envio realizado.");

        // A busca restaurou as abas anteriores. Agora ativa somente o
        // RuntimeId que comprovadamente tinha a URL exata do projeto.
        var activated = EdgeBrowser.ActivateExactTab(Automation, scan.Matches[0]);
        if (!UrlIdentity.Exact(activated.Url, composeUrl) ||
            string.IsNullOrWhiteSpace(activated.TabRuntimeId))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_RECOVERY_CHANGED: URL ou pin divergiu.");
        return new SelectedTabState(activated.Handle,
            activated.TabIndex, activated.TabRuntimeId);
    }

    private SelectedTabState FindSelectedProjectComposeTab(Project project)
    {
        var composeUrl = ChatUrlScope.ProjectPage(project.UrlPrefix);
        var composeMatches = new List<SelectedTabState>();
        foreach (var hwnd in EdgeBrowser.Windows())
        {
            try
            {
                var selected = EdgeBrowser.SelectedTab(Automation, hwnd);
                if (UrlIdentity.Exact(EdgeBrowser.ReadActiveUrl(Automation, hwnd),
                        composeUrl))
                    composeMatches.Add(selected);
            }
            catch (InvalidOperationException) { /* Edge em transição */ }
        }
        if (composeMatches.Count > 1)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_PAGE_AMBIGUOUS: várias janelas exibem /project.");
        if (composeMatches.Count == 1) return composeMatches[0];

        // /project em segundo plano continua sendo origem válida. A
        // recuperação ocorre sem nova guia, sem rascunho, sem reserva ou
        // envio e só quando o scan global comprova unicidade e RuntimeId.
        var recovered = RecoverExistingProjectComposeTab(composeUrl);
        if (recovered is not null) return recovered;

        // A seleção do console é explícita e tem prioridade. Não se escolhe
        // pela posição da guia, pelo título visual ou pelo nome do projeto.
        var records = _registry.ListChats()
            .Where(c => ChatUrlScope.IsPersistentConversation(c.Url,
                project.UrlPrefix) && c.PreferredWindowHandle is not null &&
                !string.IsNullOrWhiteSpace(c.PreferredTabRuntimeId)).ToArray();
        ChatRecord? source = _selectedChatName is null ? null :
            records.FirstOrDefault(c => c.Name.Equals(_selectedChatName,
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            // Sem USE de chat, só permite a guia já selecionada no Edge,
            // quando ela estiver cadastrada no projeto com o MESMO pin UIA.
            var candidates = new List<ChatRecord>();
            foreach (var hwnd in EdgeBrowser.Windows())
            {
                try
                {
                    var selected = EdgeBrowser.SelectedTab(Automation, hwnd);
                    var url = EdgeBrowser.ReadActiveUrl(Automation, hwnd);
                    candidates.AddRange(records.Where(c =>
                        c.PreferredWindowHandle == selected.WindowHandle &&
                        string.Equals(c.PreferredTabRuntimeId, selected.RuntimeId,
                            StringComparison.Ordinal) &&
                        UrlIdentity.Exact(c.Url, url)));
                }
                catch (InvalidOperationException) { /* Edge em transição */ }
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    candidates.Count == 0
                    ? "PROJECT_COMPOSE_SOURCE_NOT_FOUND: não há /project ou " +
                      "conversa registrada do mesmo projeto selecionada."
                    : "PROJECT_COMPOSE_SOURCE_AMBIGUOUS: mais de uma guia " +
                      "do projeto está selecionada; selecione um chat com USE.");
            source = candidates[0];
        }

        // Leitura da guia de origem pelo pin; se houver qualquer rascunho
        // humano ou se URL/RuntimeId divergirem, NÃO navegar nem reservar ID.
        var verified = EdgeBrowser.ActivateTabAndReadCurrentUrl(Automation,
            source.PreferredWindowHandle!.Value, source.PreferredTabRuntimeId!);
        if (!UrlIdentity.Exact(verified.Url, source.Url) ||
            !string.Equals(verified.TabRuntimeId, source.PreferredTabRuntimeId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SOURCE_CHANGED: não reutilizar pin instável.");
        var oldBinding = new ChatBinding(source.Url, DateTimeOffset.UtcNow,
            "project-compose-source-readonly", verified.Handle, null,
            verified.TabRuntimeId);
        if (ChatFeatures.IsGenerating(Automation, oldBinding))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SOURCE_GENERATING: a conversa atual ainda " +
                "está gerando resposta; nenhuma navegação ou envio realizado.");
        var draft = ChatFeatures.GetText(Automation, oldBinding);
        if (!string.IsNullOrWhiteSpace(draft))
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_SOURCE_DRAFT_PRESENT: há texto não enviado " +
                "na conversa atual; nenhuma navegação ou envio realizado.");

        var navigated = NewChatLauncher.NavigatePinnedProjectConversationToComposer(
            Automation, verified.Handle, verified.TabRuntimeId!, source.Url,
            project.UrlPrefix);
        // Assim que /project for confirmado, o RuntimeId deixa de apontar
        // para a URL antiga, mesmo se a leitura do editor falhar depois.
        // Evita dois chats persistidos com o mesmo pin após uma retomada.
        _ = _registry.ReleaseTabPin(source.Name, source.Url,
            navigated.WindowHandle, navigated.RuntimeId);
        Refresh();
        var newBinding = new ChatBinding(composeUrl, DateTimeOffset.UtcNow,
            "project-compose-in-place", navigated.WindowHandle, null,
            navigated.RuntimeId);
        // Carregamento SPA/Edge pode recriar o editor durante alguns instantes.
        var verifiedEditor = false;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                _ = ChatFeatures.DetectTextAreaPassive(Automation, newBinding);
                verifiedEditor = true;
                break;
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(180);
            }
        }
        if (!verifiedEditor)
            throw new InvalidOperationException(
                "PROJECT_COMPOSE_EDITOR_NOT_READY: a guia foi direcionada " +
                "para /project, mas o editor UIA não foi confirmado. " +
                "Nenhuma mensagem enviada.");

        return navigated;
    }

    // IsGenerating() não é prova de entrega remota. Para não concluir
    // antes do início da geração, exigimos duas leituras ociosas após
    // ter observado busy=true; caso o início tenha sido rápido demais
    // para a UIA, exigimos janela ociosa estável mais longa.
    internal static bool ResponseIdleConfirmed(bool busyObserved,
        int consecutiveIdleReads, long elapsedMilliseconds) =>
        busyObserved ? consecutiveIdleReads >= 2
            : consecutiveIdleReads >= 8 && elapsedMilliseconds >= 2000;

    // Nenhum envio, clique nem navegação nesta etapa. Apenas observa
    // a mesma guia originalmente fixada, sua URL real e o estado busy.
    // Uma falha de observação não remove a reserva FIRST_SEND_ATTEMPTED:
    // ReconcileProjectChat retoma sem enviar a primeira mensagem.
    private NewChatResult WaitForResponseAndRegisterProjectChat(
        PendingNewChatRecord pending, Project project, string observedUrl)
    {
        if (pending.WindowHandle is null ||
            string.IsNullOrWhiteSpace(pending.TabRuntimeId) ||
            !ChatUrlScope.IsPersistentConversation(observedUrl, project.UrlPrefix))
            throw new InvalidOperationException("PROJECT_RESPONSE_PIN_OR_URL_INVALID");

        const int maxWaitMilliseconds = 60000;
        const int maxObservations = 240;
        const int pollMilliseconds = 250;
        var clock = Stopwatch.StartNew();
        var busyObserved = false;
        var idleReads = 0;
        var validReads = 0;
        var unavailableReads = 0;
        var hwnd = new IntPtr(pending.WindowHandle.Value);
        var binding = new ChatBinding(observedUrl, DateTimeOffset.UtcNow,
            "project-response-wait", pending.WindowHandle,
            null, pending.TabRuntimeId);

        for (var attempt = 0; attempt < maxObservations &&
            clock.ElapsedMilliseconds < maxWaitMilliseconds; attempt++)
        {
            Thread.Sleep(pollMilliseconds);
            try
            {
                // A URL deve continuar a mesma do primeiro /c/ remoto.
                // Não selecionar outra guia após um desvio inesperado.
                var tab = EdgeBrowser.SelectedTab(Automation, hwnd);
                if (!string.Equals(tab.RuntimeId, pending.TabRuntimeId,
                    StringComparison.Ordinal))
                    return new NewChatResult("PROJECT_RESPONSE_FOCUS_CHANGED",
                        pending.Name, pending.DisplayTitle, project.Id, null,
                        observedUrl, pending.WindowHandle, pending.TabRuntimeId,
                        "A guia selecionada mudou durante a geração. " +
                        "Reserva preservada; reconcileProjectChat não reenvia.");

                var currentUrl = EdgeBrowser.ReadActiveUrl(Automation, hwnd);
                if (!UrlIdentity.Exact(currentUrl, observedUrl))
                    return new NewChatResult("PROJECT_RESPONSE_URL_CHANGED",
                        pending.Name, pending.DisplayTitle, project.Id, null,
                        currentUrl, pending.WindowHandle, pending.TabRuntimeId,
                        "A URL da mesma guia mudou durante a observação. " +
                        "Reserva preservada; nenhuma mensagem reenviada.");

                var busy = ChatFeatures.IsGenerating(Automation, binding);
                // Confirmar novamente URL e guia APÓS a leitura de busy.
                var after = EdgeBrowser.TryActiveExact(Automation, hwnd,
                    observedUrl);
                if (after is null ||
                    !string.Equals(after.TabRuntimeId,
                        pending.TabRuntimeId, StringComparison.Ordinal))
                    return new NewChatResult("PROJECT_RESPONSE_TARGET_CHANGED",
                        pending.Name, pending.DisplayTitle, project.Id, null,
                        observedUrl, pending.WindowHandle, pending.TabRuntimeId,
                        "Não foi possível confirmar a mesma URL/guia depois " +
                        "da leitura de busy. Reserva preservada.");

                validReads++;
                if (busy)
                {
                    busyObserved = true;
                    idleReads = 0;
                }
                else idleReads++;

                if (ResponseIdleConfirmed(busyObserved, idleReads,
                    clock.ElapsedMilliseconds))
                {
                    // Confirma URL uma última vez sob o mesmo pin antes de
                    // inserir o chat e remover a reserva.
                    var finalTab = EdgeBrowser.ActivateTabAndReadCurrentUrl(
                        Automation, pending.WindowHandle.Value, pending.TabRuntimeId);
                    if (!UrlIdentity.Exact(finalTab.Url, observedUrl))
                        return new NewChatResult("PROJECT_RESPONSE_FINAL_URL_CHANGED",
                            pending.Name, pending.DisplayTitle, project.Id, null,
                            finalTab.Url, pending.WindowHandle, pending.TabRuntimeId,
                            "URL final divergente. Cadastro adiado; não reenviar.");

                    var completed = CompleteNewChatVerified(pending.Name);
                    _ = UseChat(completed.Name);
                    return completed with
                    {
                        Note = completed.Note + " Geração observada até idle; " +
                            "busyObserved=" + busyObserved +
                            "; idleReads=" + idleReads +
                            "; uiReads=" + validReads + "."
                    };
                }
            }
            catch (COMException ex) when (IsTransientUiaCom(ex))
            {
                unavailableReads++;
                idleReads = 0; // Falha UIA não equivale a idle.
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("BUSY_TAB_NOT_ACTIVE",
                    StringComparison.Ordinal) ||
                ex.Message.StartsWith("BUSY_TARGET_CHANGED",
                    StringComparison.Ordinal) ||
                ex.Message.StartsWith("BUSY_STOP_UNVERIFIED",
                    StringComparison.Ordinal) ||
                ex.Message.StartsWith("PROJECT_COMPOSER_NOT_UNIQUE",
                    StringComparison.Ordinal) ||
                ex.Message.StartsWith("NEW_CHAT_TAB_AMBIGUOUS",
                    StringComparison.Ordinal))
            {
                unavailableReads++;
                idleReads = 0;
            }
        }
        return new NewChatResult("PROJECT_RESPONSE_WAIT_PENDING",
            pending.Name, pending.DisplayTitle, project.Id, null,
            observedUrl, pending.WindowHandle, pending.TabRuntimeId,
            "Tempo limite de observação da geração atingido. " +
            "busyObserved=" + busyObserved + "; uiReads=" + validReads +
            "; unavailableReads=" + unavailableReads +
            ". Reserva preservada; execute web.reconcileProjectChat(\"" +
            pending.Name + "\") para continuar SEM reenviar.");
    }

    // Observação somente leitura: a URL /project vira /g/g-p-ID/c/ID na
    // MESMA guia. O envio não é refeito se o navegador demorar ou perder foco.
    private NewChatResult ObserveAndRegisterProjectChat(string name, Project project)
    {
        var pending = _projectRegistry.GetPending(name);
        if (pending.State != "FIRST_SEND_ATTEMPTED" ||
            pending.WindowHandle is null ||
            string.IsNullOrWhiteSpace(pending.TabRuntimeId))
            throw new InvalidOperationException("PROJECT_COMPOSE_FINALIZATION_NOT_READY");
        var composeUrl = ChatUrlScope.ProjectPage(project.UrlPrefix);
        string? lastProvisionalUrl = null;
        var transientUiaFailures = 0;
        var successfulUiaReads = 0;
        for (var i = 0; i < 60; i++)
        {
            SelectedTabState selected;
            string url;
            try
            {
                var hwnd = new IntPtr(pending.WindowHandle.Value);
                selected = EdgeBrowser.SelectedTab(Automation, hwnd);
                url = EdgeBrowser.ReadActiveUrl(Automation, hwnd);
            }
            catch (InvalidOperationException)
            {
                // UIA transitória: não selecionar guia alternativa nem enviar.
                Thread.Sleep(200);
                continue;
            }
            catch (COMException ex) when (IsTransientUiaCom(ex))
            {
                transientUiaFailures++;
                Thread.Sleep(200); // Apenas observação, nenhum clique.
                continue;
            }
            successfulUiaReads++;
            if (!string.Equals(selected.RuntimeId, pending.TabRuntimeId,
                StringComparison.Ordinal))
                return new NewChatResult("PAUSED_FOCUS_CHANGED",
                    name, pending.DisplayTitle, project.Id, null,
                    pending.LastObservedUrl, pending.WindowHandle,
                    pending.TabRuntimeId,
                    "Envio já tentado; selecione a guia fixada e execute " +
                    "createProjectChat novamente para finalizar, SEM reenviar.");
            if (ChatUrlScope.IsPersistentConversation(url, project.UrlPrefix))
            {
                // Não suprimir colisões nem falhas de persistência como se
                // fossem uma espera normal de URL: falhar com diagnóstico.
                return WaitForResponseAndRegisterProjectChat(
                    pending, project, url);
            }
            if (ChatUrlScope.IsProvisionalConversation(url, project.UrlPrefix))
            {
                // URL local-chatgpt%3A... identifica a criação local, não
                // uma URL remota durável. Aguardar sem reenvio ou registro.
                lastProvisionalUrl = url;
                Thread.Sleep(200);
                continue;
            }
            // URL vazia/temporária durante a navegação não é prova de
            // desvio para outra conversa; manter a observação limitada.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var observedUri) ||
                string.IsNullOrWhiteSpace(observedUri.Host))
            {
                Thread.Sleep(200);
                continue;
            }
            if (!UrlIdentity.Exact(url, composeUrl))
                return new NewChatResult("FIRST_SEND_URL_UNVERIFIED",
                    name, pending.DisplayTitle, project.Id, null,
                    url, pending.WindowHandle, pending.TabRuntimeId,
                    "URL fora do projeto após tentativa de envio; não repetir mensagem.");
            Thread.Sleep(200);
        }
        return new NewChatResult(
            successfulUiaReads == 0 && transientUiaFailures > 0
                ? "PROJECT_RECONCILE_UIA_UNAVAILABLE"
                : lastProvisionalUrl is null ? "FIRST_SEND_ATTEMPTED_URL_PENDING"
                    : "FIRST_SEND_PROVISIONAL_URL",
            name, pending.DisplayTitle, project.Id, null,
            lastProvisionalUrl ?? pending.LastObservedUrl,
            pending.WindowHandle, pending.TabRuntimeId,
            "Envio tentado uma única vez, sem confirmação de entrega. " +
            "Ainda sem URL remota definitiva. Execute " +
            "web.reconcileProjectChat(\"" + name +
            "\") para consultar novamente SEM reenviar.");
    }

    // Envio inicial REAL: cria a conversa remota somente por instrução
    // explícita com texto. Nunca depende do botão genérico "Novo chat".
    // O envio pode ser incerto após o clique; não há repetição automática.
    public object SendFirstNewChatMessage(string name, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("NEW_CHAT_MESSAGE_EMPTY: informe uma mensagem.");
        using var lease = _projectRegistry.AcquireChatOperation(name);
        Refresh();
        var pending = _projectRegistry.GetPending(name);
        if (pending.State != "OPENED_PENDING_URL" || pending.ProjectId is null ||
            pending.WindowHandle is null || string.IsNullOrWhiteSpace(pending.TabRuntimeId))
            throw new InvalidOperationException(
                "NEW_CHAT_FIRST_SEND_NOT_READY: exige página de projeto com " +
                "editor confirmado e reserva OPENED_PENDING_URL.");
        var project = GetProject(pending.ProjectId);
        var composeUrl = ChatUrlScope.ProjectPage(project.UrlPrefix);
        if (pending.LastObservedUrl is null ||
            !UrlIdentity.Exact(pending.LastObservedUrl, composeUrl))
            throw new InvalidOperationException(
                "NEW_CHAT_COMPOSER_SCOPE_UNVERIFIED: envio inicial permitido " +
                "somente na página /project do projeto selecionado.");

        // Uma aba arbitrária com a mesma URL não serve: exige HWND + pin.
        var target = EdgeBrowser.ActivateTabAndReadCurrentUrl(Automation,
            pending.WindowHandle.Value, pending.TabRuntimeId);
        if (!UrlIdentity.Exact(target.Url, composeUrl) ||
            !string.Equals(target.TabRuntimeId, pending.TabRuntimeId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "NEW_CHAT_TAB_CHANGED_BEFORE_FIRST_SEND: nenhuma mensagem enviada.");
        var binding = new ChatBinding(composeUrl, DateTimeOffset.UtcNow,
            "project-first-message", target.Handle, null, target.TabRuntimeId);
        var existing = ChatFeatures.GetText(Automation, binding);
        if (!string.IsNullOrWhiteSpace(existing))
            throw new InvalidOperationException(
                "NEW_CHAT_DRAFT_NOT_EMPTY: editor já possui texto, " +
                "não substituir automaticamente.");
        var stamped = MessageTimestamp.Apply(message);
        _ = ChatFeatures.SetText(Automation, binding, stamped);
        var readback = ChatFeatures.GetText(Automation, binding);
        if (!ChatFeatures.TextEquivalent(readback, stamped))
            throw new InvalidOperationException(
                "NEW_CHAT_DRAFT_UNVERIFIED: texto não confirmado; envio bloqueado.");

        // Grava a intenção antes do clique; se a página/guia mudar ou falhar,
        // o mesmo nome não aceita um segundo envio automático.
        _projectRegistry.MarkFirstSendAttempted(name, target.Handle,
            target.TabRuntimeId!, composeUrl);
        var dispatched = ChatFeatures.SendPrepared(Automation, binding, stamped);
        return new
        {
            status = "INPUT_DISPATCHED_UNCONFIRMED",
            name = pending.Name,
            projectId = pending.ProjectId,
            tabRuntimeId = pending.TabRuntimeId,
            dispatched,
            note = "Ação de envio tentada uma vez; recebimento remoto " +
                "ainda não comprovado. Não repetir. Após surgir /c/..., " +
                "web.completeNewChat(\"" + name + "\") executa a mesma " +
                "espera por isBusy=false e a reconciliação, sem reenviar."
        };
    }
    public object sendFirstNewChatMessage(string name, string message) =>
        SendFirstNewChatMessage(name, message);

    // Finalização explícita após o primeiro envio humano. Exige mesmíssima
    // guia pela identidade UIA, URL real persistente e escopo de projeto.
    public NewChatResult CompleteNewChat(string name)
    {
        var pending = _projectRegistry.GetPending(name);
        // Os projetos não têm mais um caminho alternativo que grava
        // REGISTERED antes de observar isBusy=false. Mesmo o comando
        // explícito do console deve reconciliar e esperar sem reenviar.
        if (pending.ProjectId is not null)
            return ReconcileProjectChat(name);
        return CompleteNewChatVerified(name);
    }

    // Único ponto de persistência final. Para chats de projeto, somente
    // WaitForResponseAndRegisterProjectChat chama este núcleo, APÓS idle.
    // Standalone legado mantém o cadastro manual explícito.
    private NewChatResult CompleteNewChatVerified(string name)
    {
        Refresh();
        var pending = _projectRegistry.GetPending(name);
        if (pending.State != "OPENED_PENDING_URL" &&
            pending.State != "FIRST_SEND_ATTEMPTED")
            throw new InvalidOperationException(
                "NEW_CHAT_NOT_OPENED: exige compositor confirmado ou " +
                "primeiro envio explícito tentado; confira pendingNewChats().");
        if (pending.WindowHandle is null || string.IsNullOrWhiteSpace(pending.TabRuntimeId))
            throw new InvalidOperationException(
                "NEW_CHAT_NOT_OPENED: falta prova de guia. Confira pendingNewChats().");
        var project = pending.ProjectId is null ? null : GetProject(pending.ProjectId);
        var tab = EdgeBrowser.ActivateTabAndReadCurrentUrl(
            Automation, pending.WindowHandle.Value, pending.TabRuntimeId);
        if (!ChatUrlScope.IsPersistentConversation(tab.Url, project?.UrlPrefix))
            throw new InvalidOperationException(
                "NEW_CHAT_URL_PENDING: o ChatGPT ainda não atribuiu uma URL definitiva " +
                "para a conversa. Não enviaremos mensagem automaticamente.");
        var saved = _registry.InsertNewChat(name,
            new ChatBinding(tab.Url, DateTimeOffset.UtcNow,
                "new-chat-confirmed", tab.Handle, null, tab.TabRuntimeId),
            pending.ProjectId, pending.DisplayTitle);
        if (!_instances.TryGetValue(saved.Name, out var chat))
        {
            chat = new Chat(this, saved);
            _instances.Add(saved.Name, chat);
        }
        else chat.UpdateRecord(saved);
        _projectRegistry.MarkCompleted(name);
        var titleCapture = TryCaptureRegisteredChatFullName(chat);
        return new NewChatResult("REGISTERED", saved.Name, saved.DisplayTitle,
            saved.ProjectId, saved.Url, tab.Url, tab.Handle, tab.TabRuntimeId,
            "Chat registrado. Use \"" + saved.Name + "\" para selecioná-lo. " +
            (titleCapture.FullName is null
                ? "fullName pendente; diagnóstico: " +
                  titleCapture.Diagnostic +
                  "; use inspectFullName() ou setFullName(\"...\")."
                : "fullNameSource=" + chat.FullNameSource + "."),
            titleCapture.FullName);
    }
    public NewChatResult completeNewChat(string name) => CompleteNewChat(name);

    private string? ResolveProjectForUrl(string url) => _projectRegistry.ListProjects()
        .FirstOrDefault(p => ChatUrlScope.IsPersistentConversation(url, p.UrlPrefix))?.Id;

    // Registra uma URL sem abrir janela, sem UIA e sem alterar o chat ativo.
    // Nome já existente com outra URL não é sobrescrito silenciosamente.
    public Chat AddChat(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Refresh(); // Recarrega alterações feitas por outros processos antes da gravação.
        if (_instances.TryGetValue(name, out var existing))
        {
            if (UrlIdentity.Exact(existing.Url, url)) return existing;
            throw new InvalidOperationException(
                $"Nome '{name}' já registrado com outra URL. Remova-o explicitamente antes de reutilizá-lo.");
        }
        var saved = _registry.Upsert(name,
            new ChatBinding(url, DateTimeOffset.UtcNow, "manual-catalog"),
            projectId: ResolveProjectForUrl(url));
        var chat = new Chat(this, saved);
        _instances.Add(saved.Name, chat);
        return chat;
    }
    public Chat addChat(string name, string url) => AddChat(name, url);

    // Atualiza o catálogo em runtime sem perder referências a chats existentes.
    // Não modifica nenhuma janela nem abre o Edge.
    public IReadOnlyList<ChatRecord> Refresh()
    {
        // Evita redirecionar um comando para uma URL modificada externamente.
        var selectedUrl = CurrentChat?.Url;
        var loaded = new ChatRegistry(_catalogPath, migrateLegacy: false);
        _settings.Reload(); // Outro processo pode ter alterado settings.json.
        var records = loaded.ListChats();
        var names = new HashSet<string>(records.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _instances.Keys.Where(n => !names.Contains(n)).ToArray())
            _instances.Remove(stale);
        foreach (var record in records)
        {
            if (_instances.TryGetValue(record.Name, out var existing))
            {
                existing.UpdateRecord(record);
                existing.InvalidatePreparedText(); // Nunca confiar em rascunho após refresh.
            }
            else _instances.Add(record.Name, new Chat(this, record));
        }
        _registry = loaded;
        ReloadProjects();
        if (_selectedChatName is { } selectedName)
        {
            if (!_instances.TryGetValue(selectedName, out var selected) ||
                selectedUrl is null || !UrlIdentity.Exact(selectedUrl, selected.Url))
                _selectedChatName = null;
            else
            {
                ValidateDeclaredProjectScope(selected.Record);
                var actualProject = ChatProjectId(selected);
                if (actualProject is not null && !_projects.ContainsKey(actualProject))
                    throw new InvalidOperationException(
                        "CHAT_PROJECT_NOT_REGISTERED: refresh encontrou projeto ausente.");
                _selectedProjectId = actualProject;
            }
        }
        return ListChats();
    }
    public IReadOnlyList<ChatRecord> refresh() => Refresh();

    // Cadastro a partir da guia ativa no Edge, sem selecionar contexto de
    // execução nem alterar o arquivo legado current-binded-chat.json.
    // Com URL explícita, não interage com o navegador (mesma semântica de addChat).
    public Chat CaptureChat(string name, string? url = null,
        string? profileDirectory = null, long? windowHandle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // URL fornecida equivale ao cadastro normal: não descarta o pin existente.
        if (!string.IsNullOrWhiteSpace(url))
        {
            Refresh();
            if (_instances.TryGetValue(name, out var previous) &&
                !UrlIdentity.Exact(previous.Url, url))
                throw new InvalidOperationException(
                    $"CAPTURE_URL_CONFLICT: '{name}' já representa outra conversa. " +
                    "Use outro nome; o destino não será alterado.");
            return AddChat(name, url);
        }
        Refresh();
        var binding = ChatAutomation.Commands.BindToChat.Capture(Automation, null,
            profileDirectory, windowHandle);
        if (_instances.TryGetValue(name, out var existing) &&
            !UrlIdentity.Exact(existing.Url, binding.Url))
            throw new InvalidOperationException(
                $"CAPTURE_URL_CONFLICT: '{name}' já representa outra conversa. " +
                "Use outro nome; um registro não é redirecionado silenciosamente.");
        var saved = _registry.Upsert(name, binding,
            projectId: ResolveProjectForUrl(binding.Url));
        if (!_instances.TryGetValue(saved.Name, out var chat))
        {
            chat = new Chat(this, saved);
            _instances[saved.Name] = chat;
        }
        else chat.UpdateRecord(saved);
        // captureChat sem URL já inspecionou a guia ativa; tentar também
        // seu título externo, sem falhar nem criar conversa se não houver
        // evidência UIA inequívoca. captureChat com URL explícita segue
        // sendo puramente cadastral (sem tocar o Edge).
        _ = TryCaptureRegisteredChatFullName(chat);
        return chat;
    }
    public Chat captureChat(string name) => CaptureChat(name);

    // API legada usada pelo CLI/PowerShell: preservada para compatibilidade.
    // Novos consumidores devem cadastrar com captureChat/addChat e selecionar
    // explicitamente com use. A execução moderna NÃO lê CurrentBinding.
    [Obsolete("Use CaptureChat(name) para cadastro e UseChat(name) para seleção.")]
    public Chat BindToChat(string? name = null, string? url = null,
        string? profileDirectory = null, long? windowHandle = null)
    {
        // Mantém a semântica legada inclusive para substituição explícita
        // do registro nomeado. Não é acessível pelo console moderno.
        Refresh();
        var binding = ChatAutomation.Commands.BindToChat.Capture(
            Automation, url, profileDirectory, windowHandle);
        var saved = _registry.Upsert(name, binding);
        CurrentBinding.Set(binding);
        if (!_instances.TryGetValue(saved.Name, out var chat))
        {
            chat = new Chat(this, saved);
            _instances[saved.Name] = chat;
        }
        else
        {
            if (string.Equals(_selectedChatName, saved.Name, StringComparison.OrdinalIgnoreCase) &&
                !UrlIdentity.Exact(chat.Url, saved.Url))
                _selectedChatName = null;
            chat.UpdateRecord(saved);
        }
        return chat;
    }

    public void RemoveChat(string name)
    {
        Refresh();
        var old = _registry.Get(name);
        _registry.Remove(name);
        _instances.Remove(name);
        if (string.Equals(_selectedChatName, name, StringComparison.OrdinalIgnoreCase))
            _selectedChatName = null;
        if (CurrentBinding.currentBindedChat is { } current &&
            UrlIdentity.Exact(current.Url, old.Url))
            CurrentBinding.Clear();
    }

    internal void EnsureRegistered(Chat chat)
    {
        if (!_instances.TryGetValue(chat.Name, out var current) || !ReferenceEquals(current, chat))
            throw new InvalidOperationException($"Chat '{chat.Name}' removido ou substituído. Execute web.refresh().");
    }

    internal void RememberPreferredWindow(Chat chat, TabMatch tab)
    {
        EnsureRegistered(chat);
        var rec = chat.Record;
        var updated = _registry.Upsert(rec.Name, new ChatBinding(rec.Url, DateTimeOffset.UtcNow,
            "named-chat-ui", tab.Handle, rec.ProfileDirectory, tab.TabRuntimeId));
        chat.UpdateRecord(updated);
    }

    internal ChatRecord PersistChatFullName(Chat chat, string fullName, string source)
    {
        EnsureRegistered(chat);
        var saved = _registry.SetFullName(chat.Name, chat.Url, fullName, source);
        chat.UpdateRecord(saved);
        return saved;
    }

    // Diferenciar título com confirmação na página daquele comprovado
    // apenas pelo TabItem e identidade exata da guia.
    internal static string? FullNameSourceFromPreview(string? status) =>
        status switch
        {
            "AUTO_DETECTED" => "UIA_TAB_AND_PAGE",
            "AUTO_DETECTED_TAB_ONLY" => "UIA_TAB_VERIFIED",
            _ => null
        };

    // Captura oportunista APÓS registrar a URL remota do novo chat.
    // A falha do título não pode retroceder o registro ou provocar
    // um segundo envio. A UIA é apenas leitura; não troca guia.
    private sealed record ChatFullNameCaptureOutcome(
        string? FullName, string? Diagnostic);

    private ChatFullNameCaptureOutcome TryCaptureRegisteredChatFullName(Chat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.FullName))
            return new(chat.FullName, null);

        string? diagnostic = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var preview = chat.InspectFullName();
                if (preview.FullName is { } title &&
                    FullNameSourceFromPreview(preview.Status) is { } source)
                    return new(PersistChatFullName(chat, title, source).FullName,
                        null);
                diagnostic = preview.Status;
            }
            catch (Exception ex)
            {
                // Depois de REGISTERED, uma falha opcional de UIA ou de
                // persistência do título não pode reverter o chat, porém
                // precisa ser EXPOSTA no diagnóstico de criação.
                diagnostic = ex.GetType().Name + ": " + ex.Message;
            }
            if (attempt < 2) Thread.Sleep(180);
        }
        return new(null, diagnostic ?? "UIA_TITLE_UNAVAILABLE");
    }

    internal void MarkMaxLimit(Chat chat)
    {
        EnsureRegistered(chat);
        var updated = _registry.MarkMaxLimit(chat.Name, chat.Url);
        chat.UpdateRecord(updated);
    }

    public void Dispose()
    {
        if (_uia.IsValueCreated) _uia.Value.Dispose();
    }
}

public sealed class Chat
{
    public const string DefaultIdPrefix = "GPTBOT_";
    // A próxima sequência é persistida em projects.json; 9 reserva 0000–0008.
    public const int InitialStandaloneSequence = 9;
    private readonly WebInterface _owner;
    private string? _preparedText;
    internal ChatRecord Record { get; private set; }

    internal Chat(WebInterface owner, ChatRecord record)
    {
        _owner = owner;
        Record = record;
    }

    public string Name => Record.Name;
    public string Url => Record.Url;
    public string name => Name;
    public string url => Url;
    public string? FullName => Record.FullName;
    public string? fullName => FullName;
    public string? FullNameSource => Record.FullNameSource;
    public string? fullNameSource => FullNameSource;
    // false até uma confirmação positiva; estado terminal persistido por URL.
    public bool IsAtMaxLimit => Record.IsAtMaxLimit;
    public bool isAtMaxLimit => IsAtMaxLimit;

    internal void UpdateRecord(ChatRecord record)
    {
        if (!UrlIdentity.Exact(Record.Url, record.Url)) _preparedText = null;
        Record = record;
    }
    internal void InvalidatePreparedText() => _preparedText = null;

    private ChatBinding Binding => new(Record.Url, Record.UpdatedAtUtc,
        "named-chat", Record.PreferredWindowHandle, Record.ProfileDirectory, Record.PreferredTabRuntimeId);

    private void RememberForegroundTab()
    {
        var hwnd = ChatAutomation.Infrastructure.Win32.GetForegroundWindow();
        if (!ChatAutomation.Infrastructure.Win32.IsEdgeWindow(hwnd)) return;
        var match = EdgeBrowser.TryActiveExact(_owner.Automation, hwnd, Record.Url);
        if (match is not null) _owner.RememberPreferredWindow(this, match);
    }

    public TabMatch SetFocus()
    {
        _owner.EnsureRegistered(this);
        var match = ChatFeatures.FocusChat(_owner.Automation, Binding);
        _owner.RememberPreferredWindow(this, match);
        return match;
    }

    // Diagnóstico somente leitura; nunca usa o primeiro item em Recentes
    // como identidade. Exige URL e RuntimeId do chat selecionado.
    public ChatFullNamePreview InspectFullName()
    {
        _owner.EnsureRegistered(this);
        return ChatFullNameInspector.Inspect(_owner.Automation, Binding);
    }
    public ChatFullNamePreview inspectFullName() => InspectFullName();

    // Persistir somente quando TabItem e elemento UIA da página concordam.
    public object CaptureFullName()
    {
        var preview = InspectFullName();
        var source = WebInterface.FullNameSourceFromPreview(preview.Status);
        if (source is null || preview.FullName is null)
            return new { status = "FULL_NAME_NOT_CONFIRMED",
                chat = Name, preview, fullName = FullName };
        var saved = _owner.PersistChatFullName(this, preview.FullName, source);
        return new { status = "FULL_NAME_SAVED", chat = Name,
            fullName = saved.FullName, source = saved.FullNameSource, preview };
    }
    public object captureFullName() => CaptureFullName();

    // Metadado local, sem renomear a conversa na interface do ChatGPT.
    public object SetFullName(string fullName)
    {
        _owner.EnsureRegistered(this);
        var saved = _owner.PersistChatFullName(this, fullName, "MANUAL");
        return new { status = "FULL_NAME_SAVED", chat = Name,
            fullName = saved.FullName, source = saved.FullNameSource,
            url = saved.Url };
    }
    public object setFullName(string fullName) => SetFullName(fullName);

    public ComposerInfo DetectTextArea()
    {
        _owner.EnsureRegistered(this);
        return ChatFeatures.DetectTextArea(_owner.Automation, Binding);
    }

    // Conteúdo atual do campo de escrita. Não retorna o histórico da conversa.
    public string GetText()
    {
        _owner.EnsureRegistered(this);
        var value = ChatFeatures.GetText(_owner.Automation, Binding);
        RememberForegroundTab();
        return value;
    }

    // Limpa apenas o campo de escrita; não apaga mensagens do histórico.
    public object Clear()
    {
        _owner.EnsureRegistered(this);
        _preparedText = null;
        var result = ChatFeatures.SetText(_owner.Automation, Binding, string.Empty);
        RememberForegroundTab();
        return result;
    }

    public object SetText(string text)
    {
        _owner.EnsureRegistered(this);
        _preparedText = null;
        var stamped = MessageTimestamp.Apply(text);
        var result = ChatFeatures.SetText(_owner.Automation, Binding, stamped);
        _preparedText = stamped; // Mesmo conteúdo do readback, sem novo relógio no sendMessage().
        RememberForegroundTab();
        return result;
    }

    // UIA somente leitura. Usa a guia ativa vinculada; não clica nem muda o foco.
    // Repetir a leitura não reverte um limite já comprovado nesta URL.
    public MaxLimitEvidence InspectMaxLimit()
    {
        _owner.EnsureRegistered(this);
        var evidence = ChatLimitDetector.Probe(_owner.Automation, Binding);
        if (evidence.Detected && !IsAtMaxLimit)
        {
            _preparedText = null;
            _owner.MarkMaxLimit(this);
        }
        return evidence;
    }

    public bool CheckMaxLimit()
    {
        _ = InspectMaxLimit();
        return IsAtMaxLimit;
    }

    // Consulta passiva do limite só é válida quando a guia vinculada está
    // selecionada. O envio é uma ação ativa: pode recuperar o foco da MESMA
    // guia antes dessa leitura, sem depender do foco deixado pelo terminal.
    // Retentativas restringem-se ao preflight, ANTES de preencher/clicar.
    internal static bool IsPassiveLimitFocusError(string? reason) =>
        reason is not null && reason.StartsWith(
            "BUSY_TAB_NOT_ACTIVE", StringComparison.Ordinal);

    private void RequireCanSend()
    {
        if (IsAtMaxLimit)
            throw new InvalidOperationException(
                "CHAT_AT_MAX_LIMIT: conversa chegou à duração máxima; envio bloqueado.");

        // Nunca enfocar uma guia por título ou por índice. SetFocus usa
        // URL + pin HWND/RuntimeId e falha em caso de identidade ambígua.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = SetFocus();
            try
            {
                if (CheckMaxLimit())
                    throw new InvalidOperationException(
                        "CHAT_AT_MAX_LIMIT: conversa chegou à duração máxima; envio bloqueado. " +
                        "Abra e vincule explicitamente uma NOVA conversa para continuar.");
                return;
            }
            catch (InvalidOperationException ex) when (
                IsPassiveLimitFocusError(ex.Message) && attempt < 2)
            {
                // O terminal pode retomar o foco entre SetFocus e Probe.
                // Repetir somente a inspeção pré-envio, nunca a digitação
                // nem o clique. Na terceira falha, bloquear de fato.
                Thread.Sleep(130);
            }
        }
    }

    // Com a opção desligada, somente o texto preparado nesta sessão é enviado.
    // Com a opção ligada, lê o rascunho REAL do editor (inclusive edição humana)
    // e verifica novamente antes de clicar. Nunca reutiliza um valor em cache.
    public object SendMessage()
    {
        _owner.EnsureRegistered(this);
        RequireCanSend();
        if (_owner.EnableSendHumanText)
        {
            _preparedText = null;
            var result = ChatFeatures.SendCurrentEditor(_owner.Automation, Binding);
            RememberForegroundTab();
            return result;
        }
        if (_preparedText is null)
            throw new InvalidOperationException(
                "Sem texto preparado nesta sessão. Execute chat.setText(...) ou chat.sendMessage(\"texto\"). " +
                "Para autorizar rascunhos digitados manualmente, use enableSendHumanText = true.");
        var prepared = _preparedText;
        _preparedText = null; // Falhas não permitem nova tentativa cega.
        var sent = ChatFeatures.SendPrepared(_owner.Automation, Binding, prepared);
        RememberForegroundTab();
        return sent;
    }

    public object SendMessage(string text)
    {
        _owner.EnsureRegistered(this);
        RequireCanSend(); // Não preencher o editor de uma conversa bloqueada.
        SetText(text);
        return SendMessage(); // Revalidar antes do clique; sem envio se mudou.
    }

    // Consultas de geração não selecionam nem mudam guias; exige que a guia
    // vinculada esteja ativa na sua janela do Edge.
    public bool IsGenerating()
    {
        _owner.EnsureRegistered(this);
        return ChatFeatures.IsGenerating(_owner.Automation, Binding);
    }
    public bool IsBusy() => IsGenerating();

    // Abre um CMD separado, dedicado à consulta deste chat a cada 8 segundos.
    // O loop roda em outro processo, com seu próprio UIA3Automation.
    public object BusyWindow()
    {
        _owner.EnsureRegistered(this);
        var dllPath = typeof(Chat).Assembly.Location;
        if (dllPath.Contains('"') || dllPath.Contains('%') || dllPath.Contains('!'))
            throw new InvalidOperationException("Caminho do executável contém caracteres não seguros para CMD.");
        var info = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/k dotnet \"{dllPath}\" busy-monitor --chat {Name}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Não foi possível criar janela CMD para o monitor.");
        return new { status = "BUSY_WINDOW_STARTED", chat = Name, url = Url, processId = process.Id,
            intervalSeconds = 8 };
    }

    public object Open(Mode mode, long? windowHandle = null, string? profileDirectory = null)
    {
        _owner.EnsureRegistered(this);
        _preparedText = null;
        var result = ChatFeatures.OpenChat(_owner.Automation, Binding,
            mode == Mode.NewWindow ? ChatOpenMode.NewWindow : ChatOpenMode.NewTab,
            windowHandle, profileDirectory);
        // HWND antigo é efêmero. Atualiza apenas após verificar a URL na nova guia/janela.
        _owner.RememberPreferredWindow(this, EdgeBrowser.TryActiveExact(_owner.Automation,
            new IntPtr(result.WindowHandle), Record.Url)
            ?? throw new InvalidOperationException("Guia aberta não confirmou identidade após a navegação."));
        return result;
    }

    public bool checkMaxLimit() => CheckMaxLimit();
    public MaxLimitEvidence inspectMaxLimit() => InspectMaxLimit();
    public bool isGenerating() => IsGenerating();
    public bool isBusy() => IsBusy();
    public object busyWindow() => BusyWindow();
    public string getText() => GetText();
    public object clear() => Clear();
    public object setText(string text) => SetText(text);
    public object sendMessage() => SendMessage();
    public object sendMessage(string text) => SendMessage(text);
    public TabMatch setFocus() => SetFocus();
    public object open(Mode mode) => Open(mode);
}
