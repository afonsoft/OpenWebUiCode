# SPEC-20261103-sonar-vuln-i18n-fp

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-vuln-i18n-fp` |
| Type | `Security` |
| Stack | `.NET` (JSON i18n) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-vuln-i18n-fp-20261103` |
| Ticket | `GH#651` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** fazer triage das 3 issues `json:S2068` ("password" detected) em `pt-BR.json`
**So that** cada uma sai do board como falso-positivo confirmado (resolvida no SonarCloud) ou como chave renomeada com paridade nos 8 locales.

**Problem context:** `json:S2068` marca literais contendo "password" como possível credencial hard-coded. Em arquivo de i18n é quase certamente FP (labels/títulos de UI — ex.: `"Reset Password"`, `"Current Password"`). Linhas: 132, 161, 176 de `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json`. Precedente de triagem: `docs/specs/SPEC-20261031-sonar-vuln-fp-triage.md`.

## 2. Scope

**In scope:** as 3 linhas sinalizadas — inspecionar a chave/valor, classificar FP vs. risco real, e aplicar a resolução adequada.

**Out of scope:**
- Marcar "Won't fix" direto no SonarCloud — requer `SONAR_TOKEN` (ação manual do usuário; SPEC documenta o veredito).
- Renome de chaves que quebrem paridade — qualquer renome exige update nos 8 locales + referências no código (`check-i18n-parity.py` é gate).

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json` — labels de UI de auth.

**Files to read before implementing:**
- `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json` L132, L161, L176 (+ vizinhança)
- demais locales (`en-US.json` etc.) para a mesma chave
- usos da chave no `.razor`/`.cs` correspondente

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/i18n/*.json   (apenas se renome for necessário)
```

## 4. Requirements

### RF-001: Veredito por issue
- **Description:** para cada linha, registrar no board: (a) valor real da chave; (b) FP confirmado (label de UI) ou risco real; (c) ação: resolver no SonarCloud como *False positive/Accepted* (manual, precisa token) ou renomear chave.
- **Rules:** nenhum valor de credencial real deve existir no JSON — se houver, remover imediatamente e rotacionar.

### RF-002: Se renome necessário, paridade total
- **Description:** mesma mudança nos 8 locales + usos no código; `python3 tools/check-i18n-parity.py` verde.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] Cada uma das 3 issues tem veredito documentado no board (FP ×N ou fix).
- [ ] Se houve renome: `check-i18n-parity.py` verde e app renderiza as labels em pt-BR.
- [ ] Issues marcadas no SonarCloud (manual, pendentes de token) **ou** desaparecem no próximo scan.

## 7. Task Plan

1. Abrir pt-BR.json nas 3 linhas; extrair chave+valor.
2. Grep dos usos nos componentes.
3. Veredito por issue → atualizar board + nota em `.claude/memory/sonar-vendor-cleanup.md`-style (ou seção própria no board).
4. Se renome: aplicar em 8 locales + código; rodar `check-i18n-parity.py`.

## 8. Effort Estimate

`~30min` (triage + documentação; renome adiciona ~30min se necessário).
