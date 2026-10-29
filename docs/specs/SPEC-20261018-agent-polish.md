# SPEC-20261018-agent-polish

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-polish` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261025-agent-polish` |
| Status | `Completed` |
| Parent | `docs/analise-opencode-v2.md` (P2.9/2.10 + P3.1/3.2/3.3/3.4/3.6/3.9) |

## 1. User Story

**As a** usuário avançado
**I want** os itens menores do v2: warming de prompt-cache, catálogos HTTP de skills com autoinvoke, MCP resources/prompts, compaction manual+nativa, markdown-source toggle e tps ao vivo
**So that** fechamos o delta de paridade sem specs dedicadas — cada item é pequeno e independente.

## 2. Scope

**In scope (itens independentes — cada RF pode ser feito em commit próprio):**
- **Warming (P3.1):** `warming:true` por model → request vazio keep-alive a cada `warmingInterval` (default 5min) pra manter prompt-cache quente; opt-in, off por default, desligado quando OmniRoute detecta cache próprio.
- **Skills HTTP catalogs + autoinvoke (P3.2/3.3):** skill root `https://` → fetch `index.json`/`SKILL.md` remoto cacheado 24h; frontmatter `autoinvoke: true` injeta a skill no contexto sem precisar ser chamada.
- **MCP resources/prompts (P2.10):** `McpClientService` ganha `resources/list`+`resources/read` (expõe como tool `mcp_{server}_read_resource`) e `prompts/list`+`prompts/get` (viram slash commands `/{server}:{prompt}`).
- **Compaction manual + native (P2.9):** comando `/compact` força compactação; `compaction.native:true` usa endpoint provider quando suportado (fallback pro nosso summarizer — sob OmniRoute quase sempre fallback, documentar).
- **Composer/UX (P3.4/3.6):** toggle "markdown source" (raw markdown vs rendered) nas mensagens; `tps` ao vivo no header da run (tokens/s rolling).

**Out of scope:**
- Provider overlays e `anthropic cacheFormat` — em SPEC-model-variants. Paste→attachment — em SPEC-attachments-v2. TUI, keybinds, `opencode pair`, npm plugins, MDM — não se aplicam (ver análise §não-portar).

## 3. Technical Context

**Where the change happens:**
- Warming: `LlmClient` background keep-alive por (provider,model) marcado.
- Skills: `SkillDiscoveryService` (remote roots) + `AgentDefinitionService`/pipeline (autoinvoke injeta SKILL.md no system).
- MCP: `McpClientService` novos métodos + `BuiltinToolRegistry`/`RepoCommandService` expõem.
- Compaction: `ChatRunExecutor`/`ContextCompactionService` flag + `/compact` command.
- UI: `ChatMessage.razor` toggle, `RunHeader`/`RunInspector` tps live.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs`
- `src/OpenWebUI.Infrastructure/Services/McpClientService.cs`
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` + `ChatRunExecutor.cs`
- `src/OpenWebUI.Infrastructure/Services/RepoCommandService.cs`
- `src/OpenWebUI.Client/Components/Chat/*.razor`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Llm/ModelWarmer.cs              # NOVO: keep-alive loop
src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs # remote catalog fetch + autoinvoke
src/OpenWebUI.Infrastructure/Services/McpClientService.cs       # resources/prompts APIs
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs# native flag + /compact path
src/OpenWebUI.Infrastructure/Services/RepoCommandService.cs     # /compact + mcp prompts
src/OpenWebUI.Client/Components/Chat/ChatMessage.razor          # markdown toggle
src/OpenWebUI.Client/Components/Chat/RunInspector.razor         # tps live
tests/OpenWebUI.Api.Tests/McpResourcesTests.cs                  # NOVO
tests/OpenWebUI.Api.Tests/SkillCatalogTests.cs                  # NOVO
```

## 4. Requirements

### RF-001: Warming opt-in
- `warming:true` → timer por (provider,model) dispara request mínimo (`max_tokens:1`, prompt `ping`) a cada intervalo; para quando não usado >3 intervalos; nunca em conta de custo sem flag `warming_billable:true`.

### RF-002: Skill HTTP catalog + autoinvoke
- root `https://host/path` → GET `{root}/index.json` (lista) + `{root}/{name}/SKILL.md`, cache 24h com ETag; `autoinvoke:true` injeta corpo no system como `## Skill: {name}` antes das regras.

### RF-003: MCP resources/prompts
- `resources/list|read` + `prompts/list|get` implementados no client; read_resource expõe como tool read-only; prompts aparecem no menu `/` como `{server}:{prompt}` e resolvem para o texto retornado.

### RF-004: Compaction manual/native
- `/compact` força `MaybeCompact` imediatamente; `native:true` tenta endpoint `/compact`/`response.compact` do provider quando `capabilities.compaction:native`, caindo pro summarizer com warning.

### RF-005: UX
- toggle fonte/rendered por mensagem (persiste por chat); `tps` atualiza 1/s durante stream no header da run.

## 5. Acceptance Criteria

- Warming dispara N requests em janela simulada e para após ociosidade.
- Skill remota `https://` descoberta, cacheada e autoinvoked injeta texto no system.
- MCP com resources/prompts mock: `read_resource` retorna conteúdo; `/{server}:{prompt}` resolve texto.
- `/compact` compacta sob demanda; native cai no fallback com warning quando não suportado.
- Testes por item; nenhum comportamento default muda sem opt-in.

## 6. Implementation Notes (2026-10-25)

- **RF-001** — mapa kv `warming.models` (`{modelId: {intervalMinutes, billable}}`) em vez de flag por modelo; ping `max_tokens:1`/`"ping"`; ociosidade = última `ChatRun` do modelo > 3× intervalo; providers não-ollama exigem `billable:true`; OmniRoute detectado por base URL contendo "omniroute".
- **RF-002** — `RemoteSkillCatalog` (singleton) lê raízes do kv `skills.remoteRoots`; refresh é disparado pelos entry-points async (contributor). Escopo `remote`, local/global vence duplicata. `AutoinvokeSkillsContributor` order 72 injeta `## Skill: {name}` respeitando `u:{id}:skills-disabled` + deny admin.
- **RF-003** — `read_resource` materializado só quando o server anuncia resources; prompts cacheados 60s junto às tools; slash `{server}:{prompt}` resolve server-side no `ExpandAsync` (sem repo) e client-side via `POST .../prompts/resolve`.
- **RF-004** — `compaction:native` kv + capability `compaction` no modelo (override `models:capability-overrides` ou reportada); native via `INativeContextCompactor` com payload `context_management` (providers OpenAI-compatíveis); fallback sempre documentado em `CompactResult.Warning`.
- **RF-005** — `viewSource` vira preferência por chat (`chat:viewSource:{id}`) aplicada a todas as mensagens; `tps` = chars/4 numa janela rolante de 4s, exibido só após ≥1s de stream.
- **Desvios**: endpoints de prompts ficam sob `/api/v1/users/user/mcp-servers/prompts*` (escopo usuário, mesma família do catálogo efetivo); `compact` expõe `ChatCompactResponse{compacted,native,warning}`.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/516 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/507
