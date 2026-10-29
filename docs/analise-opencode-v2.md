# Análise: OpenCode v2 Docs → melhorias aplicáveis ao afonsoft/OpenWebUiCode

Data: 2026-10-18 · Fonte: https://opencode.ai/v2/docs/ (todas as 30 páginas lidas) · Alvo: `afonsoft/OpenWebUiCode` (.NET 10 + Blazor WASM)
Complementa `docs/analise-fork-alltomatos-opencode.md` (análise do fork alltomatos, v1.21). Aqui o foco é a **documentação oficial do OpenCode v2** — que descreve um produto diferente do nosso (agente de coding CLI/desktop vs. nossa web UI multiusuário), então a régua é "o que faz sentido portar para um app web hospedado".

---

## 1. Mapa da documentação v2 (resumo por página)

| Doc | O que o opencode v2 oferece |
|---|---|
| Intro/CLI | Agente em TUI, desktop, web (`opencode pair`), Docker; serviço de fundo compartilhado que detém sessões/config/permissões; `opencode run` headless para CI. |
| Agents | Agentes declarativos em Markdown (`*.md` com frontmatter) ou JSONC: `system`, `model` (com `#variant`), `mode` primary/subagent/all, `permissions`, `steps`, `hidden`, `color`, `disabled`. Builtins: build/plan/general/explore + hidden compaction/title/summary. Merge por precedência de config. |
| API | REST API completa do servidor (sessions, prompts, eventos) — os próprios clientes usam. |
| Attachments | `file://`/`data:` URIs; texto UTF-8 → texto no prompt; diretório → listing; PNG/JPEG/GIF/WebP → mídia; limite 20 MiB; `media.image` com auto-resize (2000×2000, 5 MB base64). PDF/áudio/vídeo não vão ao modelo. |
| Build | Extender via plugins, rodar como servidor, embedar. |
| CLI config | `cli.json` global: tema, animações, scroll, prompt (editor context, paste compact, image preview), sessão (thinking show/hide, tps, markdown source/rendered, grouping, permissions prompt/autoaccept), tabs, diffs (source, wrap, split/unified, tree), notificações/sons, keybinds, mini mode. |
| CLI providers | `/connect` + `opencode auth`: API key, OAuth (browser/device code), command, env. Multi-conta por provider; storage seguro fora do config. |
| Commands | Slash commands em `.md` (`~/.config/opencode/commands`, `.opencode/commands`) ou JSONC: `template`, `description`, `agent`, `model`, `subagent`. `$ARGUMENTS`, `$1..$N` posicionais, fallback append, `` !`shell` `` inline, reload automático, background child session com report ao pai. |
| Compaction | Summarização LLM automática perto do limite de contexto (`auto`, `keep.tokens` ~15k, `buffer` ~10%); manual via API; sumário estruturado (objetivo/decisões/próximos passos); opção `native` (checkpoint criptografado do provider OpenAI Responses) ou `summary`; retry único em overflow. |
| Config | `opencode.json(c)` hierárquico: managed/MDM > system (`/etc/opencode`) > global (`~/.config`) > project (`opencode.json` + `.opencode/`); `$schema` para validação; chaves: model, default_agent, permissions, policies, agents, snapshots, watcher, formatter, media, tool_output, websearch, mcp, compaction, warming, skills, commands, shell, update. |
| Console | Serviço de equipe: workspaces, billing, budgets, policies de org entregues aos clientes (~1 min), Go/Go Plus subscription. |
| Formatters | Pós-write/edit/patch: catálogo builtin por extensão (prettier, biome, ruff, gofmt, shfmt, csharpier-like…), custom por `command`+`extensions`, matching por extensão final, fallback ordenado, desligado por default. |
| Instructions | `AGENTS.md` global + todos do workspace até a raiz do projeto; descoberta incremental de arquivos aninhados ao ler diretórios; updates a quente na próxima chamada; array `instructions` aceita paths/globs/URLs. |
| MCP servers | `mcp.servers` local (stdio) e remote (Streamable HTTP): env `{env:VAR}`, headers, OAuth completo (CIMD → DCR → client pré-registrado, PKCE, refresh, `opencode mcp auth/logout`), timeouts startup/catalog/execution, `codemode` flag, `disabled`. |
| Migrate V1 | Mapeamento de campos legados → v2; confirma que v2 lê mesmos arquivos e normaliza em memória. |
| Models | `/models` por sessão; default em config; **variants** `model#variant` (overlays de settings/headers/body — ex.: reasoning effort low/high/max); `capabilities.reasoning` gera variants; aliases `modelID`; `compatibility.reasoningField`/`cacheFormat` (Anthropic breakpoints via proxy); discovery de Ollama/LMStudio/vLLM; `disabled`; cost/limit por modelo. |
| Network | `HTTP(S)_PROXY`/`NO_PROXY`, credenciais na URL, `NODE_EXTRA_CA_CERTS`, env persistido do serviço. |
| Permissions | Lista ordenada `{action, resource, effect: allow/ask/deny}`, wildcard `*`/`?`, última match vence; ações: read/edit/glob/grep/shell/subagent/skill/question/webfetch/websearch/external_directory/MCP; multi-resource (qualquer deny → deny); scanner de shell infere diretórios; defaults por agente; aprovações `once`/`always` (persistem por projeto, pattern proposto pela tool)/`reject`; nunca sobrescrevem `deny`. |
| Plugins | `plugins` array: npm, git, local, com `-id` pra desabilitar e `*`/`.*` wildcards; `.opencode/plugins/` discovery; CLI `plugin add/list/check/update/remove`; plugins não podem desligar `opencode.config.policy`/`opencode.provider.opencode`. |
| Policies | Statements binários `{action: provider.use|tool.use|integration.use, resource, effect}` — nunca pedem, só restringem; ordem invertida de precedência (admin/Console vence projeto); `tool.use` hard-deny sobrepõe `ask` e "allow always"; `integration.use` liga/desliga MCP/skill/plugin. |
| Providers | Catálogo models.dev + custom `providers.<id>`: package runtime, `env`, `settings.baseURL`, `headers`, `body`, `models` (modelID, capabilities, cost, limit, variants, compatibility), timeouts header/chunk, `encoding` zstd/gzip, Azure resourceName/Entra. |
| References | Fontes nomeadas externas ao projeto: `path` local ou `repository` git (+branch); clone/cache global com refresh ≥24h; `description` vira guidance no prompt; `hidden` esconde do seletor; attach = listing não-recursivo; sem permissões extras — leitura segue `external_directory`. |
| Skills | `SKILL.md` (dir) ou `*.md` flat em `.opencode/skills`, `.claude/skills`, `.agents/skills`, global; array `skills` com dirs e **catálogos HTTP** (`index.json` + version); ID = path; `description` controla descoberta; `metadata.opencode/autoinvoke: false` esconde do modelo; tool `skill` carrega body + sample de 10 arquivos; permissão por skill ID. |
| Snapshots | `/undo` `/redo`: snapshot de FS antes/depois de cada model step (git object DB separado, sem commits); rollback restaura arquivos atribuídos + devolve o prompt ao composer; escopo = worktree do projeto; ignorados/>2MiB fora. |
| Themes | Tema por `cli.json`: builtins (~30), custom JSON com tokens (`text`/`background`/`border`/`diff`/`syntax`/`markdown`), modos light/dark/system, referências `$hue.*`, por projeto em `.opencode/themes/`. |
| Tools | Catálogo: read/glob/grep/edit/write/patch, shell (fg/bg, scanner de comandos, output overflow→arquivo), webfetch/websearch, question, skill, subagent (fg/bg, sessionID p/ continuar), execute (Code Mode JS), session_rename/move, worktree_*, browser (tabs, snapshot, console, network, perf, heap, Lighthouse). |
| Troubleshooting | `service status/restart`, CORS, `api` CLI, logs com run/role, SIGPROF/SIGUSR1 profiles. |
| Warming | Requests periódicos transientes p/ manter prompt-cache do provider: `interval` 4 min, `duration` 30 min, prompt configurável; custo real de tokens. |
| Websearch | Providers Exa/Firecrawl/Parallel/Tavily/TinyFish, seleção fixa ou `random` com failover em 429 (cooldown Retry-After/60s compartilhado por workspace), permissão por query, `websearch: false` remove a tool. |

