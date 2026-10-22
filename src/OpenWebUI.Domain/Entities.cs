namespace OpenWebUI.Domain;

/// <summary>Usuário da plataforma.</summary>
public class User
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>E-mail único usado no login.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Hash da senha (PBKDF2 via ASP.NET Core PasswordHasher).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Papel do usuário: admin, user ou pending.</summary>
    public string Role { get; set; } = "pending";

    /// <summary>URL ou data-url da imagem de perfil.</summary>
    public string? ProfileImageUrl { get; set; }

    /// <summary>Fuso horário preferido (ex.: "America/Sao_Paulo").</summary>
    public string? Timezone { get; set; }

    /// <summary>Configurações de UI do usuário, serializadas como JSON.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>Permissões próprias do usuário (JSON no formato de permissões granulares);
    /// recebe os defaults no signup e faz união com as permissões dos grupos.</summary>
    public string PermissionsJson { get; set; } = "{}";

    /// <summary>Última atividade registrada (epoch seconds; atualizado no signin e na conexão do hub).</summary>
    public long? LastActiveAt { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Chats do usuário.</summary>
    public List<Chat> Chats { get; set; } = [];
}

/// <summary>Chave de API gerada por um usuário (prefixo sk-).</summary>
public class ApiKey
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da chave.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Hash SHA-256 da chave de API (a chave em si só é exibida na criação).</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Batalha de arena: duas respostas anônimas de modelos sorteados aguardando voto.</summary>
public class ArenaBattle
{
    /// <summary>Identificador único (GUID interno).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário que disparou a batalha.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Id do modelo arena usado.</summary>
    public string ArenaModelId { get; set; } = string.Empty;

    /// <summary>Modelo real da resposta A (revelado após o voto).</summary>
    public string ModelA { get; set; } = string.Empty;

    /// <summary>Modelo real da resposta B (revelado após o voto).</summary>
    public string ModelB { get; set; } = string.Empty;

    /// <summary>Conteúdo da resposta A.</summary>
    public string ResponseA { get; set; } = string.Empty;

    /// <summary>Conteúdo da resposta B.</summary>
    public string ResponseB { get; set; } = string.Empty;

    /// <summary>Vencedor: "a", "b", "tie" ou "both_bad"; null até o voto.</summary>
    public string? Winner { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Momento do voto (epoch seconds); null até votar.</summary>
    public long? VotedAt { get; set; }
}

/// <summary>Conversa pertencente a um usuário.</summary>
public class Chat
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do chat.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navegação para o dono.</summary>
    public User? User { get; set; }

    /// <summary>Título exibido na barra lateral.</summary>
    public string Title { get; set; } = "New Chat";

    /// <summary>Modelos selecionados, serializados como JSON.</summary>
    public string ModelsJson { get; set; } = "[]";

    /// <summary>Tags do chat, serializadas como JSON.</summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>Indica se o chat está arquivado.</summary>
    public bool Archived { get; set; }

    /// <summary>Indica se o chat está fixado no topo da lista.</summary>
    public bool Pinned { get; set; }

    /// <summary>Id da pasta que contém o chat, quando organizado em pastas.</summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// Chat pai quando este chat foi criado por <c>delegate_task</c>
    /// (SPEC-20261010-runs-hierarchy); <see langword="null"/> = chat raiz.
    /// </summary>
    public string? ParentChatId { get; set; }

    /// <summary>
    /// Time do chat (SPEC-20261021-harness-seams RF-E1): id do chat raiz do
    /// time — filhos herdam no spawn via <c>delegate_task team:true</c>;
    /// a raiz aponta para si mesma. <see langword="null"/> = fora de team.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Papel no team: <c>leader</c> (raiz) ou <c>member</c> (delegado).
    /// </summary>
    public string? TeamRole { get; set; }

    /// <summary>Ids das tools habilitadas neste chat, serializados como JSON.</summary>
    public string ToolIdsJson { get; set; } = "[]";

    /// <summary>Id público de compartilhamento (rota /s/{shareId}), quando compartilhado.</summary>
    public string? ShareId { get; set; }

    /// <summary>
    /// Preset de permissão de tools da conversa
    /// (SPEC-20261007-chat-tool-streaming RF-004): <c>allow-readonly</c>
    /// (mutáveis negadas), <c>approve-mutations</c> (mutáveis pedem aprovação
    /// — default), <c>smart</c> (executa sem perguntar; mutações de arquivo
    /// pedem aprovação), <c>always-allow</c> (tudo executa sem perguntar)
    /// ou <c>auto</c> (tiers de risco — legacy).
    /// </summary>
    public string ApprovalPreset { get; set; } = "approve-mutations";

    /// <summary>
    /// Modo do agente da conversa (SPEC-20261009-agent-modes-plan-build
    /// RF-001 + SPEC-15): <c>chat</c> (default — conversa comum, tools de
    /// workspace fora do spec anunciado), <c>plan</c> (somente leitura —
    /// tools de escrita/execução não são anunciadas e calls delas devolvem
    /// erro estruturado; o agente encerra pelo <c>plan_exit</c>, que
    /// promove a build após aprovação do dono) ou <c>build</c> (toolset
    /// completo, gate pelo preset — exige repositório vinculado; sem
    /// binding o modo efetivo resolve como <c>chat</c>).
    /// </summary>
    public string Mode { get; set; } = "chat";

    /// <summary>
    /// Revisor de run (SPEC-20261013-run-advisor RF-005): quando ligado, um
    /// modelo secundário (papel <c>advisor</c>) observa a run e injeta
    /// conselhos curtos no feed/contexto. Default do admin em
    /// <c>advisor.default</c>; gate global em <c>advisor.enabled</c>.
    /// </summary>
    public bool AdvisorEnabled { get; set; }

