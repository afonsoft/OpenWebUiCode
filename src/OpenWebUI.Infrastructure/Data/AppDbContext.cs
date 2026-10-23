using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Data;

/// <summary>Contexto EF Core do backend .NET (SQLite por padrão).</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Usuários cadastrados.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Chaves de API geradas por usuários.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>Chats persistidos.</summary>
    public DbSet<Chat> Chats => Set<Chat>();

    /// <summary>Mensagens de chats.</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <summary>Runs de chat desacopladas (SPEC-20261007-chat-detached-runs).</summary>
    public DbSet<ChatRun> ChatRuns => Set<ChatRun>();

    /// <summary>Inbox durável de steer/queue das runs (SPEC-20261010-steer-queue).</summary>
    public DbSet<ChatRunSteer> ChatRunSteers => Set<ChatRunSteer>();

    /// <summary>Eventos duráveis append-only por run (SPEC-20261016-run-recovery).</summary>
    public DbSet<ChatRunEvent> ChatRunEvents => Set<ChatRunEvent>();

    /// <summary>Jobs de background spawnados pelo chat (SPEC-20261007-chat-agent-tools).</summary>
    public DbSet<ChatJob> ChatJobs => Set<ChatJob>();

    /// <summary>Subscriptions Web Push de navegadores (SPEC-20261007-chat-notifications).</summary>
    public DbSet<ChatPushSubscription> ChatPushSubscriptions => Set<ChatPushSubscription>();

    /// <summary>Pastas de organização de chats.</summary>
    public DbSet<Folder> Folders => Set<Folder>();

    /// <summary>Prompts personalizados do workspace.</summary>
    public DbSet<Prompt> Prompts => Set<Prompt>();

    /// <summary>Arquivos enviados pelos usuários.</summary>
    public DbSet<FileEntry> Files => Set<FileEntry>();

    /// <summary>Modelos personalizados do workspace.</summary>
    public DbSet<ModelEntry> ModelEntries => Set<ModelEntry>();

    /// <summary>Batalhas de arena com voto pendente ou registrado.</summary>
    public DbSet<ArenaBattle> ArenaBattles => Set<ArenaBattle>();

    /// <summary>Memórias persistentes dos usuários.</summary>
    public DbSet<MemoryEntry> Memories => Set<MemoryEntry>();

    /// <summary>Notas dos usuários.</summary>
    public DbSet<Note> Notes => Set<Note>();

    /// <summary>Avaliações de mensagens.</summary>
    public DbSet<Feedback> Feedbacks => Set<Feedback>();

    /// <summary>Grupos de usuários (RBAC).</summary>
    public DbSet<Group> Groups => Set<Group>();

    /// <summary>Vínculos usuário↔grupo.</summary>
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    /// <summary>Contas OAuth/OIDC vinculadas a usuários.</summary>
    public DbSet<OAuthAccount> OAuthAccounts => Set<OAuthAccount>();

    /// <summary>Coleções de knowledge (RAG).</summary>
    public DbSet<KnowledgeCollection> KnowledgeCollections => Set<KnowledgeCollection>();

    /// <summary>Arquivos vinculados a coleções.</summary>
    public DbSet<KnowledgeFile> KnowledgeFiles => Set<KnowledgeFile>();

    /// <summary>Chunks vetoriais para retrieval.</summary>
    public DbSet<EmbeddingChunk> EmbeddingChunks => Set<EmbeddingChunk>();
    /// <summary>Tools externas (function calling via HTTP).</summary>
    public DbSet<Tool> Tools => Set<Tool>();

    /// <summary>Canais de conversa em grupo.</summary>
    public DbSet<Channel> Channels => Set<Channel>();

    /// <summary>Membros de canais.</summary>
    public DbSet<ChannelMember> ChannelMembers => Set<ChannelMember>();

    /// <summary>Mensagens de canais.</summary>
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();

    /// <summary>Reações a mensagens de canais.</summary>
    public DbSet<ChannelMessageReaction> ChannelMessageReactions => Set<ChannelMessageReaction>();

    /// <summary>Automações de prompts agendados.</summary>
    public DbSet<Automation> Automations => Set<Automation>();

    /// <summary>Execuções registradas das automações.</summary>
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();

    /// <summary>Webhooks de automação (disparam runs no chat vinculado).</summary>
    public DbSet<AutomationHook> AutomationHooks => Set<AutomationHook>();

    /// <summary>Entregas/auditoria de webhooks (SPEC-20261021-harness-seams RF-D2).</summary>
    public DbSet<AutomationHookDelivery> AutomationHookDeliveries => Set<AutomationHookDelivery>();

    /// <summary>Tarefas compartilhadas de agent teams (SPEC-20261021-harness-seams RF-E2).</summary>
    public DbSet<TeamTask> TeamTasks => Set<TeamTask>();

    /// <summary>Inbox persistido de atenção do usuário (SPEC-20261022-ade-surfaces RF-C1).</summary>
    public DbSet<UserAttention> UserAttentions => Set<UserAttention>();

    /// <summary>Combos de modelos com failover (SPEC-20261016-model-combos).</summary>
    public DbSet<ModelCombo> ModelCombos => Set<ModelCombo>();

    /// <summary>Itens (modelos ordenados) dos combos.</summary>
    public DbSet<ModelComboItem> ModelComboItems => Set<ModelComboItem>();

    /// <summary>Personas de agente acionáveis por canal (SPEC-20261016-agent-personas-channels).</summary>
    public DbSet<AgentPersona> AgentPersonas => Set<AgentPersona>();

    /// <summary>Sessões lógicas (persona × chatKey → chat).</summary>
    public DbSet<AgentPersonaSession> AgentPersonaSessions => Set<AgentPersonaSession>();

    /// <summary>Turnos auditáveis das personas (cap 200/persona).</summary>
    public DbSet<AgentTurn> AgentTurns => Set<AgentTurn>();

    /// <summary>Configurações chave-valor.</summary>
    public DbSet<ConfigEntry> ConfigEntries => Set<ConfigEntry>();

    /// <summary>Banners administrativos.</summary>
    public DbSet<Banner> Banners => Set<Banner>();

    /// <summary>Calendários.</summary>
    public DbSet<Calendar> Calendars => Set<Calendar>();

    /// <summary>Eventos de calendário.</summary>
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    /// <summary>Webhooks de notificação (por usuário ou globais).</summary>
    public DbSet<NotificationWebhook> NotificationWebhooks => Set<NotificationWebhook>();

    /// <summary>Feed in-app de notificações por usuário (D3).</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Skills do workspace.</summary>
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();

    /// <summary>Functions do ecossistema de plugins.</summary>
    public DbSet<Function> Functions => Set<Function>();

    /// <summary>Servidores de pipelines externos.</summary>
    public DbSet<PipelineServer> PipelineServers => Set<PipelineServer>();

    /// <summary>Servidores MCP registrados pelo admin.</summary>
    public DbSet<McpServer> McpServers => Set<McpServer>();

    /// <summary>Statements de policy administrativa (hard-deny).</summary>
    public DbSet<AdminPolicyStatement> AdminPolicyStatements => Set<AdminPolicyStatement>();

    /// <summary>Auditoria de bloqueios por policy.</summary>
    public DbSet<PolicyDenial> PolicyDenials => Set<PolicyDenial>();

    /// <summary>Snapshots versionados do contexto de trabalho por chat.</summary>
    public DbSet<ContextSnapshot> ContextSnapshots => Set<ContextSnapshot>();

    /// <summary>Ledger de uso por run (SPEC-20261023-run-cost-budgets RF-001).</summary>
    public DbSet<RunUsageRecord> RunUsageRecords => Set<RunUsageRecord>();

    /// <summary>Políticas de budget por sujeito (SPEC-20261023-run-cost-budgets RF-002).</summary>
    public DbSet<BudgetPolicy> BudgetPolicies => Set<BudgetPolicy>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasMany(u => u.Chats)
                .WithOne(c => c.User!)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.HasIndex(k => k.KeyHash).IsUnique();
            entity.HasIndex(k => k.UserId);
        });

        modelBuilder.Entity<Chat>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.UpdatedAt });
            entity.HasIndex(c => c.ShareId).IsUnique();
            entity.HasIndex(c => new { c.UserId, c.FolderId });
            entity.HasIndex(c => c.ParentChatId);
            // Roster de agent teams (SPEC-20261021-harness-seams RF-E1).
            entity.HasIndex(c => c.TeamId);
            entity.HasMany(c => c.Messages)
                .WithOne(m => m.Chat!)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            // Ids de mensagem são únicos dentro do chat (o original usa UUIDs locais).
            entity.HasKey(m => new { m.ChatId, m.Id });
            entity.HasIndex(m => new { m.ChatId, m.Position });
        });

        modelBuilder.Entity<ChatRun>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.ChatId, r.Status });
            entity.HasIndex(r => new { r.UserId, r.CreatedAt });
            entity.HasIndex(r => r.ParentRunId);
            entity.HasOne(r => r.Chat)
                .WithMany()
                .HasForeignKey(r => r.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentMemory>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.UserId, m.Scope });
            entity.HasIndex(m => new { m.UserId, m.RepoSlug });
        });

        modelBuilder.Entity<ChatRunEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RunId, e.Seq });
        });

        modelBuilder.Entity<ChatRunSteer>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.RunId, s.Status });
            entity.HasIndex(s => s.ChatId);
        });

        modelBuilder.Entity<ChatJob>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.HasIndex(j => new { j.ChatId, j.Status });
            entity.HasIndex(j => new { j.UserId, j.StartedAt });
            entity.HasOne<Chat>()
                .WithMany()
                .HasForeignKey(j => j.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatPushSubscription>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.Endpoint).IsUnique();
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(n => n.Id);
            // Lista por usuário em ordem cronológica inversa (feed paginado).
            entity.HasIndex(n => new { n.UserId, n.CreatedAt });
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.UserId);
        });

        modelBuilder.Entity<Prompt>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.UserId, p.Command }).IsUnique();
        });

        modelBuilder.Entity<FileEntry>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.UserId);
        });

        modelBuilder.Entity<ModelEntry>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<MemoryEntry>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => n.UserId);
        });

        modelBuilder.Entity<Feedback>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.UserId, f.MessageId }).IsUnique();
        });

        modelBuilder.Entity<Automation>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.UserId, a.UpdatedAt });
            entity.HasIndex(a => new { a.Enabled, a.NextRunAt });
            entity.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(a => a.Runs)
                .WithOne(r => r.Automation!)
                .HasForeignKey(r => r.AutomationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationRun>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.AutomationId, r.StartedAt });
        });

        modelBuilder.Entity<AutomationHook>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => h.UserId);
            entity.HasOne(h => h.User)
                .WithMany()
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(h => h.Chat)
                .WithMany()
                .HasForeignKey(h => h.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationHookDelivery>(entity =>
        {
            entity.HasKey(d => d.Id);
            // Auditoria por hook + dedup por chave do remetente.
            entity.HasIndex(d => new { d.HookId, d.ReceivedAt });
            entity.HasIndex(d => new { d.HookId, d.DeliveryKey });
        });

        modelBuilder.Entity<TeamTask>(entity =>
        {
            entity.HasKey(t => t.Id);
            // Task list por team e pendentes por status.
            entity.HasIndex(t => new { t.TeamId, t.Status });
            entity.HasIndex(t => t.AssigneeChatId);
        });

        modelBuilder.Entity<UserAttention>(entity =>
        {
            entity.HasKey(a => a.Id);
            // Upsert por origem: um item por (usuário, chave).
            entity.HasIndex(a => new { a.UserId, a.Key }).IsUnique();
            entity.HasIndex(a => a.ChatId);
        });

        modelBuilder.Entity<ModelCombo>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.Name }).IsUnique();
            entity.HasMany(c => c.Items)
                .WithOne()
                .HasForeignKey(i => i.ComboId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelComboItem>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.HasIndex(i => new { i.ComboId, i.Position });
        });

        modelBuilder.Entity<AgentPersona>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
        });

        modelBuilder.Entity<AgentPersonaSession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.PersonaId, s.ChatKey }).IsUnique();
        });

        modelBuilder.Entity<AgentTurn>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.PersonaId, t.Timestamp });
            entity.HasIndex(t => t.RunId);
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.HasIndex(g => g.Name).IsUnique();
            entity.HasMany(g => g.Members)
                .WithOne(m => m.Group!)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.HasKey(m => new { m.GroupId, m.UserId });
            entity.HasIndex(m => m.UserId);
            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OAuthAccount>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.Provider, a.ProviderAccountId }).IsUnique();
            entity.HasIndex(a => a.UserId);
        });

        modelBuilder.Entity<KnowledgeCollection>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.HasIndex(k => new { k.UserId, k.Name }).IsUnique();
            entity.HasOne(k => k.User)
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(k => k.Files)
                .WithOne(f => f.Collection!)
                .HasForeignKey(f => f.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeFile>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.CollectionId, f.FileId }).IsUnique();
        });

        modelBuilder.Entity<EmbeddingChunk>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.UserId, c.FileId });
            // RemoveFileChunksAsync filtra só por FileId — o composto
            // (UserId, FileId) não cobre busca pela coluna à direita.
            entity.HasIndex(c => c.FileId);
        });
        modelBuilder.Entity<Tool>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.UserId);
            entity.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasMany(c => c.Members)
                .WithOne(m => m.Channel!)
                .HasForeignKey(m => m.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(c => c.Messages)
                .WithOne(m => m.Channel!)
                .HasForeignKey(m => m.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelMember>(entity =>
        {
            entity.HasKey(m => new { m.ChannelId, m.UserId });
            entity.HasIndex(m => m.UserId);
            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.ChannelId, m.CreatedAt });
            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContextSnapshot>(entity =>
        {
            entity.HasKey(s => s.Id);
            // Versão monotônica por chat (SPEC-20261020-context-snapshots RF-003).
            entity.HasIndex(s => new { s.ChatId, s.Version }).IsUnique();
            entity.HasIndex(s => new { s.ChatId, s.CreatedAt });
        });

        // Ledger de uso (SPEC-20261023-run-cost-budgets RF-001): 1 linha por
        // run; agregações mensais por usuário e cadeia por ParentRunId.
        modelBuilder.Entity<RunUsageRecord>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.RunId).IsUnique();
            entity.HasIndex(r => new { r.UserId, r.CreatedAt });
            entity.HasIndex(r => r.ParentRunId);
        });

        // Uma política por sujeito (user:{id}|group:{id}|global).
        modelBuilder.Entity<BudgetPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.Subject).IsUnique();
        });
    }
}
