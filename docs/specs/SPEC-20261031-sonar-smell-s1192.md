# SPEC-20261031-sonar-smell-s1192

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s1192` |
| Type | `Refactor` |
| Stack | `.NET` (C#) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s1192-20261031` |
| Ticket | `GH#567` |
| Status | `Completed` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** extrair constantes para as 97 literais duplicadas sinalizadas por `csharpsquid:S1192` (MAJOR)
**So that** strings-chave (nomes de campo JSON, roles, endpoints) têm fonte única e o board limpa a 3ª maior família.

**Problem context:**
`S1192` = "String literals should not be duplicated" — 97 issues em 44 arquivos. Concentração (top):

| Issues | Arquivo |
| --- | --- |
| 22 | `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` |
| 6 | `src/OpenWebUI.Infrastructure/Services/WebSearchService.cs` |
| 5 | `src/OpenWebUI.Mcp/McpServer.cs` |
| 5 | `src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs` |
| 4 | `src/OpenWebUI.Infrastructure/Lsp/LspTypes.cs` |
| 3 | `ApiService.cs`, `LspEndpoints.cs`, `CheckpointService.cs`, `AudioService.cs`, `RetrievalEndpoints.cs`, `ChannelEndpoints.cs` |
| ≤2 | demais ~33 arquivos |

Precedente: RF-A1 do sweep de 19/out criou `MessageKey`…`PartsKey`/`RoleSystem` como constantes no mesmo estilo — seguir a convenção (`private const string XxxKey = "..."` no topo da classe).

## 2. Scope

**In scope:** as 97 issues `S1192` em `src/**` — extrair `const` (ou reusar constante existente) para cada literal duplicada ≥3 vezes.

**Out of scope:**
- Literais em `tests/` (a regra reporta só src aqui; se houver em tests, avaliar separadamente).
- Renome de literais públicas/serializadas — o **valor** da string é contrato (JSON keys, role names): nunca alterar o texto.

## 3. Technical Context

**Where the change happens:** 44 arquivos; maior densidade em `ProviderService.cs` (22 — sobras do RF-A1: literais de wire JSON que ficaram) e `WebSearchService.cs` (6).

**Files to read before implementing:**
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos S1192 com a literal exata por linha)
- `ProviderService.cs` — convenção `*Key`/`RoleSystem` existente
- demais arquivos top

**Files to create or modify:**
```text
src/** — 44 arquivos conforme board
```

## 4. Requirements

### RF-001: Constante por literal duplicada
- **Description:** cada literal reportada vira `private const string` (ou `internal`/`static readonly` quando cross-class) nomeada por semântica (`ContentKey`, `RoleSystem`, `PathData`, …), no padrão já usado no repo; se já existe constante equivalente no arquivo/escopo, reutilizar em vez de duplicar.
- **Rules:** o **valor** nunca muda; nome descreve o papel, não o conteúdo.
- **Input → Output:** `"tool_calls"` ×4 → `private const string ToolCallsKey = "tool_calls";` + usos.

### RF-002: Sem literal deixada para trás
- **Description:** todas as ocorrências da literal no arquivo migram para a constante (a regra conta por arquivo); literal em interpolação `$"{x}"` também migra.

### RF-003: Sem mudança de wire/contract
- **Description:** strings que são contrato externo (JSON fields, paths, role names, MIME types) preservam valor byte-a-byte — a mudança é só mecânica (literal → const).

**Business rules / invariants:** `dotnet test` verde; zero alteração de payload/serialização (testes de contract existentes cobrem).

## 5. API Contract

N/A — valores de campo JSON preservados exatamente.

## 6. Acceptance Criteria

- [ ] **Given** `ProviderService.cs`, **when** refatorado, **then** zero literais reportadas por `S1192` (reuso das `*Key` existentes + novas conforme necessário).
- [ ] **Given** cada arquivo do board, **when** processado, **then** a literal aparece uma única vez (na declaração da const).
- [ ] **Given** payloads JSON produzidos/consumidos, **when** testes rodam, **then** bytes idênticos (contrato intacto).
- [ ] **Given** o próximo scan, **then** zero `S1192` em `src/**`.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Literal já constante em outra classe | mesma string em 2 arquivos | cada arquivo mantém sua const privada (ou promove a `internal static class` compartilhada se ≥3 arquivos — decidir por coesão) |
| Literal em atributo/anotação | `[Route("x")]` etc. | const funciona em atributos (`const` compile-time) — migrar |
| Literal "quase igual" | `"chat_id"` vs `"chatId"` | não fundir — só idênticas |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** listar literais exatas por grupo no board (campo `message`/`textRange` das issues).
- [ ] **T2 — ProviderService:** 22 issues — completar o conjunto `*Key` iniciado no RF-A1.
- [ ] **T3 — WebSearchService + McpServer + ConfigEndpoints + LspTypes:** 20 issues.
- [ ] **T4 — Cauda (~55 issues, ~33 arquivos).**
- [ ] **T5 — Validation:** `dotnet test` + build; rescan.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / .NET** | Suite verde; contrato JSON preservado (testes existentes); zero `S1192` no rescan. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-s1192-20261031`.
- **Contrato:** valores de strings intocados — diff só introduz nome.
- **Scope:** só `S1192` em `src/**`.

## 9. Definition of Done

- [ ] 97 issues resolvidas.
- [ ] Suite verde; rescan sem `S1192`.
- [ ] Convenção de nomenclatura consistente com `*Key` existente.

**Next action after DoD is complete:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Promover literais cross-arquivo a classe compartilhada (`internal static class WireKeys`) vs const privada por arquivo — recomendação: privada salvo ≥3 arquivos com a mesma literal (avaliar em T1).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/584 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/567
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/560
