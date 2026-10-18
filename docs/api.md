# API

Base: `/api/v1` — Minimal APIs em `src/OpenWebUI.Api/Endpoints/`. Auth: `Authorization: Bearer <jwt>` ou `Bearer sk-*`.

| Grupo | Escopo |
|---|---|
| `AuthEndpoints` | registro, login, perfil, logout |
| `UserEndpoints` | perfil, senha, chaves `sk-*`, admin de usuários |
| `GroupEndpoints` | grupos e RBAC |
| `ChatEndpoints` | CRUD de chats/mensagens, completions (SSE), share, fixar, pastas, arquivos |
| `ChatRunEndpoints` | runs desacopladas: steer/queue, stream replay (`lastSeq`), diff, pause/resume |
| `ChatJobEndpoints` | jobs em background de runs (`shell_exec` & cia.) |
| `CheckpointEndpoints` | checkpoints/revert do workspace |
| `WorkspaceEndpoints` | folders, prompts, memories, notes, models custom |
| `WorkspaceFileEndpoints` | arquivos do workspace |
| `WorkspaceTestRunEndpoints` | execução de testes da worktree (aba Testes) |
| `ModelEndpoints` | listagem e modelos custom |
| `ComboEndpoints` | combos de modelos com failover (strategies, RPM/TPM, cooldown) |
| `AgentEndpoints` | catálogo de agentes do workspace (`*.agent.md` + builtin) |
| `AgentPersonaEndpoints` | personas de agente (prompt, modelo, guardrails, audit, dispatch) |
| `ExternalAgentEndpoints` | detecção de CLIs de agente locais (Claude Code, OpenCode, …) |
| `RepoSkillEndpoints` | skills/commands do repo vinculado + escopo `~/.agents` |
| `ToolEndpoints` | registro/invocação de tools builtin |
| `McpEndpoints` / `UserMcpEndpoints` | servidores MCP (admin + por usuário) |
| `SecretEndpoints` | secrets de agente (global admin + override por usuário) |
| `AutomationEndpoints` | automações agendadas (prompt/skill/mcp/reminder/webhook) |
| `AutomationHookEndpoints` | webhooks inbound vinculados a chats |
| `CalendarEndpoints` | calendário mensal de execuções |
| `NotificationEndpoints` | notificações in-app + Web Push (VAPID) |
| `ChannelEndpoints` | canais/DMs com realtime |
| `KnowledgeEndpoints` / `RetrievalEndpoints` | coleções, embeddings, retrieval (RAG) |
| `EvaluationEndpoints` | feedback 👍/👎 de mensagens |
| `TaskEndpoints` | título, follow-ups, tags via LLM |
| `AnalyticsEndpoints` | tokens/custos/runs, leaderboard (admin) |
| `IdeEndpoints` | backend da Web IDE (`/ide`) |
| `LspEndpoints` | status, sync de doc, diagnósticos, hover |
| `TerminalEndpoints` / `TerminalPtyEndpoints` | config + sessões PTY (WS) |
| `BrowserToolEndpoints` | config/sessão do browser do agente (Playwright) |
| `N8nEndpoints` | integração n8n (list/trigger workflows) |
| `ImageEndpoints` / `VideoEndpoints` | geração de imagem/vídeo |
| `AudioEndpoints` | STT/TTS server-side |
| `OAuthEndpoints` / `SamlEndpoints` / `ScimEndpoints` | SSO corporativo + provisioning |
| `GitHubEndpoints` | integração GitHub (repos, PRs, token) |
| `PluginEndpoints` | plugins/functions externas |
| `PreviewEndpoints` | preview de apps/artefatos |
| `MaintenanceEndpoints` | retenção de chats/memórias (dry-run + run-now) |
| `PassthroughEndpoints` | proxy Ollama/OpenAI (`/ollama/...`, `/openai/...`) |
| `ConfigEndpoints` | `/api/config` feature flags, conexões diretas |
| `ApiEndpoints` | `/api/config`, `/health`, conexões de providers |
| `UtilsEndpoints` | utilitários diversos |

## Convenções

- Erros de provider → status de erro (`404`/`502`), nunca payload enlatado.
- Seed de conexões por env na primeira inicialização.
- `DatabaseMigrator` aplica EF Core Migrations no startup; bases legadas (sem `__EFMigrationsHistory`) são baselinadas sem recriar tabelas.