    /// <summary>
    /// Leaf do ramo ativo (SPEC-20261013-chat-branching RF-001): a árvore de
    /// mensagens é caminhada <c>LeafMessageId→root</c> por
    /// <see cref="ChatMessage.ParentId"/>; null = chat legado/linear — a
    /// ponta implícita é a maior <see cref="ChatMessage.Position"/>.
    /// </summary>
    public string? LeafMessageId { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Mensagens em ordem cronológica.</summary>
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>Mensagem individual de um chat.</summary>
public class ChatMessage
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Chat ao qual a mensagem pertence.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Navegação para o chat.</summary>
    public Chat? Chat { get; set; }

    /// <summary>Papel: system, user ou assistant.</summary>
    public string Role { get; set; } = "user";

    /// <summary>Conteúdo textual em Markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Modelo que produziu a mensagem (somente assistant).</summary>
    public string? Model { get; set; }

    /// <summary>Posição da mensagem dentro do chat.</summary>
    public int Position { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long Timestamp { get; set; }

    /// <summary>Versões anteriores do conteúdo (JSON: [{content, model, timestamp}]).</summary>
    public string VersionsJson { get; set; } = "[]";

    /// <summary>
    /// Tool calls emitidas pelo modelo nesta mensagem (JSON — somente
    /// <c>assistant</c>; SPEC-20261007-chat-tool-streaming).
    /// </summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>
    /// Id do tool call que originou esta mensagem (somente <c>tool</c>).
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Anexos da mensagem (SPEC-20261013-chat-media-attachments RF-003):
    /// JSON de <c>ChatMessageFileModel[]</c> — só metadados
    /// (id/name/contentType/size/type), nunca path nem bytes.
    /// </summary>
    public string? FilesJson { get; set; }

    /// <summary>
    /// Mensagem anterior no ramo (SPEC-20261013-chat-branching RF-001):
    /// os nós do chat formam uma árvore; null = raiz ou dado legado — no
    /// walk do caminho, a aresta implícita legada é "mensagem com maior
    /// <see cref="Position"/> anterior" (compatibilidade sem backfill).
    /// </summary>
    public string? ParentId { get; set; }
}

/// <summary>Status possíveis de uma run desacoplada de chat.</summary>
public static class ChatRunStatus
{
    /// <summary>Na fila aguardando o dispatcher.</summary>
    public const string Queued = "queued";

    /// <summary>Em execução pelo dispatcher.</summary>
    public const string Running = "running";

    /// <summary>Finalizada com sucesso.</summary>
    public const string Completed = "completed";

    /// <summary>Finalizada com erro.</summary>
    public const string Failed = "failed";

    /// <summary>Interrompida por stop do usuário.</summary>
    public const string Stopped = "stopped";

    /// <summary>Suspensa pelo dono — retoma com resume (RF-013 chat-agent-parity).</summary>
    public const string Paused = "paused";

    /// <summary>Órfã de restart: estava queued/running quando o servidor caiu.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>
/// Run de chat desacoplada da conexão do cliente
/// (SPEC-20261007-chat-detached-runs): a resposta do assistant é gerada no
/// servidor e o cliente apenas anexa ao stream — fechar a aba não mata a run.
/// </summary>
public class ChatRun
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Chat ao qual a run pertence.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Navegação para o chat.</summary>
    public Chat? Chat { get; set; }

    /// <summary>Dono (mesmo do chat — usado para isolamento).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Modelo pedido no envio.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Status atual (ver <see cref="ChatRunStatus"/>).</summary>
    public string Status { get; set; } = ChatRunStatus.Queued;

    /// <summary>Snapshot do request de completion (serializado no enqueue).</summary>
    public string RequestJson { get; set; } = string.Empty;

    /// <summary>Conteúdo parcial do assistant (checkpoint a cada iteração).</summary>
    public string? PartialContent { get; set; }

    /// <summary>Erro final, quando <see cref="ChatRunStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Metadados da run em JSON (SPEC-20261016-agent-steps-usage RF-004/005):
    /// <c>{"usage":{"steps":N,"promptTokens":…,"completionTokens":…,
    /// "costUsd":…,"perStep":[…]}}</c> — tokens só quando o provider reporta.
    /// </summary>
    public string? MetaJson { get; set; }

    /// <summary>
    /// Run pai quando esta run foi spawnada por <c>delegate_task</c>
    /// (SPEC-20261010-runs-hierarchy); <see langword="null"/> = run raiz.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Início da execução (epoch seconds); null enquanto ativa.</summary>
    public long? StartedAt { get; set; }

    /// <summary>Finalização (epoch seconds); null enquanto ativa.</summary>
    public long? CompletedAt { get; set; }

    /// <summary>
    /// Instância que claima a execução (SPEC-20261016-run-recovery RF-005):
    /// guid por processo — outra instância não resume run com heartbeat fresco.
    /// </summary>
    public string? OwnerInstance { get; set; }

    /// <summary>Último heartbeat de execução (epoch seconds) — RF-005.</summary>
    public long? HeartbeatAt { get; set; }
}

/// <summary>
/// Evento durável de uma run (SPEC-20261016-run-recovery RF-001) — append-only:
/// <c>step_started</c>, <c>content_delta</c> (agregado), <c>tool_call</c>,
/// <c>tool_result</c>, <c>step_finished</c>, <c>phase</c>, <c>note</c> e
/// <c>snapshot</c> (loop-state por fronteira de round, RF-002). O seq é
/// monotônico por run e a reconstrução usa último snapshot + eventos
/// posteriores; nunca há update/delete.
/// </summary>
public class ChatRunEvent
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Run dona do evento.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Sequência monotônica por run (1..N).</summary>
    public long Seq { get; set; }

    /// <summary>Tipo do evento (<c>snapshot</c>, <c>tool_call</c>…).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Payload JSON do evento (segredos já mascarados).</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>
/// Mensagem durável de steer/queue para uma run viva
/// (SPEC-20261010-steer-queue): <c>steer</c> é injetada na próxima fronteira
/// de rodada do tool loop; <c>queue</c> só é promovida quando a run ficaria
/// ociosa. Promovida vira mensagem <c>user</c> no chat e entra no histórico.
/// </summary>
public class ChatRunSteer
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Run alvo.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Chat da run (desnormalizado pra consulta/limpeza).</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Texto do usuário.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Modo: <c>steer</c> (próxima fronteira) ou <c>queue</c> (ao ficar ociosa).</summary>
    public string Mode { get; set; } = "steer";

    /// <summary>Estado: <c>pending</c> ou <c>promoted</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Criação (epoch seconds).</summary>
    public long Timestamp { get; set; }
}

/// <summary>Status possíveis de um job de background de chat.</summary>
public static class ChatJobStatus
{
    /// <summary>Processo em execução.</summary>
    public const string Running = "running";

