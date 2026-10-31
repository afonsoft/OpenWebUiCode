# SPEC-20261031-sonar-smell-csharp-misc

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-csharp-misc` |
| Type | `Refactor` |
| Stack | `.NET` (C# + Razor + CSS) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-csharp-misc-20261031` |
| Ticket | `GH#569` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** resolver as 254 issues C# restantes (todas as regras fora de S3776/S3358/S1192/S107)
**So that** o board do SonarCloud zera code smells C# — métodos estáticos corretos, sem parâmetros mortos, sem TODOs aparentes, LINQ idiomático.

**Problem context:**
Cauda longa C# — 254 issues em 113 arquivos, por regra (top):

| Issues | Regra | Fix típico |
| --- | --- | --- |
| 37 | `S2325` | método que não usa instância → `static` |
| 25 | `S8969` | `GetHashCode`/`Equals` sobre tipos mutáveis → ajustar |
| 25 | `S1172` | parâmetro não usado → remover do método |
| 20 | `S1135` | comentário contendo "TODO" → rewording ou resolver o pendente |
| 17 | `S1075` | hardcoded path/URI → extrair config/const |
| 14 | `S4487` | campo privado lido mas nunca escrito → `readonly`/remover |
| 12 | `S3267` | `foreach` com filtro → LINQ `Where`/`Select` |
| 9 | `S6966` | chamada async substituível → `await`/método async |
| 7 | `S6580` | formato de data/cultura → `CultureInfo` explícito |
| 7 | `S127` | `for` com contador alterado → `while`/iterador claro |
| 6 | `S6667` | catch e relançamento perdendo info → `throw;`/filtro |
| 5×6 | `S1125`, `S1905`, `S1481`, `S3220`, `S6608` | literal bool desnecessário, cast redundante, var não usada, interface check, set-based op |
| 5 | `css:S9423` | `tailwind.input.css` — font-family genérica ausente |
| ≤4×~30 | `S108`, `S4136`, `S2737`, `S2589`, `S2223`, `S3871`, `S2971`, `S1994`, `S927`, `S5034`, `S125`, `S2925`, etc. | catch vazio, ordem de membros, código morto, expressão redundante, etc. |

Top arquivos: `ChatRunExecutor.cs` (13), `Ide.razor` (12), `CheckpointService.cs` (8), `ComboService.cs` (8), `WebSearchService.cs` (7), `OAuthProviderCatalog.cs` (7), `AutomationHookEndpoints.cs` (6), `HeadlessRunner.cs` (6), `tailwind.input.css` (5 — mas **arquivo é input do Tailwind, revisar se a regra css:S9423 se aplica de verdade**), `ChatPipeline.cs` (5), `SkillDiscoveryService.cs` (5).

## 2. Scope

**In scope:** as 254 issues C# (+1 css:S9423 +1 javascript:S7785 +1 css:S1874 que caíram no bucket por arquivo) listadas no board — resolução por regra: fix idiomático padrão SonarCloud ou won't-fix justificado por item.

**Out of scope:**
- S3776/S3358/S1192/S107 (SPECs irmãs) — overlaps no mesmo método são resolvidos na mesma edição quando natural.
- Mudanças de comportamento/API.
- `tests/` — só se a issue estiver em test file (S2925 ×2 em `PtyIdleWatchdogTests.cs` está — entra como "testes" dentro do escopo).

## 3. Technical Context

**Where the change happens:** 113 arquivos em `src/**` e 1-2 em `tests/**` — cada grupo do board traz arquivo:linha + mensagem.

**Files to read before implementing:**
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos com issue IDs e mensagens exatas)
- Top arquivos acima
- Convenções: `CLAUDE.md`, `.claude/rules/`, `SONAR_FIX_TODO_BOARD.md` (raiz — precedentes de resolução)

**Files to create or modify:**
```text
src/** — ~113 arquivos
src/OpenWebUI.Client/tailwind.input.css — avaliar css:S9423 (font stack)
tests/OpenWebUI.Api.Tests/PtyIdleWatchdogTests.cs — S2925 (Thread.Sleep em teste)
```

## 4. Requirements

