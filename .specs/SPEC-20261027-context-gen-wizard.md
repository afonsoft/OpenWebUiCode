# SPEC-20261027-context-gen-wizard

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `context-gen-wizard` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261027-context-gen-wizard` |
| Status | `Draft` |
| Parent | Análise Lovable `build/portfolio-website-builder` (2026-10-27) |

## 1. User Story

**As a** usuário que quer gerar um artefato (site/portfólio) via IA
**I want** um assistente em etapas (wizard) que capture contexto estruturado — persona, projetos, estilo visual, integrações — antes do primeiro prompt livre
**So that** a geração parta de um System Prompt restritivo e rico, e não de um chat vazio — paridade com o fluxo Lovable "describe your work → watch it build → refine via chat".

### Contexto do problema

Referência: `https://lovable.dev/build/portfolio-website-builder` (inspecionada em 27/10/2026). O fluxo Lovable: landing com CTA "Describe your work" → chat gera home + páginas de projeto + contato → usuário adiciona projetos e refina por chat ou código → publica. Presets por persona (Terminal-Style dev, Grid fotógrafo, Editorial, Minimal/Bold), integrações citadas: Figma MCP, Canva, Sanity, Contentful, Google Analytics, Resend, Calendly, Tally, LinkedIn. A "mágica" não é o chat — é a **captura estruturada de contexto** que vira System Prompt.

REA/browser-CDP: inspeção ao vivo indisponível neste ambiente (sem Chrome com remote-debugging); análise baseada no HTML público da página + brief fornecido.

## 2. Scope