    /// <summary>Terminou com exit code 0.</summary>
    public const string Completed = "completed";

    /// <summary>Terminou com erro/exit code não-zero.</summary>
    public const string Failed = "failed";

    /// <summary>Morto pelo dono via job_kill/DELETE.</summary>
    public const string Killed = "killed";
}

/// <summary>
/// Job de background spawnado pelo chat
/// (SPEC-20261007-chat-agent-tools RF-004): um comando classificado pelo
/// gateway de risco que roda desacoplado da run — sobrevive a ela, escreve
/// stdout+stderr num arquivo próprio e é consultável via
/// <c>job_list/job_output/job_kill</c> e os endpoints REST.
/// </summary>
public class ChatJob
{
    /// <summary>Identificador único (GUID curto — aparece no transcript).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Chat ao qual o job pertence — null para jobs fora de chat (ex.: test-run do IDE).</summary>
    public string? ChatId { get; set; }

    /// <summary>Dono (mesmo do chat — isolamento de leitura/kill).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Run que originou o job, quando conhecida.</summary>
    public string? RunId { get; set; }

    /// <summary>Comando executado (já validado pelo classifier).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Diretório de trabalho (workspace do usuário).</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Status atual (ver <see cref="ChatJobStatus"/>).</summary>
    public string Status { get; set; } = ChatJobStatus.Running;

    /// <summary>PID do processo no host, quando vivo.</summary>
    public int? Pid { get; set; }