### RF-001: Resolução por regra
- **Description:** cada grupo rule×file é resolvido com o fix canônico da regra: `S2325`→`static`; `S1172`→remover param (callers ajustados); `S1135`→rewording/executar o pendente; `S1075`→config/const; `S4487`→`readonly`/remover; `S3267`→LINQ; `S6966`→`await` completo; `S127`→`while`/iterador; `S6667`→`throw;`/`when` filter; `S2925`→substituir `Thread.Sleep` por sinalização determinística no teste.
- **Rules:** nunca remover parâmetro de override/interface/DI-bound sem provar ausência de contrato; comentário "TODO" removido não esconde pendência real (criar nota/issue se o pendente subsistir).

### RF-002: Triage dos contestáveis
- **Description:** regras com chance de falso positivo no contexto (ex.: `css:S9423` no input Tailwind — onde as utilities vêm de `tailwind.config`/`app.css`; `S8969` em tipos cujos `Equals`/`GetHashCode` são intencionais; `S1075` em URIs de loopback/constantes de protocolo) → won't-fix com justificativa por item, ou ajuste mínimo.

### RF-003: Sem mudança comportamental
- **Description:** refactor puro; qualquer fix que altere comportamento (ex.: `S6667` mudando fluxo de exceção) recebe teste de regressão novo.

**Business rules / invariants:** `dotnet test` verde; cobertura não decresce; XML docs preservados.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** cada regra-grupo, **when** processada, **then** o fix canônico aplica ou won't-fix é justificado por item.
- [ ] **Given** os top-12 arquivos, **when** refatorados, **then** nenhuma issue das regras do bucket persiste neles.
- [ ] **Given** a suite, **then** verde em todas as fatias.
- [ ] **Given** o rescan, **then** zero issues C# fora das 4 regras-irmãs em `src/**`/`tests/**`.
- [ ] **Given** `tailwind.input.css`, **when** alterado, **then** `tailwindcss` regenera o bundle e o diff commita junto (obrigatório por regra do repo).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Param unused em override | `S1172` em implementação de interface | supressão localizada justificada (não quebrar contrato) |
| `Thread.Sleep` em teste | `S2925` `PtyIdleWatchdogTests.cs` | `Task.Delay`/`ManualResetEventSlim`/polling com timeout |
| Catch vazio intencional | `S108` | comentário de justificativa ou `LogTrace` (padrão RF-B6) |
| Font-family Tailwind | `css:S9423` | avaliar se input.css precisa de font-stack explícito ou é FP |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** agrupar issues por regra no board; ordenar por arquivo×regra; separar "fix direto" de "precisa triage".
- [ ] **T2 — Bloco hot:** ChatRunExecutor (13), Ide.razor (12), CheckpointService+ComboService (16), WebSearch+OAuthCatalog (14).
- [ ] **T3 — Regras de massa:** S2325 (37), S8969 (25), S1172 (25), S1135 (20), S1075 (17), S4487 (14), S3267 (12).
- [ ] **T4 — Cauda + triage:** restantes ~100 issues + S2925 em testes + avaliação css.
- [ ] **T5 — Validation por bloco:** `dotnet test`; `tailwindcss` regen se css tocado.
- [ ] **T6 — Done + PRs por bloco.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / .NET** | Suite verde; cobertura mantida; rescan sem as regras do bucket; cada won't-fix com justificativa escrita. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-csharp-misc-20261031`.
- **CSS:** `tailwind.input.css` alterado → regenerar `wwwroot/css/tailwind.css` e commitar o artefato.
- **Scope:** só as regras do bucket — sem opportunistic refactors fora do board.
- **Pendências:** `S1135` nunca apaga pendência real sem rastreio (criar issue quando o TODO for legítimo).

## 9. Definition of Done

- [ ] 254 issues resolvidas (fix ou won't-fix justificado).
- [ ] Suite verde; artefatos gerados (`tailwind.css`) consistentes.
- [ ] Board atualizado; rescan limpo para o bucket.

**Next action after DoD is complete:** `Status = Done` + PRs por bloco.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` `S1135`: os 20 "TODO" legítimos viram issues GitHub próprias ou são reworded? — decisão por item em T3 (padrão: pendência real → issue; comentário descritivo → reword).
- `[A DEFINIR]` `css:S9423` em `tailwind.input.css` — fix (adicionar font stack) ou FP — confirmar em T1 lendo o input.css.
