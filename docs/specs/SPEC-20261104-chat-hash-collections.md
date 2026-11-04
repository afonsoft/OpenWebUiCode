# SPEC-20261104-chat-hash-collections

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-hash-collections` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-chat-hash-collections` |
| Ticket | `GH#696` — https://github.com/afonsoft/OpenWebUiCode/issues/696 |
| Status | `Done` |

## 1. User Story

**As a** usuário do chat
**I want** digitar `#` no composer e ver um dropdown com minhas coleções RAG e arquivos de knowledge
**So that** eu referencie knowledge pelo nome exato sem decorar os ids — paridade Devin/Open WebUI.

**Problem context:**
`#` já resolve **server-side**: `ChatPipeline.ResolveReferenceFileIdsAsync` (ChatPipeline.cs:366) mapeia `#nome` → todos os arquivos da coleção `KnowledgeCollection` ou arquivo individual. O gap é **só client-side**: o usuário digita o nome de memória, sem autocomplete. `@` lista arquivos do repo e `!` lista skills — `#` é o terceiro trigger, mesmo padrão.

## 2. Scope

**In scope:**
- Dropdown no composer ao digitar `#` + query, listando coleções (badge `collection`) e arquivos de knowledge (badge `file`) do usuário.
- Regex `(?:^|\s)#(?<q>[\w.\-]*)$` — mesmo padrão de `!`/`@`; debounce ~250ms; substring filter; top-20.
- Teclado: ↑↓ navega, Enter/Tab seleciona, Esc fecha; ARIA listbox/option; click-outside fecha.
- Seleção insere `#Nome-Exato` no composer (server-side já resolve — zero mudança backend).
- i18n `chat.hash_*` nos 8 locales; bUnit em `ChatHashTests.cs`.

**Out of scope:**
- Mudanças server-side na resolução `#` (já existe e funciona).
- Upload/criação de coleções pelo composer.
- Coleções de outros usuários/admin — `#` só resolve as do próprio usuário (comportamento server existente).

## 3. Technical Context

**Where the change happens:**
`ChatView.razor` — espelha o trio `_bang*` (ChatView.razor:590-601 dropdown, 945-948 estado, 1969-1988 teclado, 2195-2280 lookup/seleção). Novo trio `_hash*` idêntico em estrutura.

