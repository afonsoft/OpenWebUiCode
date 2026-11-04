# Análise comparativa — Open WebUI (.NET) vs Devin web app

**Data:** 2026-10-09 · **Autor:** Devin · **Epic:** E16 (Web IDE) — análise complementar
**Objetivo:** comparar a web app do Devin (`app.devin.ai`) com o nosso fork e mapear o que vale migrar/melhorar — mesmo formato de `ANALISE-IDE-WEB-OPENCODE.md`. O Devin é closed-source: a coluna "Devin" é descrita em nível de feature (UI pública + docs.devin.ai), a coluna "nosso" é evidenciada por arquivo.

## 1. O que o Devin web app tem

Superfícies principais observáveis do produto Devin:

| Área | Feature do Devin |
|---|---|
| Inbox de sessões | Lista de sessões com busca, status (rodando/aguardando/concluída), tags, billing tags, arquivar em massa |
| Timeline da sessão | Mensagens + tool calls expansíveis, diffs por passo, anexos, "structured output", playback |
| Desktop/VM | Aba "Desktop" com view ao vivo da VM do agente; preview de portas (app rodando na VM vira URL navegável) |
| Devin Review | Review automatizado de PR: findings por severidade na página do PR + status no merge box |
| PRs/CI | Sessão mostra PRs criados por ela + estado dos checks |
| Secrets | Vault de secrets por usuário/org (write-only, mascarado) consumível pelas sessões |
| Automações | Triggers (schedule, Slack, GitHub) → sessões; playbooks reutilizáveis |
| Boards | Kanban de tickets que sessões consomem |
| Uso | Dashboard de consumo (ACUs) por usuário/tag |
| Notificações | Feed in-app + push |

## 2. O que já temos (evidência)

| Área | Nosso estado | Evidência |
|---|---|---|
| Lista/busca de chats | Sidebar com busca, pastas, página de arquivados | `Sidebar.razor:83`, `Pages/Archived.razor` |
| Timeline | Mensagens + `ToolCallCard` + permission/question cards + diffs | `ChatView.razor`, `ToolCallCard.razor` |
| Terminal | PTY real + jobs + página dedicada | `TerminalPtyEndpoints.cs`, `TerminalPage.razor` |
| IDE web | `/ide`: explorer + editor + diff + terminal + jobs | `Pages/Ide.razor` (S1+S2 merged) |
| Automações | Entidade + endpoints + página `/automations` | `AutomationEndpoints.cs`, `Pages/Automations.razor` |
| Notificações | WebPush + preferências por usuário | `NotificationEndpoints.cs`, `SettingsModal` tab notifications |
| Analytics | Endpoints de analytics (admin) | `AnalyticsEndpoints.cs` |
| Compartilhamento | Links públicos de chat | `Pages/SharedChat.razor` |
| Repo/GitHub | Token, bind, clone, branch, git status/diff | `GitHubEndpoints.cs`, `WorkspaceRepoService.cs` |
| Subagente | `delegate_task` cria run filha | `DelegateTaskBuiltinTool.cs` |

## 3. Itens avaliados

| # | Item (Devin) | Status | Comentário |
|---|---|---|---|
| D1 | **Painel de PRs + status de CI no chat/repo** | ✅ **confirmado** | GitHub já integrado; falta só a superfície: listar PRs do repo vinculado com checks e link. Esforço M. |
| D2 | **Inbox "aguardando você"** | ✅ **confirmado** | Filtrar chats com run bloqueada em aprovação/pergunta (`PermissionPromptCard`/`QuestionPromptCard` pendente) — dado já existe no backend. Esforço M. |
| D3 | **Feed de notificações in-app** | ✅ **confirmado** | Temos WebPush+preferências, mas não há sino/feed in-app; endpoint existe (`NotificationEndpoints`). Esforço M. |
| D4 | **Port preview** (app do terminal vira iframe/URL) | ✅ **confirmado** | Devin expõe portas da VM. Aqui: proxy `/preview/{port}` → `localhost:{port}` do host da API quando job/terminal ativo; iframe na IDE. Esforço L, alto valor para o IDE web. |
| D5 | **Secrets vault por usuário (UI write-only)** | 🔶 candidato | Já há armazenamento de API keys por usuário (`sk-*`); vault genérico de secrets para tools/runs é novo. Útil para MCP/pipelines; esforço M. |
| D6 | **Árvore de runs filhas (delegate_task)** | 🔶 candidato | `delegate_task` existe; a timeline não mostra a run filha como entidade navegável. Esforço M. |
| D7 | **"Meu uso" — analytics por usuário** | 🔶 candidato | `AnalyticsEndpoints` é admin-facing; página/card de uso pessoal é gap pequeno. Esforço S. |
| D8 | Desktop/VM live view | ❌ rejeitado | Não há VM remota por sessão; o terminal roda no host da API. Sem equivalência arquitetural. |
| D9 | Blueprints/snapshots de ambiente | ❌ rejeitado | Conceito da infra do Devin; app self-hosted não provisiona ambientes por sessão. |
| D10 | Boards kanban | 🔶 candidato grande | Escopo de produto novo; avaliar em Epic próprio se houver pedido. |
| D11 | Billing/ACU | ❌ rejeitado | Não há modelo de cobrança por compute. |
| D12 | Devin Review automatizado | 🔶 candidato grande | Equivale a um produto de review próprio (temos QA/review por skills); reavaliar após S5–S9. |

