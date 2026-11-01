# SPEC-20261031-sonar-vuln-ci-workflows

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-vuln-ci-workflows` |
| Type | `Security` |
| Stack | `Infra` (GitHub Actions / shell) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-vuln-ci-workflows-20261031` |
| Ticket | `GH#562` |
| Status | `In review` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** corrigir as 5 findings do SonarCloud nos workflows de CI (supply-chain hardening)
**So that** o pipeline não executa artefato baixado sem verificação nem baixa por canal inseguro.

**Problem context:**
SonarCloud `afonsoft_open-webui` reporta 4 VULNERABILITY + 1 CODE_SMELL em `.github/workflows/` — incluindo 1 **BLOCKER**:

| Issue | Regra | Arquivo:linha | Achado |
| --- | --- | --- | --- |
| `AaFWWJgNyGpyqZsdZ7yb` | githubactions:**S8482** BLOCKER | `security-scan.yml:114` | `bash <(curl -fsSL …download-actionlint.bash)` — executa script baixado sem verificação de integridade. |
| `AaFWWJgNyGpyqZsdZ7yc` | githubactions:S6506 MAJOR | `security-scan.yml:114` | `curl` sem `--proto =https --tlsv1.2` (redirect p/ http possível). |
| `AaFWWJgNyGpyqZsdZ7yd` | githubactions:S8541 MAJOR | `security-scan.yml:116` | `pipx install zizmor` sem `--only-binary :all:` — sdist executa `setup.py`. |
| `AaEl440pMT913z7YBs3Y` | githubactions:S6506 MAJOR | `ci-build-test.yml:312` | `curl -sSL --proto-redir =https` — força https no redirect mas não no request inicial. |
| `AaGM9MdaFGVBesHTKBxQ` | githubactions:S7679 MINOR | `ci-build-test.yml:493` | `$1` posicional na função `status()` — atribuir a variável local. |

⚠️ **Hard Rule do repo:** `.github/workflows/` não pode ser modificado sem revisão humana — esta SPEC exige aprovação humana explícita das mudanças no diff antes do merge.

## 2. Scope

**In scope:**
- `security-scan.yml`: verificação de integridade do instalador do actionlint (checksum SHA256 pinado ou binário assinado / instalação via package manager com digest), enforce TLS no `curl`, `--only-binary :all:` no pipx/pip.
- `ci-build-test.yml`: `--proto =https` (ou equivalente) no curl do tailwind + variável local para `$1` em `status()`.

**Out of scope:**
- Reescrita/reorganização dos workflows, pins de actions por SHA (fora do achado).
- Qualquer outro arquivo fora de `.github/workflows/`.

## 3. Technical Context

**Where the change happens:** `.github/workflows/security-scan.yml` (job de lint supply-chain, ~:107-118) e `.github/workflows/ci-build-test.yml` (~:310-314, ~:488-498).

**Files to read before implementing:**
- `CLAUDE.md` (Hard Rule 5 — workflows protegidos)
- `.github/workflows/security-scan.yml` · `ci-build-test.yml`
- docs oficiais: actionlint release checksums (`rhysd/actionlint`), pip `--only-binary`, curl `--proto`.

**Files to create or modify:**
```text
.github/workflows/security-scan.yml
.github/workflows/ci-build-test.yml
```

## 4. Requirements

### RF-001: Verificação de integridade do artefato baixado (S8482 BLOCKER)
- **Description:** o script `download-actionlint.bash` baixado via `curl` não pode executar sem verificação — baixar para arquivo, conferir SHA256 pinado por versão (`ACTIONLINT_VERSION`) contra checksum oficial publicado, e só então executar; ou substituir pela release binária com checksum.
- **Input → Output:** install step → download + `sha256sum -c` antes de executar.

