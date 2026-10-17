# SPEC-20261016-tool-output-bounding

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `tool-output-bounding` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-tool-output-bounding` |
| Ticket | [#402](https://github.com/afonsoft/open-webui/issues/402) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Completed` |
| Fonte | análise opencode v2.0.31 (`tool-output.ts`: bound genérico 2000 linhas/50KiB, output completo em arquivo gerenciado + path no resultado) |

## 1. User Story

**As a** mantenedor de runs longas de agente
**I want** saídas de tool acima de um teto gravadas em arquivo gerenciado e só o tail entregue ao modelo
**So that** um `file_grep`/log gigante não exploda o contexto — e o modelo ainda saiba onde ler o resto.

**Problem context:**
Hoje cada tool trunca por conta própria (diff 8KB, shell capped). `file_read`/`file_grep`/`fetch_url` podem devolver dezenas de KB que vão direto pro histórico e ficam lá para sempre — custo de tokens e risco de overflow. O opencode aplica bound genérico pós-execução (2000 linhas / 50 KiB), grava o full output em `tool-output/` reter por 7 dias e devolve tail+path ao modelo.

## 2. Scope

**In scope:**
- RF-001: `ToolOutputService` — bound pós-execução: `maxLines=2000`, `maxBytes=50KiB` (configurável `BuiltinTools:OutputMaxLines/OutputMaxBytes`).
- RF-002: overflow → grava output completo em `{data}/tool-output/tool_{id}.txt`, retorna head+tail delimitados + `[output truncated — full output at {path} ({lines} lines, {bytes} B)]`.
- RF-003: `file_read`/`file_grep`/`shell_exec`/`fetch_url`/`code_interpreter`/`lsp_*` passam pelo bound; tool que já devolve truncado com path próprio não é re-truncada (`metadata.truncated` respeitado).
- RF-004: retenção — cleanup de arquivos >7 dias (padrão `FileRetention`/`CheckpointService` cleanup existente).
- RF-005: o path gravado é legível por `file_read` do mesmo workspace (não por shell fora do sandbox) — dentro do workspace quando possível, senão diretório de dados da app com URL `tool-output://` e tool `file_read` resolvendo esse scheme.

**Out of scope:**
- Compressão/resumo semântico de outputs grandes.
- Streaming incremental de output para a UI.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` — ponto único de dispatch (pós-`ExecuteAsync` das tools).
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/*.cs` — `Truncate` helpers atuais.
- `src/OpenWebUI.Infrastructure/Services/CheckpointService.cs` — padrão de cleanup/retenção a reutilizar.
- `DataPaths` (Domain) — diretório de dados da app.
- `WorkspaceFiles` — resolução de paths dentro do workspace (para o scheme legível).

## 4. Requirements

### RF-001: Bound genérico
- **Input → Output:** `ToolExecutionOutcome.Text` → truncado (head 70%/tail 30% do limite de linhas, respeitando bytes) + marcador + metadata `{truncated:true, outputPath, totalLines, totalBytes}`.

### RF-002: Arquivo gerenciado
- **Description:** escrita atômica (`tmp`+rename) em `{data}/tool-output/`; nome `tool_{callId ou guid}.txt`; cleanup no boot/diário >7d.

### RF-003: Respeito a metadata existente
- **Description:** outcome já `truncated` com path próprio (shell atual) passa intacto.

### RF-004: Cleanup
- **Description:** `ToolOutputCleanup` executa no `ChatRunDispatcher` boot + timer diário; falhas logadas, nunca fatais.

### RF-005: Leitura do tail completo
- **Description:** `file_read` aceita path absoluto sob `{data}/tool-output/` (allowlist estrita — não abre leitura arbitrária fora do workspace); alternativa: scheme `tool-output://{id}` resolvido internamente. Decisão: allowlist de prefixo no `file_read`, mais simples.

## 5. API Contract

Sem API nova. O resultado da tool carrega o marcador:
```text
<head…>
[... 4.231 lines omitted — full output: tool-output/tool_a1b2c3.txt (5.000 lines, 210 KiB) ...]
<tail…>
```

## 6. Acceptance Criteria

- [x] **Given** `file_grep` retorna 5.000 linhas **when** o resultado é materializado **then** o modelo recebe ≤2000 linhas + marcador com path e o arquivo existe em `tool-output/`.
- [x] **Given** output de 30 linhas **when** executa **then** passa intacto (`truncated:false`).
- [x] **Given** arquivo `tool_*.txt` com 8 dias **when** o cleanup roda **then** é removido.
- [x] **Given** modelo chama `file_read` no path retornado **when** a tool executa **then** lê o conteúdo completo.
- [x] **Given** `file_read` pede `/etc/passwd` **when** executa **then** nega (allowlist não abre FS arbitrário).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Output binário/base64 | bytes não-UTF8 | bound por bytes; marcador avisa |
| Workspace read-only | sem permissão de escrita em data dir | fallback: só trunca, sem arquivo |
| Output já pequeno mas com path metadata | `truncated:true` próprio | não re-grava |

## 7. Task Plan

- [x] **T1 —** `ToolOutputService` (bound, head/tail, escrita atômica, cleanup).
- [x] **T2 —** Wire no `ToolExecutor.ExecuteAsync` pós-resultado.
- [x] **T3 —** `file_read` allowlist `{data}/tool-output/` (ou scheme).
- [x] **T4 —** Cleanup no boot + testes de retenção.
- [x] **T5 —** build+test; DoD; PR.

**7.1 Validation strategy:** unit tests do bound/atomic write/cleanup + integration `file_read` allowlist; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-tool-output-bounding`.
- `SecretScrubber` no output ANTES de gravar (arquivo não pode conter secret que o modelo veria).
- Allowlist estrita de leitura — sem path traversal (`..`, symlink).
- Diretório `tool-output/` fora do repo do usuário; gitignored por padrão.

## 9. Definition of Done

- [x] RF-001..RF-005 implementados e testados.
- [x] Critérios da seção 6 verdes.
- [x] build/test verdes.

## Open Questions / Pending Ambiguity

- Scrub antes ou depois de gravar? Decisão: antes — arquivo deve ser tão seguro quanto o que o modelo vê.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #423 · d3ffa5cd3 |
| Issue | https://github.com/afonsoft/open-webui/issues/402 |
