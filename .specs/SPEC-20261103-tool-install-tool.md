# SPEC-20261103-tool-install-tool

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `tool-install-tool` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/tool-install-tool-20261103` |
| Ticket | — |
| Status | `Draft` |
| Parent | builtin-tools improvement sweep 2026-11-03 (agent tool primitives) |

## 1. User Story

**As a** agente em execução autônoma prolongada
**I want** uma tool `tool_install` que instala dependências/CLIs ausentes no ambiente durante a task
**So that** não travo em "comando não encontrado" nem preciso pedir ao usuário para instalar manualmente — mantendo política de aprovação para ações perigosas.

**Problem context:** o comparativo destaca `tool_install` do Antigravity como primitiva de resiliência: instalação dinâmica de utilitários ausentes. Hoje `shell_exec` executa qualquer comando (incl. apt/pip) mas sem curadoria — o agente chuta flags, não verifica se o binário ficou resolvível, e cada install é uma ação "perigosa" genérica. Uma tool dedicada dá allowlist de gerenciadores, verificação pós-install e auditoria.

## 2. Scope

**In scope:**
- Nova builtin `tool_install` em `ChatTools/Tools/` reaproveitando o runner/avaliação de risco do `ShellExecBuiltinTool`.
- Gerenciadores allowlisted: `apt-get`, `pip`/`pipx`, `npm -g`, `dotnet tool`, `cargo`, `go install` — cada um com template de comando validado (sem shell string livre).
- Verificação pós-install: `which`/`Get-Command` do binário alvo → reporta resolved path ou falha honesta.
- Gate de aprovação: mesma política de "perigoso" do shell_exec (`ToolApprovalOverrideService`/assessment) — install sudo/apt sempre exige aprovação salvo allowlist do agente.
- Timeout rígido e output truncado (mesmos limites do shell_exec).
- Dry-run/`check` mode: reporta se o pacote já está instalado sem instalar.

**Out of scope:**
- Gerenciadores ad-hoc (brew, choco) — fase 2.
- Cache/pin de versões.
- Instalação de skills/MCP (caminho separado, já existe `skill` builtin).

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Infrastructure/ChatTools/Tools/ToolInstallBuiltinTool.cs` (novo); reusa `ShellExecBuiltinTool` internals (prepared command, risk assessment, approval gate, ProcessRunner) — extrair o que for necessário para helper compartilhado em vez de duplicar.

**Files to read before implementing:**
- `ShellExecBuiltinTool.cs` — prepared command, allowlist de binário, assessment, approval flow, timeouts
- `ToolApprovalOverrideService.cs` — override/aprovação
- `IBuiltinChatTool.cs`, `BuiltinToolRegistry.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/ToolInstallBuiltinTool.cs (novo)
src/OpenWebUI.Infrastructure/ChatTools/Tools/ShellExecBuiltinTool.cs (extração se necessária)
tests/OpenWebUI.Api.Tests/** (novo)
```

## 4. Requirements

### RF-001: Instalação por gerenciador allowlisted
- **Description:** `tool_install{manager, package, binary?, check_only?}` monta comando a partir de template por gerenciador (`apt-get install -y <pkg>`, `pip install <pkg>`, `npm i -g <pkg>`, `dotnet tool install -g <pkg>`, `cargo install <pkg>`, `go install <pkg>`). Pacote validado contra regex de nome seguro (sem `;`/`|`/espaço/`&&`). Gerenciador fora da allowlist → erro.

### RF-002: Verificação pós-install
- **Description:** após install, resolve `binary` (ou nome do pacote) via `which`; retorna `{installed, binary, resolved_path, version?}` — falha de verificação reportada como tal (nunca "sucesso" falso).

### RF-003: Gate de aprovação e política
- **Description:** managers que tocam o sistema (apt-get/sudo) passam pela mesma avaliação de risco/aprovação do shell_exec; `pip`/`npm`/`dotnet tool`/`cargo` em escopo de usuário podem ter política mais leve configurável. Respeita deny de policies.

### RF-004: Check mode e limites
- **Description:** `check_only:true` só verifica presença do binário (sem install); timeout clampeado como shell_exec; saída truncada; erro estruturado `{error, stderr_excerpt}`.

## 5. API Contract

`tool_install{manager, package, binary?, check_only?, timeout_seconds?}` → `{ok, installed, resolved_path?, output_excerpt}` ou `{ok:false,error}`.

## 6. Acceptance Criteria

- [ ] Template de comando por manager — sem interpolação de shell no nome do pacote (injection-safe).
- [ ] `check_only` não executa install.
- [ ] apt-get dispara o mesmo gate de aprovação do shell_exec.
- [ ] Falha de verificação reportada honestamente.
- [ ] `dotnet test` verde (runner mockado — sem installs reais).

## 7. Task Plan

1. Schema + allowlist de managers + validação de nome de pacote.
2. Montagem de comando via template + exec no runner do shell_exec.
3. Verificação `which` + resultado estruturado.
4. Gate de aprovação + check mode + testes.

## 8. Effort Estimate

`~4-6h`.
