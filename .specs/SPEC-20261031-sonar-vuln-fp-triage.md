# SPEC-20261031-sonar-vuln-fp-triage

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-vuln-fp-triage` |
| Type | `Security` |
| Stack | `.NET` (i18n JSON) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-vuln-fp-triage-20261031` |
| Ticket | `GH#564` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** triar as 3 findings `json:S2068` ("hard-coded credential") no `pt-BR.json` como falsos positivos com resolução formal
**So that** o board do SonarCloud fica limpo sem quebrar a paridade de chaves i18n nos 8 locales.

**Problem context:**
SonarCloud reporta `json:S2068` MAJOR nas linhas 132, 161 e 176 de `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json` — issues `AaEl44TaMT913z7YBsg0`, `…sg1`, `…sg2`. As linhas são chaves de tradução (`"settings.current_password": "Senha atual"`, `"settings.new_password": "Nova senha"`, `"settings.allow_password_login": "Permitir login com senha"`) — **labels, não credenciais**. A palavra "password" no nome da chave dispara a heurística.

Renomear as chaves não é opção trivial: `tools/check-i18n-parity.py` (drift guard, roda no CI) exige o mesmo conjunto de chaves nos 8 locales — e as chaves são referenciadas nos `.razor`. O custo/benefício pende para **Won't Fix** com justificativa.

## 2. Scope

**In scope:**
- Verificar as 3 linhas e confirmar FP (conteúdo = label pt-BR, sem valor secreto).
- Resolver como "Won't Fix" (ou "False Positive") no SonarCloud com justificativa — requer `SONAR_TOKEN`/acesso UI (ação humana).
- Se a preferência do projeto for eliminar a heurística via código: avaliar alternativa (ex.: renomear chave `*_password` → `*_passwd` em todos os 8 locales + callers `.razor`) — apenas se o usuário preferir fix a triage.

**Out of scope:**
- Outras findings S2068 em outros arquivos (nenhuma acionável hoje).
- Mudança de conteúdo das traduções.

## 3. Technical Context

**Where the change happens:** triage no SonarCloud (ação fora do repo) + opcionalmente renome de chaves em `src/OpenWebUI.Client/wwwroot/i18n/*.json` (8 arquivos) + usos em `.razor`.

**Files to read before implementing:**
- `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json:125-180`
- `tools/check-i18n-parity.py` (regra de paridade)
- usos das chaves: `grep -rn "settings.current_password\|settings.new_password\|settings.allow_password_login" src/`

**Files to create or modify:**
```text
(nenhum, no caminho won't-fix — resolução é no SonarCloud)
# alternativa fix: src/OpenWebUI.Client/wwwroot/i18n/*.json + .razor que citam as chaves
```

## 4. Requirements

### RF-001: Confirmação de falso positivo
- **Description:** evidenciar que as 3 linhas contêm apenas labels de UI (sem segredo) — print/diff das linhas no relatório da entrega.
- **Input → Output:** issues `AaEl44TaMT913z7YBsg0/1/2` → classificadas `FALSE_POSITIVE` com evidência.

### RF-002: Resolução formal no SonarCloud
- **Description:** marcar as 3 issues como "Won't Fix"/"False Positive" com comentário ("i18n label key, not a credential — key name triggers heuristic; rename blocked by i18n parity guard"). Requer `SONAR_TOKEN` — ação humana registrada.

### RF-003: Guard de recorrência (opcional)
- **Description:** documentar na memória do repo (`.claude/memory/sonar-vendor-cleanup.md` ou nota similar) que findings S2068 em `i18n/*.json` são FP conhecidos — evita retrabalho em scans futuros.

**Business rules / invariants:**
- Nunca renomear chave i18n sem rodar `check-i18n-parity.py` nos 8 locales.
- Nenhum código é alterado no caminho won't-fix.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** as 3 issues no board, **when** triadas, **then** constam `FALSE_POSITIVE`/`Won't Fix` com comentário no SonarCloud.
- [ ] **Given** `check-i18n-parity.py`, **when** executado, **then** continua OK (nada renomeado no caminho won't-fix).
- [ ] **Given** um futuro scan, **when** re-reportar S2068 nos mesmos arquivos, **then** existe nota de FP conhecido na memória do repo.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Usuário prefere fix | renomear chaves | Só com paridade verde nos 8 locales + grep de usos em `.razor` — vira T4 alternativa |
| SONAR_TOKEN indisponível | sem acesso API | instruções manuais de UI documentadas (mesmo padrão de `sonar-vendor-cleanup.md`) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** confirmar as 3 linhas (evidência no relatório); verificar se `SONAR_TOKEN` está disponível; grep dos usos das chaves.
- [ ] **T2 — Decision:** com o usuário, escolher won't-fix (recomendado) vs rename-fix.
- [ ] **T3 — Execution:** won't-fix → resolução via UI/API + nota de memória; rename → chaves novas nos 8 locales + callers + `check-i18n-parity.py` verde.
- [ ] **T4 — Verification:** board limpo / parity OK.
- [ ] **T5 — Done + PR:** DoD → `Status = Done` (+ PR somente se houve diff — caminho won't-fix pode não gerar PR de código).

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Security triage** | Evidência de FP por linha; resolução formal no SonarCloud; `check-i18n-parity.py` OK. |

## 8. Organization Guardrails (mandatory)

- **Branches:** só se houver diff (rename-fix) → `fix/sonar-vuln-fp-triage-20261031`.
- **Security:** nenhum valor de i18n é segredo — não tratar como credencial real nem rotacionar nada.
- **Scope:** somente as 3 issues `AaEl44TaMT913z7YBsg0/1/2`.

## 9. Definition of Done

- [ ] RF-001/002 (e RF-003 se aplicável) concluídos.
- [ ] Issues encerradas no SonarCloud com justificativa.
- [ ] Paridade i18n preservada (guard verde).

**Next action after DoD is complete:** `Status = Done` (PR apenas se houve diff de código).

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` `SONAR_TOKEN` disponível? Se não, entregar instruções de UI para ação humana (mesmo modelo de `sonar-vendor-cleanup.md`).
- `[A DEFINIR]` Preferência do usuário: won't-fix (recomendado) vs rename das chaves.
