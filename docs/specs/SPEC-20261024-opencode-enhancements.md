# SPEC-20261024-opencode-enhancements

## 0. Metadata
| Field | Value |
|---|---|
| Feature | Plano de Melhorias Baseado no fork alltomatos/opencode e OpenCode v2 |
| Type | Feature |
| Stack | .NET 10 / C# 14 / Blazor WASM / Entity Framework Core / Tailwind CSS v4 |
| Repository | afonsoft/OpenWebUiCode |
| Branch | `feature/Agent-20261024-opencode-enhancements` |
| Ticket | TICKET-OPENCODE-ENHANCEMENTS |
| Status | Completed |

## 1. User Story
As a desenvolvedor e usuário do Open WebUI Code,
I want incorporar as melhores práticas, arquiteturas de orquestração multi-agente (como o épico "Batuta" do alltomatos/opencode) e recursos avançados do OpenCode v2 (agentes declarativos em Markdown, CLI config, orquestração de workers em git worktrees, diffs estruturados e detecção de ociosidade em PTY),
So that nosso ambiente de coding agent em .NET ofereça capacidade robusta de engenharia autônoma, orquestração paralela segura, flexibilidade de configuração por camadas e paridade de ponta com os melhores harnesses de agentes do mercado.

## 2. Scope
### In Scope
- **Arquitetura de Orquestração Multi-Agente (Estilo Batuta):**
  - Papéis estruturados: *Arquiteto* (análise de repo, PRD e planejamento em `docs/batuta-pipeline.md`), *Orquestrador* (distribuição de issues, delegação e revisão estrita de diff antes de mergear) e *Workers* (sub-agents isolados ou CLIs externos em PTY).
  - Isolamento estrito por Git Worktree por worker (`workspaces/{user}/worktrees/{owner}__{repo}/{chatId}` e branch `chat/{chatId}`), garantindo concorrência sem colisões.
  - Status vivo em arquivo Markdown partilhado (`.batuta/{activityId}/pipeline.md`).
  - Detecção de término de turno de ferramentas externas em PTY baseada em silêncio de saída (`idle` timeout configurável).
- **Agentes Declarativos (.agent.md):**
  - Descoberta e parsing de arquivos `.agent.md` com frontmatter YAML e system prompt (`# Covers RF-001`).
- **Pipeline Batuta (`BatutaPipelineService`) & Watchdog PTY (`PtyIdleWatchdog`):**
  - Implementados com sucesso (`# Covers RF-002`, `# Covers RF-003`).

## 3. Technical Context
- `src/OpenWebUI.Application/Contracts/BatutaContracts.cs`
- `src/OpenWebUI.Infrastructure/Services/BatutaPipelineService.cs`
- `src/OpenWebUI.Infrastructure/Services/PtyIdleWatchdog.cs`
- `tests/OpenWebUI.Api.Tests/BatutaPipelineTests.cs`
- `tests/OpenWebUI.Api.Tests/PtyIdleWatchdogTests.cs`

## 4. Requirements & Implementation Status
- **RF-001:** Descoberta e Execução de Agentes Declarativos (.agent.md) — **Concluído**.
- **RF-002:** Pipeline de Orquestração Multi-Agente (Modelo Batuta) — **Concluído** (`BatutaPipelineService`).
- **RF-003:** Detecção de Ociosidade em PTY para Sub-Agentes Externos — **Concluído** (`PtyIdleWatchdog`).
- **RF-004:** Configuração Global Unificada (Estilo CLI Config) — **Planejado para próxima fase**.

## 5. Definition of Done
- [x] Código implementado e validado por testes unitários em NUnit.
- [x] SPEC arquivada em `docs/specs/`.
- [x] Commits organizados na branch `feature/Agent-20261024-opencode-enhancements`.
