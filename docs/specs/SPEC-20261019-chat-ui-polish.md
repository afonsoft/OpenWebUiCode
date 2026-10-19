# SPEC-20261019-chat-ui-polish

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-ui-polish` |
| Type | `Bugfix + Frontend` |
| Stack | `.NET / Blazor WASM` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261019-chat-ui-polish` |
| Ticket | `[A DEFINIR — criada via create-issues após aprovação]` |
| Status | `Delivered` |
| Fonte | relato do usuário (2026-10-19): anel `animate-ping` do avatar "fica muito grande", ícone `thinking-brain.svg` sem identidade, chat abre no topo sem scroll ao fim |

## 1. User Story

**As a** usuário do chat
**I want** o indicador de streaming colado no avatar, um ícone "thinking" com identidade visual (cérebro + engrenagens animadas) e a página do chat abrindo já na última mensagem
**So that** a UI fique polida e eu não precise rolar manualmente a cada chat que abro.

**Problem context:**

1. `MessageBubble.razor:14` usa `absolute inset-0 rounded-full border-2 border-blue-400 dark:border-blue-500 animate-ping` sobre o avatar `size-7` (28px). O keyframe `ping` do Tailwind escala o elemento até **200%** — um anel de 56px que invade o conteúdo ao redor e "não fica somente em volta da imagem".
2. `wwwroot/img/thinking-brain.svg` é um emoji 🧠 com `animateTransform` scale — sem identidade visual, depende do emoji do SO do usuário. O usuário quer um desenho de cérebro + engrenagens (referência: ilustração brain-merged-with-mechanical-gears) com **paleta simples cinza + azul** e **animação** (engrenagens girando).
3. `ChatView.OnParametersSetAsync` carrega `_messages` mas nunca chama `ScrollToEndAsync` — `scrollToEnd` só existe no caminho de reload/stream (`ReloadChatAsync`, `ScrollToEndIfPinnedAsync`). Abrir `/c/{id}` sempre cai no topo.

## 2. Scope

**In scope:**
- RF-001: anel de streaming do avatar — substituir `animate-ping` por animação que permanece colada na borda da imagem (escala máx. ~1.3, fade de opacidade), via keyframe dedicado `avatar-ping` em `tailwind.input.css` + utility gerada.
- RF-002: novo `thinking-brain.svg` — cérebro em cinza neutro + 2 engrenagens azuis girando (SMIL `animateTransform`, sentidos opostos), paleta fixa que funcione em light e dark.
- RF-003: auto-scroll ao abrir chat — `scrollToEnd` incondicional uma única vez após a renderização inicial das mensagens de `/c/{id}` (incluindo o caminho de attach de run ativa).
- Regenerar `wwwroot/css/tailwind.css` e commitar (artefato gerado).

**Out of scope:**
- Scroll do stream (`scrollToEndIfPinned` / `initPinnedScroll`) — já funciona, não tocar.
- Restaurar posição de leitura por chat (decisão: sempre ao fim — paridade upstream).
- Mudanças em `TerminalView`, anel de foco de outros componentes, ou novos keyframes além do `avatar-ping`.
- Botão "voltar ao topo".

## 3. Technical Context