    /// <summary>Caminho do arquivo com stdout+stderr capturados.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Exit code final, quando terminou.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Erro de spawn/monitoramento, quando houver.</summary>
    public string? Error { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long StartedAt { get; set; }

    /// <summary>Finalização (epoch seconds); null enquanto vivo.</summary>
    public long? FinishedAt { get; set; }
}

/// <summary>
/// Subscription Web Push de um navegador
/// (SPEC-20261007-chat-notifications): endpoint do push service (FCM/Mozilla)
/// mais o material de criptografia do cliente, gravado quando o usuário opta
/// por receber aviso de run concluída com a aba fechada. O endpoint é único —
/// re-subscribe do mesmo navegador apenas rotaciona as chaves.
/// </summary>
public class ChatPushSubscription
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da subscription.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Endpoint do push service (único por navegador).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Chave pública ECDH do cliente (base64url) para cifrar o payload.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Segredo de auth do cliente (base64url) para cifrar o payload.</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>User-Agent do navegador (hint para ops/debug).</summary>
    public string? UserAgent { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização/renovação (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Entrada chave-valor de configuração persistida (espelha a tabela config do Open WebUI).</summary>
public class ConfigEntry
{
    /// <summary>Chave da configuração.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Valor serializado em JSON.</summary>
    public string ValueJson { get; set; } = string.Empty;

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Pasta para organizar chats na barra lateral.</summary>
public class Folder
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da pasta.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome exibido.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Pasta-pai, para pastas aninhadas (opcional).</summary>
    public string? ParentId { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Prompt personalizado do workspace (acionado por /comando).</summary>
public class Prompt
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do prompt.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Comando que aciona o prompt (ex.: "resumir" → /resumir).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Título exibido.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Texto do prompt com placeholders {{CLIPBOARD}}, {{USER:name}} etc.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Arquivo enviado pelo usuário para uso como contexto em chats.</summary>
public class FileEntry
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do arquivo.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome original do arquivo.</summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>Tipo MIME reportado no upload.</summary>
    public string? ContentType { get; set; }

    /// <summary>Caminho no disco onde o binário foi gravado.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Tamanho em bytes.</summary>
    public long Size { get; set; }

    /// <summary>Texto extraído do arquivo (somente formatos de texto suportados).</summary>
    public string? ExtractedText { get; set; }

    /// <summary>URL de origem quando o arquivo veio de process/url|youtube.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Modelo personalizado do workspace: camada sobre um modelo base com system prompt e parâmetros.</summary>
public class ModelEntry
{
    /// <summary>Identificador único (GUID interno).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do modelo personalizado.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome público exibido no seletor de modelos.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Modelo base usado na completion (id do provedor); null em modelos arena.</summary>
    public string? BaseModelId { get; set; }

    /// <summary>Metadados do modelo em JSON (ex.: {"arena": true, "model_ids": [...]}).</summary>
    public string? MetaJson { get; set; }

    /// <summary>Access grants (user/group/* × read/write) em JSON; null = visibilidade padrão.</summary>
    public string? AccessGrantsJson { get; set; }

    /// <summary>System prompt aplicado antes das mensagens do usuário.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Parâmetros de geração (temperature, top_p, etc.), serializados como JSON.</summary>
    public string? ParamsJson { get; set; }

    /// <summary>URL ou data-url da imagem do modelo.</summary>
    public string? ProfileImageUrl { get; set; }

    /// <summary>Sugestões de prompt exibidas em chat vazio, serializadas como JSON.</summary>
    public string? SuggestionPromptsJson { get; set; }

    /// <summary>Indica se o modelo está ativo/visível.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Memória persistente do usuário injetada no contexto dos chats.</summary>
public class MemoryEntry
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da memória.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Conteúdo lembrado (fato, preferência, contexto).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Nota do usuário com conteúdo em Markdown.</summary>
public class Note
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da nota.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Título da nota.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Conteúdo em Markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Grants de acesso JSON [{principal_type, principal_id, permission}].</summary>
    public string AccessGrantsJson { get; set; } = "[]";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Avaliação (joinha) registrada em uma mensagem de assistant.</summary>
public class Feedback
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário que avaliou.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Chat que contém a mensagem avaliada.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Mensagem avaliada.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Modelo que produziu a mensagem.</summary>
    public string? ModelId { get; set; }

    /// <summary>Avaliação: 1 (positiva) ou -1 (negativa).</summary>
    public int Rating { get; set; }

    /// <summary>Motivo opcional da avaliação.</summary>
    public string? Reason { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Grupo de usuários com permissões granulares (RBAC).</summary>
public class Group
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome do grupo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Flags de permissão serializadas como JSON (workspace/sharing/chat).</summary>
    public string PermissionsJson { get; set; } = "{}";

    /// <summary>Domínios de e-mail com membership automático no signup (JSON array).</summary>
    public string AllowedDomainsJson { get; set; } = "[]";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Membros do grupo.</summary>
    public List<GroupMember> Members { get; set; } = [];
}

/// <summary>Vínculo usuário ↔ grupo, com papel dentro do grupo.</summary>
public class GroupMember
{
    /// <summary>Grupo do vínculo.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Grupo navegação.</summary>
    public Group? Group { get; set; }

    /// <summary>Usuário do vínculo.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Papel no grupo: "admin" (gerencia membros) ou "member".</summary>
    public string Role { get; set; } = "member";

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Conta externa OAuth/OIDC vinculada a um usuário local.</summary>
public class OAuthAccount
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário local dono da conta.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Provedor (google, github, microsoft, oidc).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Identificador da conta no provedor (sub/id).</summary>
    public string ProviderAccountId { get; set; } = string.Empty;

    /// <summary>E-mail reportado pelo provedor.</summary>
    public string? Email { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Coleção nomeada de arquivos para contexto RAG (aba Knowledge).</summary>
public class KnowledgeCollection
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da coleção.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Nome único por usuário.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Grants de acesso JSON [{principal_type, principal_id, permission}].</summary>
    public string AccessGrantsJson { get; set; } = "[]";

    /// <summary>Arquivos vinculados.</summary>
    public List<KnowledgeFile> Files { get; set; } = [];

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Vínculo arquivo↔coleção de knowledge.</summary>
public class KnowledgeFile
{
    /// <summary>Identificador único do vínculo (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Coleção dona.</summary>
    public string CollectionId { get; set; } = string.Empty;

    /// <summary>Coleção navegação.</summary>
    public KnowledgeCollection? Collection { get; set; }

    /// <summary>Arquivo vinculado (FileEntry).</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Inclusão (epoch seconds).</summary>
    public long AddedAt { get; set; }
}

/// <summary>Trecho de texto indexado com embedding vetorial para RAG.</summary>
public class EmbeddingChunk
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário dono do arquivo origem (isolamento por conta).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Arquivo origem (FileEntry).</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Ordem do chunk dentro do arquivo.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Texto do chunk.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Vetor de embedding serializado (JSON array de floats).</summary>
    public string EmbeddingJson { get; set; } = string.Empty;

    /// <summary>
    /// Norma L2 do vetor, gravada na indexação. Chunks antigos (0) recalculam
    /// a norma em memória até a próxima reindexação.
    /// </summary>
    public double EmbeddingNorm { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}
/// <summary>Tool externa invocável pelo modelo (function calling via HTTP ou código Python).</summary>
public class Tool
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da tool — <c>null</c> em tools virtuais MCP (admin-provisionadas).</summary>
    public string? UserId { get; set; }

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição exibida na UI.</summary>
    public string? Description { get; set; }

    /// <summary>Spec da função no formato OpenAI {"type":"function","function":{...}}.</summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>Endpoint HTTP POST que executa a tool (server-side, nunca exposto).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Fonte Python da tool (convenção do upstream: <c>class Tools</c> com
    /// métodos públicos chamáveis). Quando preenchida, a execução roda em
    /// subprocess Python em vez de HTTP — com os privilégios do servidor.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// Se a tool exige aprovação antes de executar
    /// (SPEC-20261007-chat-tool-streaming RF-003). Tools com
    /// <see cref="Code"/> Python ou URL virtual MCP são sempre tratadas como
    /// mutáveis, independente deste flag.
    /// </summary>
    public bool RequiresApproval { get; set; }

    /// <summary>Se a tool está habilitada.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Canal de conversa em grupo (múltiplos usuários, estilo chat de equipe).</summary>
public class Channel
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome do canal.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição opcional.</summary>
    public string? Description { get; set; }

    /// <summary>Tipo do canal: "channel" (grupo) — "dm" fica para fase futura.</summary>
    public string Type { get; set; } = "channel";

    /// <summary>Usuário criador (sempre admin do canal).</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Grants de acesso JSON [{principal_type, principal_id, permission}] (ex.: leitura pública "*").</summary>
    public string AccessGrantsJson { get; set; } = "[]";

    /// <summary>Membros do canal.</summary>
    public List<ChannelMember> Members { get; set; } = [];

    /// <summary>Mensagens do canal.</summary>
    public List<ChannelMessage> Messages { get; set; } = [];
}

/// <summary>Vínculo usuário↔canal com papel dentro do canal.</summary>
public class ChannelMember
{
    /// <summary>Canal.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Canal navegação.</summary>
    public Channel? Channel { get; set; }

    /// <summary>Usuário membro.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Papel no canal: "admin" (gerencia membros/canal) ou "member".</summary>
    public string Role { get; set; } = "member";

    /// <summary>Última leitura (epoch seconds) — base do unread_count.</summary>
    public long LastReadAt { get; set; }

    /// <summary>Entrada (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Mensagem de canal; autor é usuário ou modelo (UserId nulo = modelo).</summary>
public class ChannelMessage
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Canal dono da mensagem.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Canal navegação.</summary>
    public Channel? Channel { get; set; }

    /// <summary>Usuário autor; nulo quando a mensagem é do modelo (@menção).</summary>
    public string? UserId { get; set; }

    /// <summary>Usuário navegação.</summary>
    public User? User { get; set; }

    /// <summary>Identificador do modelo autor, quando UserId é nulo.</summary>
    public string? ModelId { get; set; }

    /// <summary>Mensagem pai quando é reply de thread (nulo = mensagem raiz).</summary>
    public string? ParentId { get; set; }

    /// <summary>Mensagem fixada no canal.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Conteúdo em texto/markdown.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Reação (emoji) de um usuário a uma mensagem de canal.</summary>
public class ChannelMessageReaction
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Mensagem alvo.</summary>
    public string ChannelMessageId { get; set; } = string.Empty;

    /// <summary>Mensagem navegação.</summary>
    public ChannelMessage? Message { get; set; }

    /// <summary>Usuário que reagiu.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Emoji (ex.: "👍", ":smile:").</summary>
    public string Emoji { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Automação: prompt executado em um agendamento recorrente.</summary>
public class Automation
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da automação.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navegação para o dono.</summary>
    public User? User { get; set; }

    /// <summary>Nome exibido na lista.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Prompt enviado ao modelo a cada execução.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Modelo usado na execução.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Tipo de agenda: interval, daily ou weekly.</summary>
    public string ScheduleKind { get; set; } = "interval";

    /// <summary>Intervalo em minutos quando ScheduleKind = interval (mín. 1).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Hora do dia "HH:mm" (UTC) quando ScheduleKind = daily/weekly.</summary>
    public string? TimeOfDay { get; set; }

    /// <summary>Dia da semana 0-6 (domingo=0, UTC) quando ScheduleKind = weekly.</summary>
    public int? Weekday { get; set; }

    /// <summary>Indica se a automação está habilitada.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tipo de ação disparada (SPEC-20261016-automation-actions):
    /// <c>prompt</c> (default) | <c>skill</c> | <c>mcp</c> | <c>reminder</c>
    /// | <c>webhook</c>.
    /// </summary>
    public string ActionKind { get; set; } = "prompt";

    /// <summary>Payload JSON da ação, validado por <see cref="ActionKind"/>.</summary>
    public string? ActionJson { get; set; }

    /// <summary>Modo de permissão da ação: <c>default</c> | <c>auto</c> | <c>bypass</c>.</summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>Chat reutilizado pelas execuções, quando configurado.</summary>
    public string? ChatId { get; set; }

    /// <summary>Próxima execução agendada (epoch seconds, UTC).</summary>
    public long? NextRunAt { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }

    /// <summary>Execuções registradas.</summary>
    public List<AutomationRun> Runs { get; set; } = [];
}

/// <summary>Execução registrada de uma automação.</summary>
public class AutomationRun
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Automação executada.</summary>
    public string AutomationId { get; set; } = string.Empty;

    /// <summary>Navegação para a automação.</summary>
    public Automation? Automation { get; set; }

    /// <summary>Resultado: ok ou failed.</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Ação executada (cópia de <see cref="Automation.ActionKind"/> no disparo).</summary>
    public string ActionKind { get; set; } = "prompt";

    /// <summary>Resumo do output da ação (capado a 4KB), quando aplicável.</summary>
    public string? Output { get; set; }

    /// <summary>Mensagem de erro quando Status = failed.</summary>
    public string? Error { get; set; }

    /// <summary>Chat criado com o prompt e a resposta, quando a execução completa.</summary>
    public string? ChatId { get; set; }

    /// <summary>Início da execução (epoch seconds).</summary>
    public long StartedAt { get; set; }

    /// <summary>Fim da execução (epoch seconds).</summary>
    public long FinishedAt { get; set; }
}

/// <summary>
/// Webhook de automação (SPEC-20261007-chat-agent-parity RF-021):
/// <c>POST /api/v1/hooks/{Id}</c> anônimo dispara uma run no
/// <see cref="ChatId"/> vinculado — n8n, cron, CI ou qualquer sistema
/// externo. O próprio Id (GUID) é o token da URL.
/// </summary>
public class AutomationHook
{
    /// <summary>Identificador único (GUID) — usado como token na URL do webhook.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do hook (isola dados e executa a run como ele).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navegação para o dono.</summary>
    public User? User { get; set; }

    /// <summary>Chat que recebe a mensagem e a run disparada pelo webhook.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Navegação para o chat vinculado.</summary>
    public Chat? Chat { get; set; }

    /// <summary>Nome exibido na lista de hooks.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Indica se o hook aceita disparos.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Último disparo aceito (epoch seconds); null se nunca disparou.</summary>
    public long? LastFiredAt { get; set; }

    /// <summary>
    /// Segredo HMAC-SHA256 do webhook (SPEC-20261021-harness-seams RF-D1):
    /// quando setado exige <c>X-Hub-Signature-256: sha256=&lt;hmac&gt;</c>
    /// com comparação timing-safe. Null = hook legado sem assinatura.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Provider do remetente: <c>generic</c> (default; header
    /// <c>X-Event-Type</c> opcional) ou <c>github</c> (<c>X-GitHub-Event</c>
    /// + dedup por <c>X-GitHub-Delivery</c>).
    /// </summary>
    public string Provider { get; set; } = "generic";

    /// <summary>
    /// Filtro de eventos aceitos (JSON array de tipos); null/vazio = todos.
    /// </summary>
    public string? EventsJson { get; set; }

    /// <summary>
    /// Template do prompt com placeholders <c>{{campo.path}}</c> resolvidos
    /// do payload JSON (máx. 4KB interpolado); null = payload cru vira prompt.
    /// </summary>
    public string? PromptTemplate { get; set; }

    /// <summary>Repo <c>owner/name</c> a vincular no chat da run disparada.</summary>
    public string? RepoSlug { get; set; }

    /// <summary>Persona (slug/id de Skill ativa) aplicada como system do disparo.</summary>
    public string? PersonaId { get; set; }

    /// <summary>Modo do agente para a run (<c>chat</c>|<c>plan</c>|<c>build</c>).</summary>
    public string? AgentMode { get; set; }
}

/// <summary>
/// Entrega/auditoria de um disparo de webhook
/// (SPEC-20261021-harness-seams RF-D2): uma linha por entrega aceita,
/// filtrada ou rejeitada — <see cref="DeliveryKey"/> é a chave de
/// idempotência (mesmo delivery ⇒ retorna a run original).
/// </summary>
public class AutomationHookDelivery
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Hook que recebeu a entrega.</summary>
    public string HookId { get; set; } = string.Empty;

    /// <summary>
    /// Chave de dedup do remetente (<c>X-GitHub-Delivery</c>,
    /// <c>X-Delivery-Id</c>); null = entrega sem idempotência declarada.
    /// </summary>
    public string? DeliveryKey { get; set; }

    /// <summary>Tipo de evento (<c>X-GitHub-Event</c>/<c>X-Event-Type</c>).</summary>
    public string? EventType { get; set; }

    /// <summary>Recebimento (epoch seconds).</summary>
    public long ReceivedAt { get; set; }

    /// <summary><c>accepted</c> | <c>filtered</c> | <c>rejected</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "accepted";

    /// <summary>Run criada pelo disparo, quando aceito.</summary>
    public string? RunId { get; set; }

    /// <summary>Erro interno, quando <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Aviso de segurança (ex.: hook legado sem assinatura).</summary>
    public string? SecurityWarning { get; set; }
}

/// <summary>
/// Tarefa compartilhada de um agent team
/// (SPEC-20261021-harness-seams RF-E2): coordenação durável por
/// <see cref="TeamId"/> — membros criam/claimam/concluem via
/// <c>builtin:team_task</c>.
/// </summary>
public class TeamTask
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Team (id do chat raiz).</summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>Título da tarefa.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary><c>pending</c> | <c>in_progress</c> | <c>done</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Chat membro que claimou a tarefa.</summary>
    public string? AssigneeChatId { get; set; }

    /// <summary>Chat membro que criou a tarefa.</summary>
    public string? CreatedByChatId { get; set; }

    /// <summary>Resumo do resultado ao concluir.</summary>
    public string? Result { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Combo de modelos com failover (SPEC-20261016-model-combos RF-001):
/// grupo nomeado de modelos ordenados com estratégia de resolução,
/// rate limit compartilhado e cooldown por falha reportada. Referenciado
/// como <c>combo:{Id}</c> onde um modelo é aceito.
/// </summary>
public class ModelCombo
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do combo; null = global (só admin cria).</summary>
    public string? UserId { get; set; }

    /// <summary>Nome único por dono.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Estratégia: <c>priority</c> | <c>round-robin</c> | <c>priority-round-robin</c>.</summary>
    public string Strategy { get; set; } = "priority";

    /// <summary>Teto de requisições por minuto do combo (janela deslizante); null = sem limite.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>Teto de tokens por minuto do combo; null = sem limite.</summary>
    public int? TokensPerMinute { get; set; }

    /// <summary>Indica se o combo aceita resoluções.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cursor persistido do round-robin (RF-002 — "persiste em kv").</summary>
    public int RoundRobinCursor { get; set; }

    /// <summary>Itens ordenados do combo.</summary>
    public List<ModelComboItem> Items { get; set; } = [];

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Modelo participante de um <see cref="ModelCombo"/>, ordenado por <see cref="Position"/>.</summary>
public class ModelComboItem
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Combo ao qual o item pertence.</summary>
    public string ComboId { get; set; } = string.Empty;

    /// <summary>Id do modelo/provider (ex.: <c>gpt-4o</c>, <c>ollama:llama3</c>).</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Ordem de prioridade dentro do combo (menor primeiro).</summary>
    public int Position { get; set; }
}

/// <summary>Banner de aviso exibido no topo do app (CRUD admin).</summary>
public class Banner
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Tipo visual: info, warning, error ou success.</summary>
    public string Type { get; set; } = "info";

    /// <summary>Título curto do aviso.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Conteúdo do aviso (texto).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Se o usuário pode dispensar o banner.</summary>
    public bool Dismissible { get; set; } = true;

    /// <summary>Criação (epoch seconds; usado para ordenar).</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}


/// <summary>Calendário do usuário (colaborativo via grants).</summary>
public class Calendar
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do calendário.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Cor em hex (ex.: #3b82f6).</summary>
    public string? Color { get; set; }

    /// <summary>Grants de acesso JSON [{principal_type, principal_id, permission}].</summary>
    public string AccessGrantsJson { get; set; } = "[]";

    /// <summary>Eventos do calendário.</summary>
    public List<CalendarEvent> Events { get; set; } = [];

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Evento de um calendário.</summary>
public class CalendarEvent
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Calendário dono do evento.</summary>
    public string CalendarId { get; set; } = string.Empty;

    /// <summary>Navegação para o calendário.</summary>
    public Calendar? Calendar { get; set; }

    /// <summary>Título.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Início (epoch seconds).</summary>
    public long StartTs { get; set; }

    /// <summary>Fim (epoch seconds).</summary>
    public long EndTs { get; set; }

    /// <summary>Cor opcional em hex.</summary>
    public string? Color { get; set; }

    /// <summary>Notas/descrição opcional.</summary>
    public string? Notes { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Webhook de notificação: por usuário (OwnerId) ou global/admin (OwnerId nulo).</summary>
public class NotificationWebhook
{
    /// <summary>Identificador único.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono do webhook; nulo = webhook global do admin.</summary>
    public string? OwnerId { get; set; }

    /// <summary>URL de destino (http/https).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Eventos habilitados, em CSV (ex.: "user.pending,automation.failed").</summary>
    public string Events { get; set; } = string.Empty;

    /// <summary>Segredo HMAC usado para assinar payloads (nunca retornado).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Se o webhook está habilitado.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criação (unix s).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Notificação persistida do feed in-app (sino) — mesma construção
/// de título/corpo/link do Web Push, mas navegável sem depender de push
/// (SPEC-20261009-notification-feed D3).</summary>
public class Notification
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da notificação (isolamento por usuário).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Tipo/origem do evento (ex.: run.completed, run.failed).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Título curto exibido na lista.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Corpo/trecho da notificação (opcional).</summary>
    public string? Body { get; set; }

    /// <summary>Deep-link in-app aberto ao clicar (ex.: /c/{chatId}).</summary>
    public string? Link { get; set; }

    /// <summary>Leitura (epoch seconds); null = não lida.</summary>
    public long? ReadAt { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Skill do workspace — conteúdo de instrução anexável a modelos custom
/// (MetaJson.skill_ids) e injetado no system prompt.</summary>
public class Skill
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da skill.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Descrição exibida na UI.</summary>
    public string? Description { get; set; }

    /// <summary>Conteúdo markdown/instrução injetado no system prompt.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Se a skill está habilitada.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Memória do agente (SPEC-20261010-agent-memory): notas duráveis
/// que o agente salva via <c>builtin:memory_save</c> e recupera via
/// <c>builtin:memory_search</c>. Escopo <c>global</c> (vale para todos os
/// chats do usuário) ou <c>repo</c> (vinculada ao slug do repo — só entra
/// no prompt de chats bound àquele repo).</summary>
public class AgentMemory
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da memória.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Escopo: <c>global</c> ou <c>repo</c>.</summary>
    public string Scope { get; set; } = "global";

    /// <summary>Slug do repo (owner/repo) quando Scope=repo; null em global.</summary>
    public string? RepoSlug { get; set; }

    /// <summary>Título curto (chave de upsert por user+scope+repo).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Conteúdo lembrado.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Function do ecossistema de plugins — registro com manifest; a
/// execução é delegada a pipelines externos (nenhum código arbitrário roda
/// no servidor .NET).</summary>
public class Function
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da function (admin que a registrou).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo da function: filter, pipe ou action.</summary>
    public string Type { get; set; } = "filter";

    /// <summary>Manifest da function (JSON): metadados, schema de valves, etc.</summary>
    public string ManifestJson { get; set; } = "{}";

    /// <summary>Valores de valves configurados (JSON; nunca expostos com secrets).</summary>
    public string? ValvesJson { get; set; }

    /// <summary>Se a function está ativa.</summary>
    public bool Active { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Servidor de pipelines externo — hospeda pipes expostos como
/// modelos `pipeline:{id}` no seletor.</summary>
public class PipelineServer
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL base do servidor (ex.: http://pipelines:9099).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Chave de API (Bearer) — nunca retornada pela API.</summary>
    public string? Key { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>Servidor MCP (Model Context Protocol) registrado pelo admin —
/// fonte de tools virtuais <c>mcp_...</c> descobertas via <c>tools/list</c>.</summary>
public class McpServer
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transporte: <c>stdio</c> (subprocess) ou <c>http</c> (streamable HTTP).</summary>
    public string Transport { get; set; } = "http";

    /// <summary>Comando a executar (transport stdio, ex.: <c>npx</c>).</summary>
    public string? Command { get; set; }

    /// <summary>Argumentos do comando em JSON array (stdio).</summary>
    public string? ArgsJson { get; set; }

    /// <summary>Nomes de variáveis de ambiente passadas ao processo em JSON array
    /// (stdio) — só os nomes são persistidos; valores lidos do ambiente no spawn.</summary>
    public string? EnvJson { get; set; }

    /// <summary>URL do endpoint MCP (transport http, streamable HTTP).</summary>
    public string? Url { get; set; }

    /// <summary>Headers HTTP extras em JSON object (http) — mascarados nas leituras.</summary>
    public string? HeadersJson { get; set; }

    /// <summary>Se o server está habilitado (tools disponíveis no function calling).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Último erro de conexão/discovery (legível, nunca exception).</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Dono do servidor (SPEC-20261014-config-layers RF-002): null = global
    /// provisionado pelo admin; preenchido = servidor do usuário (credenciais
    /// e tools isoladas por dono).
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Persona de agente acionável externamente (SPEC-20261016-agent-personas-
/// channels RF-001): identidade nomeada com system prompt, modelo (ou
/// <c>combo:{id}</c>) e nível de guardrails — o mesmo agente atende no
/// chat, numa automação ou no endpoint <c>/message</c> (canal webhook).
/// </summary>
public class AgentPersona
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Dono da persona (isolamento por usuário).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Nome de exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>System prompt base injetado a cada turno.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Modelo de geração — id, nome ou referência <c>combo:{id}</c>.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Nível de guardrails: <c>none</c> (todas as tools habilitadas),
    /// <c>basic</c> (sem tools de execução) ou <c>strict</c> (somente
    /// tools de leitura + instrução de confirmação no system).
    /// </summary>
    public string GuardrailLevel { get; set; } = "basic";

    /// <summary>Allowlist extra de tools (JSON array); null = conjunto do guardrail.</summary>
    public string? ToolsJson { get; set; }

    /// <summary>Se a persona atende mensagens (false → 404 no dispatch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Última atualização (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Sessão lógica de uma persona por canal (RF-003): o par
/// (<see cref="PersonaId"/>, <see cref="ChatKey"/>) aponta para o chat que
/// acumula os turnos daquele canal (ex.: <c>webhook:contato-42</c>).
/// </summary>
public class AgentPersonaSession
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Persona dona da sessão.</summary>
    public string PersonaId { get; set; } = string.Empty;

    /// <summary>Chave lógica do canal (default <c>"default"</c>).</summary>
    public string ChatKey { get; set; } = "default";

    /// <summary>Chat subjacente onde as mensagens/turnos persistem.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Última atividade (epoch seconds).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Turno auditável de uma persona (RF-004): entrada, saída e status de
/// cada mensagem de canal, capado a 200 por persona. O
/// <see cref="RunId"/> é preenchido no dispatch; <see cref="OutText"/>/
/// <see cref="Status"/> ao término da run (IChatRunNotifier).
/// </summary>
public class AgentTurn
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Persona dona do turno.</summary>
    public string PersonaId { get; set; } = string.Empty;

    /// <summary>Run que produziu a resposta.</summary>
    public string? RunId { get; set; }

    /// <summary>Chave lógica do canal de origem.</summary>
    public string ChatKey { get; set; } = "default";

    /// <summary>Canal de entrada (ex.: <c>webhook</c>).</summary>
    public string Channel { get; set; } = "webhook";

    /// <summary>Texto recebido (capado a 4KB).</summary>
    public string InText { get; set; } = string.Empty;

    /// <summary>Texto respondido ou erro (capado a 4KB).</summary>
    public string? OutText { get; set; }

    /// <summary><c>running</c> | status terminal da run.</summary>
    public string Status { get; set; } = "running";

    /// <summary>Início do turno (epoch seconds).</summary>
    public long Timestamp { get; set; }
}

/// <summary>
/// Statement de policy administrativa (SPEC-20261018-policies-admin):
/// regra de hard-deny avaliada antes de qualquer permissão de usuário.
/// <see cref="Action"/> em <c>provider.use|model.use|tool.use|integration.use</c>;
/// <see cref="Resource"/> é glob sobre o identificador concreto
/// (<c>shell:rm *</c>, <c>openai/*</c>, <c>mcp:filesystem</c>, <c>skill:name</c>).
/// </summary>
public class AdminPolicyStatement
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Ordem de avaliação — último match vence.</summary>
    public int Order { get; set; }

    /// <summary>Ação: <c>provider.use</c>, <c>model.use</c>, <c>tool.use</c> ou <c>integration.use</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Glob aplicado ao recurso concreto (case-insensitive).</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>Efeito — em v1 apenas <c>deny</c> (ausência de deny = allow).</summary>
    public string Effect { get; set; } = "deny";

    /// <summary>Nota/motivo exibida quando a regra bloqueia algo.</summary>
    public string? Note { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>
/// Snapshot versionado do contexto de trabalho de um chat
/// (SPEC-20261020-context-snapshots RF-003): itens compactáveis
/// (decisões, notas, passages, tool outputs) gravados pelas tools
/// <c>builtin_context_save</c>/<c>builtin_context_compact</c> e relidos
/// por <c>builtin_context_recovery</c>. O escopo é o chat;
/// <see cref="RepoKey"/> registra o binding de repo vigente na gravação
/// (informativo — não há consulta cross-chat por repo no v1).
/// </summary>
public class ContextSnapshot
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Chat dono do snapshot.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Slug do repo (owner/repo, lowercase) do binding do chat; null sem binding.</summary>
    public string? RepoKey { get; set; }

    /// <summary>Versão monotônica por chat, começando em 1.</summary>
    public int Version { get; set; }

    /// <summary>Rótulo opcional dado pelo agente.</summary>
    public string? Label { get; set; }

    /// <summary>Itens serializados (RF-004): kind/title/content/score.</summary>
    public string PayloadJson { get; set; } = "[]";

    /// <summary>Estimativa de tokens do payload (ceil chars/4).</summary>
    public int TokenEstimate { get; set; }

    /// <summary>Se foi gravado já compactado (por tool ou pós-compactação).</summary>
    public bool Compacted { get; set; }

    /// <summary>Estratégia aplicada: <c>none</c>, <c>elision</c> ou <c>elision+summary</c>.</summary>
    public string? Strategy { get; set; }

    /// <summary>Criação (epoch seconds).</summary>
    public long CreatedAt { get; set; }

    /// <summary>Usuário dono da run que gravou.</summary>
    public string? CreatedByUserId { get; set; }
}

/// <summary>
/// Registro de auditoria de bloqueio por policy — cada deny efetivo
/// gera uma linha (quem, ação, recurso, statement que casou).
/// </summary>
public class PolicyDenial
{
    /// <summary>Identificador único (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Usuário afetado (null quando a negação não tem usuário).</summary>
    public string? UserId { get; set; }

    /// <summary>Ação negada (mesma do statement).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Recurso concreto que casou com a regra.</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>Id do <see cref="AdminPolicyStatement"/> que negou.</summary>
    public string StatementId { get; set; } = string.Empty;

    /// <summary>Quando (epoch seconds).</summary>
    public long CreatedAt { get; set; }
}
