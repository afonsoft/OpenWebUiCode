# SPEC-20261103-notebook-edit-tool

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `notebook-edit-tool` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/notebook-edit-tool-20261103` |
| Ticket | `GH#671` |
| Status | `Approved` |
| Parent | builtin-tools improvement sweep 2026-11-03 (agent tool primitives) |

## 1. User Story

**As a** agente rodando num workspace com Jupyter notebooks
**I want** uma tool `notebook_edit` que manipula células `.ipynb` preservando metadados nbformat
**So that** edições em notebooks não corrompam o JSON (hoje só dá para usar `file_edit` cirúrgico — frágil em JSON grande — ou `file_write` que reescreve o arquivo inteiro e perde outputs/execução).

**Problem context:** o comparativo de primitivas mostra `NotebookEdit` como diferencial do Claude Code: manipulação direta de células sem quebrar metadados. Nosso inventário (`file_read`/`file_edit`/`file_write`) não é ciente de nbformat — uma edição textual errada deixa o notebook inválido e reescrever tudo apaga `execution_count`/outputs.

## 2. Scope

**In scope:**
- Nova builtin `notebook_edit` em `ChatTools/Tools/` (padrão `FileBuiltinTools` — workspace-scoped, `IBuiltinChatTool`).
- Operações: `replace` (edita `source` de célula por índice), `insert` (nova célula `code`/`markdown` em índice), `delete` (remove célula por índice).
- Preservação nbformat: `nbformat`, `nbformat_minor`, `metadata` (kernelspec/language_info), `id`/`metadata`/`attachments` de células intocados quando não alvo.
- Ao editar `source` de célula `code`: limpar `outputs` e `execution_count` (conteúdo stale).
- Leitura helper: `notebook_read` (lista células com índice/tipo/first-lines) — discovery barata antes do edit.
- Escrita atômica (tmp + move) e validação JSON após mutação.

**Out of scope:**
- Execução de células (kernel) — `code_interpreter` já cobre execução avulsa.
- Diffs/merge de notebooks (nbdime).
- Suporte a `.ipynb` fora do workspace.

## 3. Technical Context

**Where the change happens:** novo arquivo `src/OpenWebUI.Infrastructure/ChatTools/Tools/NotebookBuiltinTools.cs`; registro automático via `BuiltinToolRegistry`/`IBuiltinChatTool` (DI scan existente). Workspace sandbox idêntico ao `FileBuiltinTools` (mesma resolução de path confinado).

**Files to read before implementing:**
- `FileBuiltinTools.cs` — convenções de arg schema, path confinement, escrita atômica, size caps
- `IBuiltinChatTool.cs`, `BuiltinToolRegistry.cs` — registro/tiers
- nbformat spec (JSON: `cells[]`, `source` string[] ou string, `cell_type`, `outputs`, `execution_count`)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/NotebookBuiltinTools.cs (novo)
tests/OpenWebUI.Api.Tests/** (novo arquivo de testes da tool)
```

## 4. Requirements

### RF-001: Leitura estruturada
- **Description:** `notebook_read` retorna por célula: índice, `cell_type`, primeiras N linhas do `source`, contagem de outputs — sem dump do JSON inteiro.

### RF-002: Mutação de células
- **Description:** `notebook_edit` com `operation` ∈ `replace|insert|delete`; `index` (0-based) ou `cell_id`; `cell_type` (`code`/`markdown`) e `source` (string ou string[]) conforme operação. Edição de `code` zera `outputs`/`execution_count`.

### RF-003: Integridade nbformat
- **Description:** round-trip preserva todos os campos não-alvo (metadados de doc e célula, `id`s, attachments); JSON resultante validado (parse + `cells` array presente); escrita atômica (tmp+move) — nunca deixa arquivo parcial.

### RF-004: Confinamento e limites
- **Description:** apenas `.ipynb` dentro do workspace (mesma regra do FileBuiltinTools); arquivo ≤ cap existente de leitura; erros claros para índice fora de faixa / arquivo não-notebook / JSON inválido.

## 5. API Contract

`notebook_edit{path, operation, index?, cell_id?, cell_type?, source?}` → `{ok, index, cell_type}` ou erro descritivo. `notebook_read{path, max_lines?}` → `{cells:[{index, cell_type, preview, outputs_count}]}`.

## 6. Acceptance Criteria

- [ ] replace/insert/delete round-trip em fixture nbformat mantém metadados e `id`s.
- [ ] Editar `code` zera `execution_count`/`outputs`; editar `markdown` não toca em nada além do `source`.
- [ ] JSON corrompido não é gravado (atomic write + validação).
- [ ] Path fora do workspace → erro, sem I/O.
- [ ] `dotnet test` verde.

## 7. Task Plan

1. `notebook_read` + parser nbformat mínimo (JsonNode).
2. `notebook_edit` operações + regras de outputs/exec_count.
3. Escrita atômica + validação + confinamento.
4. Testes com fixtures .ipynb (code/markdown/attachments).

## 8. Effort Estimate

`~4-6h`.