**Where the change happens:**
- `src/OpenWebUI.Client/Components/MessageBubble.razor` — wrapper do avatar (linhas 9-16) e dois usos de `img/thinking-brain.svg` (linhas 23 e 49).
- `src/OpenWebUI.Client/tailwind.input.css` — seção de keyframes junto a `typing-blink`/`thinking-brain-pulse` (~linha 146).
- `src/OpenWebUI.Client/wwwroot/img/thinking-brain.svg` — reescrita do asset.
- `src/OpenWebUI.Client/Components/ChatView.razor` — `OnParametersSetAsync` (~linha 1344) e `OnAfterRenderAsync` (~linha 1053); helpers `ScrollToEndAsync` (~linha 3320) e `openwebui.scrollToEnd` em `wwwroot/js/app.js:11`.
- Skill `tailwind-css` para regeneração do CSS.

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/skills/tailwind-css/SKILL.md`
- `src/OpenWebUI.Client/Components/MessageBubble.razor` (linhas 6-26)
- `src/OpenWebUI.Client/Components/ChatView.razor` (linhas 1053-1116, 1217-1371, 3320-3347)
- `src/OpenWebUI.Client/tailwind.input.css` (linhas 140-195)
- `src/OpenWebUI.Client/wwwroot/js/app.js` (linhas 11-30)
- Referência upstream do avatar streaming (opcional): `git show FETCH_HEAD:src/lib/components/chat/Messages/ResponseMessage.svelte`

**Files to create or modify:**
```text
src/OpenWebUI.Client/Components/MessageBubble.razor      # anel avatar-ping + refs do svg
src/OpenWebUI.Client/tailwind.input.css                  # keyframes/utility avatar-ping
src/OpenWebUI.Client/wwwroot/img/thinking-brain.svg      # novo desenho brain+gears
src/OpenWebUI.Client/Components/ChatView.razor           # flag _needsInitialScroll
src/OpenWebUI.Client/wwwroot/css/tailwind.css            # regenerado (não editar à mão)
```

## 4. Requirements

### RF-001: Anel de streaming colado no avatar
- **Description:** substituir `animate-ping` por classe dedicada (`avatar-ping`) cujo keyframe mantém a borda adjacente ao avatar: `inset: -2px` no span e escala máxima 1.25–1.35 com fade 0.9→0, duração ~1.5s, `cubic-bezier` suave; `border-2` mantido.
- **Rules:** keyframes em `tailwind.input.css` na mesma seção dos indicadores `thinking-*`; `prefers-reduced-motion` já neutraliza via regra global — não adicionar exceção; `aria-hidden="true"` mantido.
- **Input → Output:** streaming=true → anel azul pulsa colado no avatar sem exceder ~36px de diâmetro visual.

### RF-002: Novo `thinking-brain.svg` (brain + gears)
- **Description:** redesenhar o asset: silhueta de cérebro em cinza neutro (ex. `#9ca3af` com stroke `#6b7280`), uma engrenagem azul (`#3b82f6`) maior e uma menor (`#60a5fa`) sobrepostas/ao lado, ambas com `animateTransform` rotate infinito em sentidos opostos (ex. 4s e 2.5s), `transform-origin` no centro de cada engrenagem; `viewBox="0 0 24 24"`, `role="img"`, `aria-label` preservado.
- **Rules:** cores fixas no SVG (carregado via `<img>` — `currentColor` não herda); funcional em fundos light e dark; sem depender de fonte/emoji do SO; SMIL apenas (sem `<script>`).
- **Input → Output:** `img/thinking-brain.svg` renderiza cérebro+gears animado em `size-4` nos dois pontos de uso (nome do modelo streaming + placeholder "thinking").

### RF-003: Auto-scroll ao abrir `/c/{id}`
- **Description:** ao final do carregamento inicial de mensagens em `OnParametersSetAsync` (e do attach de run ativa, quando aplicável), marcar flag `_needsInitialScroll`; em `OnAfterRenderAsync` (após render das bolhas), chamar `openwebui.scrollToEnd` uma vez e limpar a flag. Novo `OnParametersSetAsync` para outro chat re-arma a flag.
- **Rules:** incondicional (`scrollToEnd`, não `scrollToEndIfPinned`) — abrir chat = posição no fim; apenas uma vez por carregamento; reloads silenciosos (`scrollToEnd: false`) e navegação para `/` (chat novo) não alteram comportamento atual; falha de interop degrada sem derrubar a página (try/catch `JSException` no padrão existente).
- **Input → Output:** abrir chat com N mensagens → viewport posicionado na última mensagem sem ação do usuário.

## 5. API Contract