---

## 2. Onde já temos paridade (não agir)

- **Sessões duráveis / steer**: runs server-side com replay, heartbeat, pause/resume, steer (SPEC ChatRunSteer) ≈ session_input durável + wake do v2.
- **Subagentes**: `delegate_task` com `wait=false` (background), agent por nome, binding de outro repo, auto-merge de worktree, profundidade máxima ≈ tool `subagent` do v2 (o nosso ainda delega a CLIs externos — à frente do v2).
- **Skills**: descoberta `.claude/.agents/.devin/.opencode` + global `~/.agents` + tool `skill` ≈ v2 (faltam catálogos HTTP e `autoinvoke`).
- **Commands**: `$ARGUMENTS`/`$N`, `` !`shell` `` opt-in, `agent`/`model`/`subagent`, workspace prompts ≈ v2 commands.
- **Ask user**: `ask_user` multi-pergunta ≈ `question`.
- **Snapshots/undo**: `checkpoint`/`rewind` + worktree_diff/merge ≈ `/undo`/`/redo` do v2.
- **Compaction**: `ContextCompactionService` (sumário LLM estruturado) + `ContextPruner` (elide mecânica) ≈ compaction `summary` do v2.
- **Instructions**: AGENTS.md + CLAUDE.md carregados (v2 abandonou CLAUDE.md; o nosso está mais amplo).
- **Websearch**: `web_search` com failover `auto` ≈ seleção `random` + cooldown do v2.
- **Browser**: Playwright por chat com self-healing/OCR ≈ namespace `browser` (v2 tem Lighthouse/heap — temos screenshot/navigate/act/observe).
- **Aprovação de tools**: gate LOW/MED/HIGH + "lembrar" por (chat, tool, arg-pattern glob) ≈ `once`/`always` — mas o nosso é só em memória e por chat.
- **Format hook**: comando formatador pós-write por binding ≈ formatter do v2, porém com catálogo menor.
- **Auth/identidade**: JWT + API keys + OAuth OIDC/SAML/LDAP/SCIM — muito além do v2.
- **Console-like admin**: grupos/RBAC, analytics, retenção — parcial (falta a parte de policies, ver abaixo).