**Files to read before implementing:**
- `src/OpenWebUI.Client/Components/ChatView.razor` (mention `@` + bang `!`)
- `src/OpenWebUI.Client/Services/ApiService.cs` (`GetKnowledgeAsync` → `List<KnowledgeResponse>`; endpoint de arquivos da coleção)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs:366` (`ResolveReferenceFileIdsAsync` — formato do nome que casa: `KnowledgeCollection.Name`)
- `tests/OpenWebUI.Client.Tests/ChatBangTests.cs` (harness bUnit)

**Files to create or modify:**
```text
src/OpenWebUI.Client/Components/ChatView.razor   (dropdown + estado + handlers _hash*)
src/OpenWebUI.Client/Services/ApiService.cs      (se faltar método p/ listar arquivos das coleções)
src/OpenWebUI.Client/wwwroot/i18n/*.json         (chat.hash_* ×8)
tests/OpenWebUI.Client.Tests/ChatHashTests.cs    (novo)
```

## 4. Requirements

### RF-001: Trigger `#` com dropdown
- **Description:** `(?:^|\s)#(?<q>[\w.\-]*)$` no fim do input → dropdown com coleções+arquivos filtrados; `oi#` não dispara; `#` sozinho lista top-20.
- **Input → Output:** input `#proj` → itens `collection`/`file` com nome contendo "proj" (case-insensitive).

### RF-002: Conteúdo do dropdown
- **Description:** coleções da `GET /api/v1/knowledge` (badge `collection`, ordenadas primeiro) + arquivos das coleções (badge `file`). Filtro por nome E (coleção) descrição; ordenação: collections → files, depois nome.
- **Rules:** cache do resultado por sessão de chat (mesmo padrão `_bangSkills`); falha de fetch → lista vazia silenciosa + log console.

### RF-003: Seleção insere token
- **Description:** selecionar item remove o token `#query` do input e insere `#<nome>` — coleção usa `Name`, arquivo usa o nome que `ResolveReferenceFileIdsAsync` casa.
- **Rules:** espaço após o token se não houver; cursor no fim.

### RF-004: Teclado + a11y
- **Description:** ↑↓/Enter/Tab/Esc; `role="listbox"`/`role="option"` + `aria-selected`; fecha ao clicar fora ou enviar a mensagem. Não interfere em `!`/`@`/`/` existentes.

### RF-005: i18n + testes
- **Description:** `chat.hash_knowledge`, `chat.hash_collection`, `chat.hash_file`, `chat.hash_empty` ×8; ≥3 bUnit cobrindo trigger, filtro, seleção→token, não-disparo em `oi#`.

## 5. API Contract

Reusa `GET /api/v1/knowledge` (coleções) e o endpoint existente de arquivos por coleção (`GET /api/v1/knowledge/{id}` ou equivalente — confirmar shape em `ApiService.cs`). **Nenhum endpoint novo.**

## 6. Acceptance Criteria

- [x] **Dado** usuário com coleção "backend-docs" **quando** digita `#back` **então** vê `backend-docs` com badge `collection`.
- [x] **Dado** arquivo "api-spec.md" em knowledge **quando** digita `#api` **então** vê `api-spec.md` com badge `file`.
- [x] **Dado** seleção via Enter **quando** confirmada **então** input vira `#backend-docs ` e dropdown fecha.
- [x] **Dado** `oi#foo` **quando** digitado **então** dropdown não abre.
- [x] **Dado** dropdown aberto **quando** Esc **então** fecha sem alterar o input; `@` e `!` continuam funcionando.

## 7. Task Plan

- [x] **T1:** estado `_hash*` + regex + `QueueHashLookup`/`RefreshHashSuggestionsAsync`/`SelectHash` no `ChatView.razor` (espelho do bang, inserção de token em vez de conteúdo).
- [x] **T2:** dropdown markup + integração teclado (mesmos pontos de `_bangItems`).
- [x] **T3:** i18n ×8 + tailwind rebuild.
- [x] **T4:** `ChatHashTests.cs` bUnit; regressão `ChatBangTests`/`ChatMentionTests`.
- [x] **T5:** build + guards + DoD → PR.

## 8. Organization Guardrails

- **Security:** só coleções/arquivos do próprio usuário (endpoint já enforce); token inserido é só o nome — sem ids internos.
- **Scope:** não criar coleções pelo composer; não resolver `#` em client (server-side é fonte de verdade).
- **Architecture:** zero mudança backend; client consome contratos `Application` existentes.
- **Upstream parity:** `#` para knowledge é o comportamento do Open WebUI upstream (RAG references).

## 9. Definition of Done

- [x] RF-001..RF-005 implementados e cobertos por testes passando.
- [x] `dotnet build` + suite client verdes; regressão `@`/`!`/`/` intacta.
- [x] Drift guards verdes; i18n paridade.
- [x] SPEC → `Done` + arquivada com `Delivered`.

**Next action after DoD is complete:** PR `feature/Devin-20261104-chat-hash-collections`.

## Open Questions / Pending Ambiguity

- Nenhuma. Decisões: só coleções+arquivos do próprio usuário (server já enforça); seleção insere `#nome` (não chips — server resolve por nome).

## Delivered

PR [a preencher no squash]: dropdown `#` no composer — coleções RAG +
arquivos do usuário (badges collection|file, substring filter, top-20,
debounce 250ms, ↑↓/Enter/Tab/Esc); seleção insere `#nome ` resolvido
server-side por `ResolveReferenceFileIdsAsync`; `_hashCts` disposed.
4 bUnit (`ChatHashTests`); regressão `!`/`@` intacta. Closes #696.