## 4. SPECs Draft gerados (ordem sugerida)

| Spec | Título | Tamanho | Depende de |
|---|---|---|---|
| `SPEC-20261009-pr-ci-panel` | D1 — painel de PRs + checks no workspace/IDE | M | S1 |
| `SPEC-20261009-attention-inbox` | D2 — filtro "aguardando você" + badge na sidebar | M | — |
| `SPEC-20261009-notification-feed` | D3 — feed in-app (sino) sobre NotificationService | M | — |
| `SPEC-20261009-port-preview` | D4 — proxy `/preview/{port}` + iframe no IDE | L | S1/S2 |

D5–D7 ficam documentados como candidatos (sem SPEC) até priorização; D10/D12 dependem de decisão de produto.

## 5. Como encaixar na fila do E16

Ordem já aprovada: S1 ✅ → S2 ✅ → **S3+S4 ✅ (PR #248)** → S5 (em execução) → S6 → S7 → S8/S9.
Recomendação: as D-specs entram **depois de S7** (checkpoints e @mentions mudam a superfície do chat/IDE que elas consomem). D1 e D4 são as de maior valor para a proposta "IDE web"; D2/D3 melhoram o loop diário de uso.

## 6. Adendo 2026-11-04 — leitura profunda do docs.devin.ai (Customize / Skills / Memory / Insights / DeepWiki)

Segunda passada guiada pelos product guides (skills, memory, session-insights, knowledge-onboarding, deepwiki-mcp). Novos achados:

| # | Item (Devin) | Status | Comentário |
|---|---|---|---|
| D13 | **Página Customize** (escopos Pessoal/Org: Skills, Knowledge, Memory) | ✅ confirmado → SPECs | Nossos equivalentes existem espalhados (skills DB+repo, MCP admin+usuário, sem notas de conhecimento). `SPEC-20261104-customize-hub` unifica em `/customize`. |
| D14 | **Trigger `!` de skills no composer** | ✅ confirmado → SPEC | `@` arquivos+refs já entregue; `!` dedicado a skills é greenfield. `SPEC-20261104-chat-bang-trigger`. |
| D15 | **Session Insights** (tamanho XS–XL, categoria, timeline de issues, improved-prompt/action-items por LLM, knowledge usage) | ✅ confirmado → SPEC | Camada de dados completa (`RunUsageLedger`, `/usage/*`, `/analytics`); a lacuna é a UI. `SPEC-20261104-session-insights-v2`. |
| D16 | **Knowledge do Devin = deprecated** | ⚠️ notável | Upstream migra Knowledge → Skills-in-Plugins. Ainda entregamos notas estilo Devin (injeção de contexto), mas mantemos o conceito enxuto — o peso fica nas Skills. |
| D17 | **DeepWiki MCP** (`https://mcp.deepwiki.com/mcp`; tools `read_wiki_structure`/`read_wiki_contents`/`ask_question`) | ✅ adotado | `.mcp.json` adicionado na raiz; gratuito/remoto/sem auth. `afonsoft/OpenWebUiCode` precisa ser indexado no deepwiki.com (só repos públicos). |
| D18 | Devin Memory (drive git MEMORY.md + Dreaming) | 🔶 candidato | Já temos `AgentMemory` (memory_save/search, escopo global/repo) — suficientemente próximo; consolidação estilo Dreaming é possível SPEC futura. |