N/A — mudança somente client.

## 6. Acceptance Criteria

- [ ] **Given** uma resposta em streaming **when** o avatar do assistant exibe o anel **then** o anel permanece adjacente à imagem (≤ ~36px de diâmetro), sem invadir o texto ao lado, e desaparece ao fim do stream.
- [ ] **Given** o indicador "thinking" no light e no dark mode **when** renderizado **then** exibe cérebro cinza + engrenagens azuis girando em sentidos opostos, sem emoji do SO.
- [ ] **Given** um chat existente com mensagens além da altura da tela **when** o usuário abre `/c/{id}` **then** a view posiciona na última mensagem automaticamente.
- [ ] **Given** um chat aberto **when** o usuário sobe o scroll e uma reload silenciosa acontece **then** a posição não é arrastada (comportamento `scrollToEnd: false` preservado).
- [ ] **Given** `prefers-reduced-motion` ativo **when** streaming **then** o anel e o SVG não animam (estado visível e estático).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Chat de 1 mensagem | abrir `/c/{id}` | scroll no fim sem jump visível (altura < viewport) |
| Run ativa ao abrir | `AttachActiveRunAsync` | mesma rolagem ao fim após attach |
| Interop JS indisponível | `scrollToEnd` lança `JSException` | página renderiza normal, erro logado |
| Navegar `/c/a` → `/c/b` | `OnParametersSetAsync` | scroll ao fim re-executa para o novo chat |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler arquivos da seção 3; confirmar seletor/altura do wrapper do avatar e o ponto exato de `_messages.AddRange(chat.Messages)` + attach de run.
- [ ] **T2 — Anel:** adicionar `@keyframes avatar-ping` + utility em `tailwind.input.css`; trocar classes do span em `MessageBubble.razor` (`-inset-0.5 avatar-ping`); regenerar `tailwind.css`.
- [ ] **T3 — SVG:** reescrever `thinking-brain.svg` (brain cinza + 2 gears azuis, SMIL rotate opostos); validar render em `size-4`.
- [ ] **T4 — Scroll:** implementar `_needsInitialScroll` (set após `_messages.AddRange` e após `AttachActiveRunAsync` quando era carga inicial; consumo em `OnAfterRenderAsync` com `scrollToEnd` + try/catch `JSException`).
- [ ] **T5 — Validation:** `dotnet build`; `python3 tools/check-css-classes.py`; verificação manual nos 3 cenários (anel, ícone, scroll) em light/dark; preencher DoD.
- [ ] **T6 — Done + PR:** `Status = Done`, mover para `docs/specs/`, PR na branch.

**7.1 Validation strategy**

| Item | Evidence |
| --- | --- |
| Build | `dotnet build OpenWebUI.slnx -c Release` sem erros |
| CSS | `tools/check-css-classes.py` passa (classe nova definida) |
| i18n | `tools/check-i18n-parity.py` (se novas chaves — esperado: nenhuma) |
| Manual | screenshots/descrição dos 3 cenários light+dark |

## 8. Organization Guardrails

- Não editar `wwwroot/css/tailwind.css` manualmente — regenerar via skill `tailwind-css`.
- Não tocar `.github/workflows/`.
- Sem commit direto em `main`; branch `feature/devin-20261019-chat-ui-polish`.
- Manter paridade visual com upstream quando a mudança não for divergência deliberada (anel e ícone são divergências aprovadas pelo usuário).

## 9. Definition of Done

- [ ] RF-001/002/003 implementados e verificados em light+dark.
- [ ] `tailwind.css` regenerado e commitado.
- [ ] `tools/check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`, `check-spec-status.py` verdes.
- [ ] `dotnet build` verde; testes existentes passam (`dotnet test tests/OpenWebUI.Api.Tests` — esperado sem regressão).
- [ ] SPEC arquivada em `docs/specs/` com `Status: Done` + `Delivered` (PR).
