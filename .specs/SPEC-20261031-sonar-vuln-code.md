# SPEC-20261031-sonar-vuln-code

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-vuln-code` |
| Type | `Security` |
| Stack | `.NET` + Docker |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-vuln-code-20261031` |
| Ticket | `GH#563` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** corrigir ou triar as 6 VULNERABILITY + 1 code smell de segurança reportadas pelo SonarCloud no código e no Dockerfile
**So that** a superfície de segurança fica limpa e cada finding tem resolução documentada (fix real ou won't-fix justificado).

**Problem context:**
SonarCloud `afonsoft_open-webui` reporta em código de produto:

| Issue | Regra | Arquivo:linha | Achado | Leitura prévia |
| --- | --- | --- | --- | --- |
| `AaEl44UaMT913z7YBshL` | roslyn.security.cs:**S7044** MAJOR | `Client/Services/AuthService.cs:170` | "API Traversal via unsanitized user input in `CreateRequest()`" | Código **já valida** `uri` (deve começar com `/`, rejeita `//` e segmentos `..`). Lacuna plausível: `..` **URL-encoded** (`%2e%2e`), backslash, ou schemes. Triar e endurecer. |
| `AaEl44kBMT913z7YBs3Y` | csharpsquid:**S7039** MAJOR | `Api/Program.cs:448` | "CSP should be restrictive" | Middleware injeta `ContentSecurityPolicy` em HTML fora de `/preview/`. Revisar a policy `ContentSecurityPolicy` (provável `unsafe-inline`/`unsafe-eval`/`data:`/`http:` permissivos). |
| `AaF1CyI6Xd_g6jxuVz9L` | csharpsquid:**S5693** MAJOR | `Client/Components/ChatView.razor:2213` | "Limit the content length of HTTP requests" | `UploadBrowserFileAsync` já passa `maxAllowedSize: MaxUploadBytes` no `OpenReadStream`; o finding mira o request HTTP (`Api.UploadFileAsync`) — confirmar cap server-side (`[RequestSizeLimit]`/Kestrel) ou justificar. |
| `AaF19n5KYghEOE8baxtm` | csharpsquid:**S4036** MINOR | `Infrastructure/Services/ReferenceService.cs:324` | "Use an absolute path for this command" | `new ProcessStartInfo("git")` depende de PATH — resolver path absoluto do git no start (fallback `which git`/`/usr/bin/git`) ou justificar container controlado. |
| `AaEl44jiMT913z7YBs3Y` | csharpsquid:**S5332** MINOR | `Api/Endpoints/PreviewEndpoints.cs:298` | "Using http protocol is insecure" | Rewrite de `Location` para preview de portas `127.0.0.1`/`localhost` — http loopback intencional (preview local); provável won't-fix justificado ou whitelist explícita. |
| `AaEl443JMT913z7YBtIK` | docker:**S6471** MINOR | `Dockerfile:138` | "Default user root might be unsafe" | `USER root` **intencional** — comentário no Dockerfile: agente `shell_exec`/jobs precisa root (apt, paths, sudo). Won't-fix com justificativa formal (aceita no SonarCloud ou supressão documentada). |
| `AaFNGVYoK8wdZAVl2Tvk` | docker:**S7031** MINOR (smell) | `Dockerfile:70` | "Merge consecutive RUN" | Dois `RUN` seguidos na instalação do `gh` — merge trivial. |

## 2. Scope

**In scope:** os 7 findings acima — fix real onde o risco é genuíno (S7044, S7039, S4036, S7031) e resolução formal documentada onde o design é intencional (S6471, S5332) ou já mitigado (S5693 — confirmar cap server-side antes).

**Out of scope:**
- `.github/workflows/` (SPEC-20261031-sonar-vuln-ci-workflows).
- FPs de i18n `json:S2068` (SPEC-20261031-sonar-vuln-fp-triage).
- Hardening geral de CSP/headers fora do finding (nova SPEC se surgir).

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/Services/AuthService.cs` (`CreateRequest` ~:160-175), `src/OpenWebUI.Api/Program.cs` (CSP middleware ~:435-455 + constante `ContentSecurityPolicy`), `src/OpenWebUI.Client/Components/ChatView.razor` (`UploadBrowserFileAsync` ~:2211) + upload endpoint server-side, `src/OpenWebUI.Infrastructure/Services/ReferenceService.cs` (`RunGitAsync` ~:321-335), `src/OpenWebUI.Api/Endpoints/PreviewEndpoints.cs` (~:290-305), `Dockerfile` (:65-72, :135-140).

**Files to read before implementing:**
- `CLAUDE.md` (guardrails, Soft Rule 1 — Dockerfile pede confirmação do usuário)
- `AuthService.cs` completo (callers de `CreateRequest` — origem do `uri`)
- `Program.cs` — definição atual de `ContentSecurityPolicy`
- Endpoints de upload (`Files`/`AttachmentEndpoints`) — limites de request atuais
- `Dockerfile` completo

**Files to create or modify:**
```text
src/OpenWebUI.Client/Services/AuthService.cs
src/OpenWebUI.Api/Program.cs
src/OpenWebUI.Infrastructure/Services/ReferenceService.cs
src/OpenWebUI.Api/Endpoints/PreviewEndpoints.cs   # apenas comentário/whitelist se won't-fix
Dockerfile                                        # merge RUN (S7031); comentário de justificativa S6471
tests/OpenWebUI.Api.Tests/*.cs                    # testes dos fixes reais
```

## 4. Requirements

### RF-001: S7044 — hardening do `CreateRequest` (ou won't-fix evidenciado)
- **Description:** revisar a validação de `uri` contra bypasses: `..` URL-encoded (`%2e%2e`, `%2E`), backslash (`..\`), `/@`, URI absoluta disfarçada, segmento vazio. Se a validação atual já cobre todos os casos → resolver no SonarCloud como "Reviewed/Safe" com justificativa no código; se houver bypass → corrigir (decodificar antes de validar, whitelist de path).
- **Input → Output:** `CreateRequest(method, "../admin")`, `"/%2e%2e/x"`, `"/a\\..\\b"`, `"//evil"` → `ArgumentException` (ou won't-fix documentado).

### RF-002: S7039 — CSP restritiva
- **Description:** revisar `ContentSecurityPolicy` em `Program.cs` e eliminar diretivas fracas (`unsafe-eval`, `unsafe-inline` onde evitável, `*`, `http:`/`data:` indiscriminados) mantendo o boot WASM funcional (Blazor precisa de `wasm-unsafe-eval`/`script-src` compatível — usar o mínimo necessário documentado).
- **Input → Output:** header CSP emitido em HTML (fora `/preview/`) → policy restritiva revisada; app continua funcional.

### RF-003: S5693 — cap de request confirmado ou aplicado
- **Description:** confirmar que o endpoint de upload limita o corpo (Kestrel `MaxRequestBodySize`, `[RequestSizeLimit]`, ou streaming com cap). Se o cap já existe → won't-fix justificado; se não → aplicar limite coerente com `MaxUploadBytes` do cliente.

### RF-004: S4036 — path absoluto do git
- **Description:** `RunGitAsync` resolve o executável `git` por path absoluto na criação do `ProcessStartInfo` (probe `PATH` uma vez, cacheado; fallback erro claro) — remove a dependência de resolução por PATH em runtime.

### RF-005: S5332 — http loopback triado
- **Description:** justificar formalmente (o rewrite só cobre `127.0.0.1`/`localhost`/`[::1]` — preview interno) via won't-fix no SonarCloud + comentário no código; ou trocar para match explícito por scheme allowlist.

### RF-006: S6471 — `USER root` won't-fix documentado
- **Description:** decisão de design registrada (agente precisa root para `shell_exec`/jobs — já comentado no Dockerfile): resolver como "Reviewed" no SonarCloud e referenciar a justificativa; nenhuma mudança funcional.

### RF-007: S7031 — merge de RUN consecutivos
- **Description:** unir os `RUN` consecutivos do bloco do `gh` (Dockerfile ~:69-72) preservando o comportamento; requer confirmação do usuário por tocar o Dockerfile (Soft Rule 1).

**Business rules / invariants:**
- Won't-fix nunca é silencioso: cada finding encerrado sem código carrega justificativa escrita (SPEC + SonarCloud + comentário onde couber).
- Nenhuma regressão funcional: upload, preview, git references, CSP/boot WASM.

## 5. API Contract

N/A — possível endurecimento de validação de `uri` em `CreateRequest` (interno; sem mudança de contrato HTTP).

## 6. Acceptance Criteria

- [ ] **Given** `CreateRequest` com inputs maliciosos (`%2e%2e`, backslash, `//host`), **when** chamado, **then** todo bypass conhecido é rejeitado (ou finding resolvido como reviewed com prova de cobertura).
- [ ] **Given** a página HTML da SPA, **when** servida, **then** o header CSP não contém `unsafe-eval`/wildcard fora do estritamente necessário ao WASM — e o app boota.
- [ ] **Given** upload de arquivo > cap, **when** postado, **then** servidor rejeita (413) ou finding é won't-fix documentado.
- [ ] **Given** `RunGitAsync`, **when** executa, **then** usa path absoluto do git.
- [ ] **Given** o board do SonarCloud, **when** o próximo scan roda, **then** as 7 issues constam resolvidas (fixed ou won't-fix justificado).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Encoded traversal | `/%2e%2e/secret` | rejeitado |
| WASM sem unsafe-eval | CSP restrita | boot continua OK (validar `wasm-unsafe-eval` se exigido) |
| git ausente do PATH | container minimal | erro explícito e legível (não crash obscuro) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler os 6 pontos + callers; confirmar cap de upload server-side e o valor atual de `ContentSecurityPolicy`; checar callers de `CreateRequest`.
- [ ] **T2 — Test first (RED):** testes para RF-001 (bypasses) e RF-004 (path absoluto mockável) onde testável.
- [ ] **T3 — Implementation (GREEN):** fixes RF-001/002/003/004/007 + comentários de won't-fix RF-005/006.
- [ ] **T4 — Verification:** `dotnet test` verde; `docker build` do Dockerfile alterado; resolução das issues no SonarCloud pós-merge.
- [ ] **T5 — Done + PR:** DoD → `Status = Done` + PR.

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Security / .NET+Docker** | Testes dos fixes reais; suite verde; build Docker ok; cada finding com resolução formal no SonarCloud (fixed/won't-fix+justificativa). |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-vuln-code-20261031`.
- **Dockerfile:** alteração (RF-007) exige confirmação do usuário antes (Soft Rule 1).
- **Security:** won't-fix sempre com justificativa escrita; nenhum secret/token em código ou log.
- **Scope:** só as 7 findings — refactors de CSP/header além disso ficam para outra SPEC.

## 9. Definition of Done

- [ ] RF-001…RF-007 resolvidos (fix ou won't-fix formal).
- [ ] Acceptance criteria cobertos.
- [ ] Build + testes verdes; Docker build ok.
- [ ] Issues encerradas/triadas no SonarCloud no próximo scan.

**Next action after DoD is complete:** `Status = Done` + PR na branch `fix/sonar-vuln-code-20261031`.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Valor atual exato de `ContentSecurityPolicy` e baseline CSP do WASM (confirmar em T1 — pode exigir `wasm-unsafe-eval`, que é aceitável e distinto de `unsafe-eval`).
- `[A DEFINIR]` Confirmação do usuário para tocar `Dockerfile` (RF-007) — Soft Rule 1.