**In scope:**
- Wizard multi-step no client: **Identidade → Projetos → Look & Feel → Prompt livre**.
- `GenerationContext` tipado (persona, estilo, projetos, integrações, prompt livre) persistido por usuário/sessão.
- `PromptBuilderService` no backend: contexto → System Prompt restritivo (template + JSON serializado).
- Payload JSON Schema versionado enviado ao motor de IA (ver §5).
- Sessão de geração persistida (EF Core/SQLite) vinculada ao usuário, com revisão iterativa via chat existente (`ChatRunExecutor`).
- Presets por persona: `developer` (terminal/dark), `designer` (grid minimal), `photographer` (lightbox), `writer` (editorial) — catálogo extensível.
- i18n nos 8 locales; tema consistente com `themes-custom` (#513) quando entregue.

**Out of scope:**
- Editor visual de layout/WYSIWYG; geração de código-fonte em si (delegada à run/agente existente).
- Integrações reais com Sanity/Contentful/Figma (v1 = metadados declarados no contexto, sem conector).
- Deploy/hosting do artefato gerado; preview renderizado em sandbox.
- PostgreSQL — persistência segue o padrão do repo (SQLite + EF Core + migration).

## 3. Technical Context

**Where the change happens:** novo endpoint de contexto em `OpenWebUI.Api`; wizard em `OpenWebUI.Client` (página `/generate` ou modal); prompt builder em `OpenWebUI.Infrastructure`; entidades em `OpenWebUI.Domain` + `AppDbContext`.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — como Params entram na run
- `src/OpenWebUI.Api/Endpoints/ReferenceEndpoints.cs` — padrão de endpoint + auth por usuário
- `src/OpenWebUI.Domain/Entities.cs` — padrão de entidades (ver `ReferenceEntry`, `UserTheme`)
- `src/OpenWebUI.Client/Pages/Automations.razor` — padrão de página com form + lista
- `src/OpenWebUI.Client/Services/ApiService.cs` — `SendAsync<T>` + `ClientErrors.IsTransient`
- `src/OpenWebUI.Client/Components/Settings/SettingsAppearance.razor` — padrão de seletor (themes-custom)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                    # GenerationContextEntry {UserId, Json, SessionId?}
src/OpenWebUI.Api/Endpoints/GenContextEndpoints.cs  # NOVO: CRUD do contexto + POST build-prompt
src/OpenWebUI.Application/Contracts/GenContextContracts.cs  # NOVO: DTOs + JSON Schema do payload
src/OpenWebUI.Infrastructure/Services/PromptBuilderService.cs # NOVO: contexto → system prompt
src/OpenWebUI.Client/Pages/Generate.razor           # NOVO: wizard 4 passos
src/OpenWebUI.Client/Services/GenContextClient...   # NOVO: chamadas + presets builtin
src/OpenWebUI.Client/wwwroot/gen-context/presets.json       # presets por persona
tests/OpenWebUI.Api.Tests/GenContextEndpointsTests.cs       # NOVO
tests/OpenWebUI.Api.Tests/PromptBuilderServiceTests.cs      # NOVO
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs   # DbSet + config
# + migration EF Core + i18n ×8 + NavItem/sidebar
```

## 4. Requirements

### RF-001: Modelo de contexto
- **Description:** `GenerationContext { persona, style, projects[], integrations[], freePrompt, meta }` validado server-side (persona ∈ presets, máx 20 projetos, URLs https only, tokens de estilo sanitizados — sem `url(`/JS — mesmo guard do themes-custom).
- **Rules:** contexto salvo por usuário (1 ativo); `POST /api/v1/gen-context` upsert; `GET` retorna o ativo.

### RF-002: Wizard de captura
- **Description:** página `/generate` com 4 passos navegáveis (voltar preserva estado): 1) cards de persona com presets; 2) lista rápida de projetos (título, descrição, url, tags — import manual; "conectar repositório" reusa `@mention`/references quando presente); 3) pickers de estilo (theme preset + paleta + densidade); 4) textarea livre + resumo do contexto capturado.
- **Rules:** avanço exige passo válido; rascunho em localStorage para anônimo; submit → `POST /gen-context` → redireciona para chat com a run criada.

### RF-003: Prompt engine
- **Description:** `PromptBuilderService.BuildSystemPrompt(GenerationContext)` gera system prompt restritivo: papel (expert frontend architect), persona, estilo, features obrigatórias, projetos serializados, regras de output (semantic HTML/produção). Template versionado (`prompt_version` no payload).
- **Rules:** tamanho máximo do prompt (p.ex. 8k tokens); projetos truncados por prioridade; nunca vaza dados de outro usuário.

### RF-004: Orquestração e iteração
- **Description:** `POST /api/v1/gen-context/generate` cria Chat com a primeira mensagem = prompt livre + `Params["gen_context"]` = id do contexto; `ChatRunExecutor` injeta o system prompt construído no início da run. Respostas seguintes do chat = revisões iterativas sobre a mesma sessão/contexto.
- **Rules:** a run falha com erro real (não placeholder) se o provider falhar; sessão lista/reatomável pelo chat existente.

## 5. API Contract

`POST /api/v1/gen-context` — upsert do contexto:

```jsonc
// GenerationContext — JSON Schema (draft 2020-12), version="1"
{
  "type": "object",
  "required": ["persona", "style"],
  "properties": {
    "persona":  { "type": "string", "enum": ["developer","designer","photographer","writer","custom"] },
    "personaCustom": { "type": "string", "maxLength": 200 },
    "style": {
      "type": "object",
      "required": ["preset"],
      "properties": {
        "preset":   { "type": "string", "enum": ["terminal","minimal","editorial","bold","custom"] },
        "mode":     { "type": "string", "enum": ["system","light","dark"], "default": "system" },
        "palette":  { "type": "object", "additionalProperties": { "type": "string", "pattern": "^(#[0-9a-fA-F]{3,8}|rgb\\(|var\\()" } },
        "density":  { "type": "string", "enum": ["compact","comfortable","spacious"] }
      }
    },
    "projects": {
      "type": "array", "maxItems": 20,
      "items": { "type": "object", "required": ["title"],
        "properties": {
          "title":  { "type": "string", "maxLength": 120 },
          "summary":{ "type": "string", "maxLength": 500 },
          "url":    { "type": "string", "format": "uri" },
          "tags":   { "type": "array", "items": { "type": "string" }, "maxItems": 8 },
          "ref":    { "type": "string", "pattern": "^ref://" }   // liga a references
        } }
    },
    "integrations": { "type": "array", "maxItems": 12,
      "items": { "type": "string", "maxLength": 40 } },          // ex.: "figma-mcp", "ga4"
    "freePrompt": { "type": "string", "maxLength": 4000 },
    "locale": { "type": "string" }
  }
}
```

`POST /api/v1/gen-context/generate` → `{ "chatId": "...", "runId": "..." }`. Erros: `400` schema inválido; `401` sem auth; `422` contexto inexistente.

## 6. Acceptance Criteria

- **Dado** wizard completo com persona `developer` + 2 projetos + preset `terminal`, **quando** submit, **então** cria-se chat cuja primeira run contém system prompt com persona/estilo/projetos serializados (`prompt_version` presente).
- **Dado** JSON com `persona` fora do enum, **quando** POST, **então** 400 com detalhe do campo inválido.
- **Dado** estilo com valor `url(javascript:...)` em palette, **quando** POST, **então** 400 (sanitização).
- **Dado** sessão gerada, **quando** usuário envia "mude a cor de fundo para azul escuro" no chat, **então** a run continua na mesma sessão com contexto intacto.
- Wizard navegável com teclado; i18n paridade nos 8 locales; guards (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`) verdes.

## 7. Task Plan

1. `GenerationContextEntry` + `AppDbContext` + migration `AddGenerationContexts` — validar `dotnet ef migrations add` + EnsureCreated em teste.
2. DTOs + validação (schema acima) em `GenContextContracts.cs` — testes de contrato.
3. `PromptBuilderService` — testes unitários: persona→preset, truncamento, sanitização.
4. `GenContextEndpoints` (GET/POST/generate) — testes HTTP cobrindo auth, 400/422 e idempotência de upsert.
5. Página `/generate` wizard + `presets.json` + client service — bUnit: navegação entre passos, validação, submit.
6. Integração `ChatRunExecutor`: `Params["gen_context"]` → injeta system prompt — teste ponta-a-ponta.
7. i18n ×8 + guards + cobertura ≥ baseline ratchet.

## 8. Organization Guardrails

- Branch `feature/Devin-20261027-context-gen-wizard`; PR para `main` (squash), `Closes` na issue de tracking.
- Nunca commitar secrets/db; `.github/workflows/` intocado; mudança de schema exige migration (item 1).
- Reusar `references` (ref://) para projetos ligados a fontes externas quando o usuário as tiver — não duplicar ingestão.
- Run falha → erro real ao cliente (Hard Rule), nunca resposta enlatada persistida.

## 9. Definition of Done

- [ ] Migration aplicada e `AppDbContext` consistente
- [ ] `POST /gen-context` + `/generate` com testes verdes (auth, schema, erro)
- [ ] Wizard acessível (labels, teclado) e i18n paridade nos 8 locales
- [ ] System prompt contém persona/style/projects e `prompt_version`
- [ ] Iteração via chat preserva contexto da sessão
- [ ] Coverage ratchet não regride; `check-spec-status.py` OK
- [ ] SPEC arquivada em `docs/specs/` com Status `Completed` + issue/épico atualizados