---

## 3. Gaps e melhorias propostas (priorizadas)

### P1 — maior valor / esforço razoável

| # | Melhoria | Gap atual | Proposta |
|---|---|---|---|
| 1 | **Policies admin (hard-deny)** | RBAC cobre seções (`workspace.*`), mas não há como admin proibir um provider/modelo, um MCP server, um padrão de shell (`git push *`, `sudo *`) ou uma skill de forma **inalcançável** pelo usuário. | Novo `PolicyService`: statements `{action: provider.use\|tool.use\|integration.use, resource(glob), effect: deny}` em nível admin (Settings → Admin → Policies), avaliados **depois** das regras do usuário (última palavra do admin). Mensagem de bloqueio nomeia a política. Reaproveita `GlobToRegex` e o pipeline de approvals. |
| 2 | **Permissões declarativas + aprovação durável** | Aprovações "sempre" vivem só em memória por chat (restart perde); não há regras ordenadas `allow/ask/deny` por usuário/projeto; sem boundary `external_directory` (shell herda acesso do host). | (a) Persistir approvals por (userId, workdir, tool, argPattern) — vira "Allow always" de verdade, revogável em Settings; (b) array `permissions[]` ordenado em Settings (usuário) com ações read/edit/shell/subagent/skill/webfetch/websearch/external_directory; (c) flag `external_directory: ask` — hoje file_* já jails ao workdir; auditar shell/terminal que não têm boundary. |
| 3 | **MCP OAuth remoto** | `McpClientService` conecta por headers/env; servidores com OAuth (Sentry, Notion, etc.) não conectam. | Fluxo PKCE com dynamic client registration + pre-registered client (`client_id`/`client_secret`/`scope`/`redirect_uri`); tokens criptografados no DB por usuário; botão "Autenticar" na tab MCPs + refresh automático. |
| 4 | **Model variants (esforço de reasoning)** | Tokens de reasoning são exibidos, mas não há presets `model#variant` (`low`/`high`/`max`) nem overlay de settings por variante. | `variants[]` por modelo custom (settings + headers + body merge), `capabilities.reasoning.effort[]` gera variants automaticamente, seletor `#variant` no composer ao lado do modelo. Default configurável por persona. |
| 5 | **Anexos: texto e diretório vão ao modelo** | Só `image/*` entra na request (ChatPipeline RF-006); arquivo de texto/código anexado não chega como contexto direto. | No pipeline: anexo `text/*`/`*.md`/`*.json`/código → decodifica e injeta como conteúdo nomeado; diretório (do workspace) → listing; PDF → extrair texto (ou manter no RAG, mas o opencode não manda PDF mesmo). Manter limite ~20 MiB + auto-resize de imagem (`media.image` equivalente — verificar se já existe; se não, adicionar max_width/base64_bytes). |

### P2 — bom ganho, escopo médio

| # | Melhoria | Gap | Proposta |
|---|---|---|---|
| 6 | **References** (fontes nomeadas externas) | Workdir binding é único por chat; sem anexo de outro diretório/repo git nomeado. | `references` por usuário/projeto: `{alias: {path|repository+branch, description, hidden}}`; clone em cache com refresh ≥24h; attach no composer injeta listing + vira guidance quando tem `description`; leitura respeita as permissões de diretório. |
| 7 | **Catálogo de formatters por extensão** | `FormatHookService` roda UM comando por binding/global; sem matching por extensão nem fallback. | Mapa extensão→comando com builtins detectados (`dotnet format`/`csharpier` .cs, prettier/biome .ts, ruff .py, shfmt .sh…), custom por usuário, ordem com fallback ao próximo que existir; falha nunca quebra a tool (já é o comportamento atual). |
| 8 | **Compaction manual + config** | Auto existe; verificar gatilho manual e budget visível. | Botão "Compactar agora" na run/chat + endpoint; settings `keep.tokens`/`buffer` por usuário; opcional `compaction.type: native` p/ OpenAI Responses (checkpoint criptografado) onde suportado. |
| 9 | **Temas customizados** | Só dark/light/system. | Presets de tema (tokens CSS vars) + upload JSON de tema por usuário; escopo global/admin. Encaixa no Tailwind (mapear tokens→CSS custom properties). |
| 10 | **MCP resources/prompts** | Só tools são listados/chamados. | Listar `resources`/`prompts` do servidor na tab MCPs; `read_mcp_resource`/`get_mcp_prompt` como tools ou inserção de prompt via `/`. |

