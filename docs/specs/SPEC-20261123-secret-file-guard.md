# SPEC-20261123-secret-file-guard — guarda de arquivos secretos nas builtin tools

**Status**: Completed

## Motivação

Portado do Devin CLI (*"… is a secret file or matched by an ignore file. The
drafter cannot read it"* + *"carries a literal credential"*): tools de arquivo
recusam ler/escrever caminhos com padrão de segredo, e há detecção de
credencial literal trafegando pelo agente.

Hoje a regra "não commitar secrets" existe **só no system prompt** — nada no
runtime impede o agente de ler `.env` e vazar seu conteúdo no transcript, ou
de escrever `appsettings.json` com a chave em claro.

## Design

- Novo `SecretPathGuard` (Infrastructure/ChatTools) consultado por `read`,
  `write`, `edit`, `patch`, `notebook_edit` e `shell_exec` (quando o comando
  referencia o path — checagem best-effort por tokenização do comando).
- Padrões bloqueados para **escrita** (fail closed — escrever segredo é o
  risco real): `**/.env`, `**/.env.*` (exceto `.env.exemplo`), `**/*.pem`,
  `**/*.key`, `**/id_rsa*`, `**/credentials*`, `**/secrets*.json`,
  `**/webui.db`/`*.sqlite*` (databases — já proibido por regra).
- **Leitura**: `.env`, `*.pem`, `*.key`, `id_rsa*`, `credentials*` →
  bloqueada com mensagem "secret file — cannot read". Arquivos de exemplo
  (`.env.exemplo`, `*-example*`) passam.
- `git`/commit path: `shell_exec` com `git add -A`/`git commit` → o guard
  verifica os arquivos staged via `git diff --cached --name-only` antes de
  liberar (hook pré-exec para comandos `git commit*`). Se staged contém
  padrão secreto → bloqueia e explica.
- **Detector de credencial literal** (leve, regex): tool args e output são
  varridos por `sk-[a-zA-Z0-9]{20,}`, `ghp_`, `AKIA[0-9A-Z]{16}`,
  `-----BEGIN.*PRIVATE KEY-----`, `eyJhbGci` (JWT longo). Match em **args**
  → o resultado da tool anexa aviso `"contains a credential-like literal —
  do not persist"` (não bloqueia: pode ser legítimo, ex. testar auth).
- Bypass documentado: `secrets:allow_paths` na config do chat para exceções
  explícitas; nenhum bypass implícito.

## Testes

- `Guard_Write_Env_Bloqueia` — `write` em `.env` recusa; `.env.exemplo` ok.
- `Guard_Read_Pem_Bloqueia` — `read` de `*.pem` recusa com mensagem clara.
- `Guard_GitCommit_StagedSecret_Bloqueia` — stage com `.env` → `git commit`
  bloqueado até unstage.
- `Guard_LiteralCredential_Avisa` — args contendo `sk-…` → aviso no result.
- `Guard_SemPadrao_NaoInterfere` — paths normais inalterados.

## Delivered

Implementado em feature/agent-20261123-devin-parity — ver commit da
branch e testes dedicados em tests/OpenWebUI.Api.Tests.
