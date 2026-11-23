# SPEC-20261124 — Devin parity batch 2: wait_for + user-edits + footer

| Campo | Valor |
|-------|-------|
| Status | `Delivered` |
| Origem | engenharia reversa do Devin CLI (análise strings/ELF) — segundo lote de paridade barata |

## Requisitos

### RF-001 `wait_for` no `builtin:shell_exec`
- Novo arg opcional `wait_for` (regex). Presente → o comando sobe como
  job durável (mesmo caminho de `background:true`) e a tool faz poll no
  log do job a cada ~400ms até o regex casar, o job terminar ou
  `timeout_seconds` (default 60, cap 300) expirar.
- Resultado: output até o match + `jobId` + flag `matched`/`timedOut` —
  o processo **sobrevive** (readiness de servidores); `job_kill` encerra.
- Regex inválido → erro de argumento, sem spawnar.

### RF-002 `user-edits-injected`
- `WorkspaceEditWatcher` (FileSystemWatcher no workdir, recursivo):
  ignora ruído (`.git`, `bin`, `obj`, `node_modules`, `.chat-jobs`,
  `.chat-tmp`, `*.tmp`, `*~`, `*.swp`) e escritas das próprias tools —
  `MarkWrite(path)` chamado pelo pipeline após tool mutável bem-sucedida.
- `Drain()` devolve paths relativos alterados desde o último drain,
  deduplicados, cap 20 — chamado na fronteira de round; paths viram nota
  `user` `<user_edits>` instruindo a reconhecer mudanças externas.
- Ciclo de vida: criado por run no executor com o BuiltinToolContext e
  descartado ao fim da run.

### RF-003 `tool_result_footer` — já entregue
- `AgentLoop:ToolResultFooter` é anexado a todo tool result no loop
  (`ToolLoopCallbacks.ToolResultFooter`, SPEC-20261031-agent-prompt-pack)
  — SPEC documenta a paridade; sem código novo.

## Testes

1. `wait_for`: regex inválido → erro; poll-case fake → matched/timeout/
   job-morto cobrem o loop de espera.
2. `WorkspaceEditWatcher`: arquivo criado → drain lista; MarkWrite →
   suprime; `.chat-jobs`/`.git`/bin/obj → ignorados; cap 20.
3. Nota `<user_edits>` montada com paths relativos.

## Critérios de aceite

- [ ] `shell_exec {"wait_for":"listening"}` retorna ao primeiro match
      com jobId; timeout → `timedOut` no payload, job segue rodando.
- [ ] Edição externa durante a run injeta `<user_edits>` no próximo
      round; edição via file_write/apply_patch não gera nota.
- [ ] Build 0 warnings + testes verdes.