### P3 — polish / nicho

| # | Melhoria | Nota |
|---|---|---|
| 11 | **Warming** (`warming.interval/duration`) | Requests keep-alive transientes p/ preservar prompt-cache do provider; custo real de tokens — opt-in por usuário/conexão. Barato de fazer, valor depende do provider (Anthropic cache 5 min). |
| 12 | **Catálogo HTTP de skills** | `skills[]` aceitar URL com `index.json` versionado — permite catálogo central `afonsoft/skills` servido sem git clone. |
| 13 | **`metadata.opencode/autoinvoke` / `disable-model-invocation`** | Frontmatter que esconde a skill do catálogo do modelo mas permite invocação explícita — útil p/ skills "on-demand". |
| 14 | **Markdown source/rendered toggle** | Cli-config `session.markdown`; toggle por mensagem para ver fonte markdown. |
| 15 | **Paste grande → attachment chip** (`prompt.paste: compact`) | Colar >N KB vira anexo em vez de poluir o composer; preview de imagem no composer. |
| 16 | **tps no footer da resposta** | Tokens/s durante streaming (cli `session.tps`) — já temos usage; falta rate ao vivo. |
| 17 | **Provider request overlays** | `headers`/`body`/`settings` por provider e por modelo (ex.: `store:false`, `service_tier:flex`, `reasoningEffort`) — hoje params_override de filter cobre parte; overlay declarativo completo dá paridade com `providers.*.body`. |
| 18 | **Prompt caching awareness** | `compatibility.cacheFormat: anthropic` — inserir `cache_control` em system/última msg quando modelo Claude passa por gateway OpenAI-compat (direto relevante pro OmniRoute). |

### Não portar (não se aplicam ao nosso produto)

- TUI/mini/keybinds, `opencode pair` (somos web nativo), `opencode serve` (já somos o servidor), plugins npm/TS (nossa extensibilidade já cobre via MCP + functions/pipelines + n8n; plugin .NET real seria projeto à parte), managed plist/MDM (desktop-only), uninstall/service commands, themes de terminal ANSI.

---

## 4. Sugestão de ordem de execução (SPECs)

1. **SPEC policies-admin** — motor de statements + UI admin + auditoria. *(fundação de segurança; destrava P2 de permissões)*
2. **SPEC permissions-v2** — array ordenado allow/ask/deny por usuário + approvals duráveis por workdir + `external_directory` ask.
3. **SPEC mcp-oauth** — PKCE/DCR + storage de token + UI de auth na tab MCPs.
4. **SPEC model-variants** — variants/effort presets + `capabilities.reasoning` + overlays headers/body (inclui P3.17/3.18).
5. **SPEC attachments-v2** — texto/diretório no prompt + auto-resize de imagem + limites configuráveis.
6. **SPEC references** — fontes nomeadas (path/git) anexáveis com refresh.
7. **SPEC formatters-catalog** — matching por extensão + builtins + fallback.
8. **SPEC themes-custom** — presets + JSON por usuário.
9. **SPEC mcp-resources-prompts**, **SPEC warming**, **SPEC skill-catalogs** — agrupar conforme capacidade.

Dependência: 1→2 (policies avaliam depois das permissions); 4 pode abrir em paralelo; 5 é independente e de alto retorno de UX.

---

## 5. Riscos / decisões a validar contigo

- **Policies vs RBAC atual**: vale manter os dois (RBAC = seções da UI; policies = veto sobre ações de agente) em vez de fundir — fusão complica o modelo. Recomendo coexistência, com policies só admin.
- **`external_directory`**: nosso shell roda no host do servidor — expor leitura/escrita fora do workdir a usuário comum é mais sensível que no CLI local do opencode. Proposta: default `ask` para usuários, `deny` configurável por admin via policy.
- **Compaction nativa**: só vale se usarmos OpenAI Responses; pelo OmniRoute provavelmente cai em `summary` — prioridade baixa.
- **Warming**: gasta tokens; ligar só por conexão/provider que cobre cache (Anthropic), default off.
