# SPEC-20261103-batch-file-read

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `batch-file-read` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/batch-file-read-20261103` |
| Ticket | — |
| Status | `Draft` |
| Parent | builtin-tools improvement sweep 2026-11-03 (agent tool primitives) |

## 1. User Story

**As a** agente explorando um codebase desconhecido
**I want** ler N arquivos numa única chamada `file_read`
**So that** reduzo round-trips de tool (latência + tokens de protocolo) quando já sei quais arquivos preciso — o `read_files` em lote do Cline existe justamente para isso.

**Problem context:** `file_read` hoje aceita um único `path`. Explorar um módulo típico (8-15 arquivos) custa 8-15 invocações de tool serializadas — cada uma com overhead de schema/validação no loop. O comparativo mostra que leitura em lote é primitiva de eficiência (Cline). O ganho compõe com `file_glob`/`file_grep` que já descobrem candidatos.

## 2. Scope

**In scope:**
- `file_read` aceita `paths` (array) como alternativa a `path` — itens `{path, offset?, limit?}` com defaults iguais ao modo single.
- Resultado ordenado por item: `{path, content, lines, truncated}` ou `{path, error}` — erro num item não derruba o lote (per-path isolation).
- Caps: máx de itens por chamada (default 20, clamp configurável), cap agregado de bytes/linhas retornadas; cada item mantém os limites atuais de tamanho.
- Back-compat total: `path` único + `offset`/`limit` continuam idênticos (schema e output).

**Out of scope:**
- Leitura paralela interna (pode ser sequencial — o ganho é de round-trip, não de I/O).
- Glob dentro do batch (caminhos explícitos apenas — `file_glob` já resolve expansão).
- Conteúdo binário/imagens (segue regra atual do file_read).

## 3. Technical Context

**Where the change happens:** `FileBuiltinTools.cs` — classe do `file_read` (~L156-320): schema de args + execução; resultado batch vira array de sub-resultados.

**Files to read before implementing:**
- `FileBuiltinTools.cs` (`file_read` — caps atuais, confinamento, output format)
- Testes existentes de `file_read` (padrão de asserts/output)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs
tests/OpenWebUI.Api.Tests/** (testes da tool file_read)
```

## 4. Requirements

### RF-001: Modo batch
- **Description:** `paths[]` de `{path, offset?, limit?}` (1..20 itens); resultados na mesma ordem; `path` e `paths` são mutuamente exclusivos (ambos → erro de validação).

### RF-002: Isolamento por item
- **Description:** path inexistente/fora do workspace/não-arquivo → `{path, error}` naquele item, demais itens processados normalmente; batch nunca retorna sucesso parcial como erro global.

### RF-003: Caps agregados
- **Description:** soma de bytes de conteúdo ≤ cap agregado (default: 4× cap single); quando estoura, itens restantes retornam `{path, error:"budget_exceeded"}` — truncamento honesto, nunca silencioso.

### RF-004: Back-compat
- **Description:** chamadas existentes com `path` único produzem output byte-idêntico ao atual (schema aditivo apenas).

## 5. API Contract

`file_read{paths:[{path,offset?,limit?},...]}` → `{results:[{path,content?,lines?,truncated?,error?}]}`.

## 6. Acceptance Criteria

- [ ] Batch de 5 arquivos → 5 resultados ordenados numa chamada.
- [ ] Item inválido no meio → erro só naquele item.
- [ ] Cap agregado → itens excedentes com `budget_exceeded`.
- [ ] Single-path → wire/output idênticos.
- [ ] `dotnet test` verde.

## 7. Task Plan

1. Schema aditivo (`paths`) + validação de exclusividade.
2. Loop de leitura com isolamento de erro por item.
3. Caps agregados + contadores.
4. Testes (batch ordenado, erro por item, budget, back-compat).

## 8. Effort Estimate

`~3-4h`.