### RF-002: TLS enforce no curl (S6506 ×2)
- **Description:** ambos os `curl` usam `--proto "=https" --tlsv1.2` (ou `https://` + `--proto-redir "=https"` já presente mais o proto inicial) — request inicial e redirects só por HTTPS.
- **Input → Output:** `security-scan.yml:114` e `ci-build-test.yml:312` → flags aplicadas.

### RF-003: pip sem execução de setup scripts (S8541)
- **Description:** instalação do zizmor usa `--only-binary :all:` no `pipx install`/`pip install` — falha explícita em vez de rodar `setup.py` de sdist.

### RF-004: Positional parameter nomeado (S7679)
- **Description:** `status()` em `ci-build-test.yml:488-498` atribui `$1` a variável local (`local st="$1"`) antes do `case`.

**Business rules / invariants:**
- Pin de versão permanece (`ACTIONLINT_VERSION`, `ZIZMOR_VERSION`, tailwind `v4.3.3`) — nada de `latest`.
- CI continua verde: actionlint/zizmor/tailwind instalam e executam.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** o step de install do actionlint, **when** roda, **then** o script só executa após `sha256sum -c` bem-sucedido (falha aborta o job).
- [ ] **Given** os dois `curl`, **when** inspecionados, **then** carregam enforce de HTTPS (`--proto`/`--proto-redir =https`) inicial + redirect.
- [ ] **Given** o `pipx/pip install zizmor`, **when** inspecionado, **then** contém `--only-binary :all:`.
- [ ] **Given** `status()`, **when** inspecionada, **then** `$1` é lido via variável local nomeada.
- [ ] **Given** workflow dispatch/PR, **when** CI roda, **then** jobs de security-scan e tailwind permanecem verdes.
- [ ] **Given** o diff do PR, **when** revisão humana ocorre, **then** aprovação explícita registrada (Hard Rule 5).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Checksum divergente | artefato adulterado | job falha antes de executar o script |
| zizmor sem wheel | sdist only | `--only-binary` falha explícito (não executa setup.py) — tratar como bloqueio intencional |
| Redirect http→https | URL encurtada | bloqueado por `--proto =https` |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler os dois workflows nas linhas citadas; confirmar checksum oficial do actionlint para `v1.7.12` (release assets) e wheel do zizmor `1.25.2`.
- [ ] **T2 — Implementation:** aplicar RF-001…RF-004 com diffs mínimos.
- [ ] **T3 — Verification:** `actionlint` no workflow editado (o próprio job valida a sintaxe); `bash -n`/`shellcheck` nos blocos `run:` alterados.
- [ ] **T4 — Validation:** dry-run do step em shell local (download+checksum) antes do push; CI verde.
- [ ] **T5 — Done + PR:** DoD + aprovação humana dos workflows → `Status = Done` + PR.

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Security / Infra** | Revisão humana do diff (obrigatória — workflows protegidos); `actionlint`/`shellcheck` limpos; CI verde end-to-end; issues `AaFWWJgNyGpyqZsdZ7yb/yc/yd`, `AaEl440pMT913z7YBs3Y`, `AaGM9MdaFGVBesHTKBxQ` não reportadas no próximo scan. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-vuln-ci-workflows-20261031`, nunca `main`.
- **Workflows:** modificar `.github/workflows/` **somente com revisão humana explícita do diff** (Hard Rule 5 — condição de merge).
- **Security:** checksum pinado por versão; nunca baixar/executar sem verificação; sem `latest`.
- **Scope:** apenas as 5 findings — nenhuma outra alteração em workflows.

## 9. Definition of Done

- [ ] RF-001…RF-004 implementados.
- [ ] Acceptance criteria verificados (CI verde + diff reviewado por humano).
- [ ] SonarCloud não reporta mais as 5 issues no próximo scan.
- [ ] Aprovação humana explícita registrada no PR.

**Next action after DoD is complete:** `Status = Done` + PR com evidência de revisão humana.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Fonte do checksum oficial do actionlint (release notes vs checksums.txt no release) — confirmar em T1.
