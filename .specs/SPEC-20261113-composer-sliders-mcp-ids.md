# SPEC-20261113-composer-sliders-mcp-ids

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `composer-sliders-mcp-ids` |
| Type | `Frontend` |
| Stack | `.NET 10 / Blazor WebAssembly / Tailwind CSS` |
| Repository | `OpenWebUiCode` |
| Branch | `feature/Devin-20261113-composer-sliders-mcp-ids` |
| Ticket | `GH#727` |
| Status | `Draft` |

## 1. User Story

**As a** usuário e desenvolvedor do Open WebUI  
**I want** uma barra de ações do chat composer limpa e moderna, com controle unificado de capacidades ('sliders-up'), opções de mídia (imagem/vídeo) agrupadas, busca na web ativada por padrão, submenu em cascata à direita para servidores e ferramentas MCP (inspirado no Devin Web), e atributos `id` em todos os elementos HTML  
**So that** a experiência de configuração rápida de recursos seja intuitiva, o composer permaneça visualmente despoluído e todos os elementos estejam preparados para automação e testes e2e de ponta a ponta.

**Problem context:**  
Atualmente, em `src/OpenWebUI.Client/Components/ChatView.razor`, a barra de ações do composer (`ml-1 self-end flex items-center flex-1 min-w-0`) possui ícones soltos e redundantes (ícone separado para imagem, ícone separado para vídeo, ícone separado para web search). A busca na web inicia desabilitada por padrão, exigindo ativação manual repetitiva em cada novo chat. Além disso, os servidores e ferramentas MCP não possuem um acesso rápido contextual na área de composição (ficando restritos a modais profundos ou configurações de admin), e múltiplos elementos HTML não possuem atributos `id` semânticos, dificultando a automação de testes com QA agents, Playwright ou scripts de acessibilidade.

## 2. Scope

### In Scope
1. **Ícone Sliders-Up**: Criação do botão central de capacidades de runtime com ícone de sliders na toolbar do composer.
2. **Web Search Default**: Habilitação automática da busca na web por default (`_webSearch = true`) ao abrir novas conversas no chat (quando o recurso estiver habilitado no backend).
3. **Unificação de Mídia**: Agrupamento dos controles de 'imagem' e 'vídeo' em uma única opção contextual de geração de mídia no menu de sliders.
4. **Submenu MCP em Cascata à Direita (estilo Devin Web)**:
   - Nível 1: Exibição dos servidores MCP configurados no sistema com badge de ferramentas e indicador chevron `>`.
   - Nível 2 (Flyout à Direita): Exibição das ferramentas específicas do servidor MCP sob hover/clique, com toggles/checkboxes individuais refletindo diretamente em `_toolIds` e ações em lote ("Habilitar todas" / "Desabilitar todas").
5. **IDs em Todos os Elementos HTML**: Atribuição de identificadores `id` únicos, kebab-case e semânticos a todos os elementos da barra de ferramentas do composer, modais, popovers e controles interativos.
6. **Internacionalização (i18n)**: Adição e paridade das strings nos 8 idiomas suportados.

### Out of Scope
- Alterações em APIs REST de backend ou novos endpoints de MCP (os contratos e serviços existentes `GetMcpServersAsync` e `GetMcpToolsAsync` são suficientes).
- Alterações no executor de LLM ou nos providers de inferência.
- Modificações no layout de páginas de administração ou workspaces fora do chat view.

## 3. Technical Context

### Onde a mudança acontece
- Frontend Blazor WASM: `src/OpenWebUI.Client/Components/ChatView.razor`
- Recursos de localização: `src/OpenWebUI.Client/wwwroot/i18n/*.json` (8 arquivos)

### Arquivos a ler
- `src/OpenWebUI.Client/Components/ChatView.razor` (composer toolbar, linhas 640-850 e 4650-4850)
- `src/OpenWebUI.Application/Contracts/McpContracts.cs` (`McpServerResponse`, `McpToolResponse`)
- `src/OpenWebUI.Client/Services/ApiService.cs` (`GetMcpServersAsync`, `GetMcpToolsAsync`)

### Arquivos a criar / modificar
- `src/OpenWebUI.Client/Components/ChatView.razor` (atualização da barra, novo menu sliders e flyouts cascata, atribuição de IDs)
- `src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json` e demais 7 locales (novas traduções)
- `.specs/SPEC-20261113-composer-sliders-mcp-ids.md` (esta especificação)
- `plans/2026-11-13-composer-sliders-mcp-ids.md` (plano de execução)

## 4. Requirements

### RF-001: Atributos ID em Todos os Elementos do Composer
- **Description:** Todos os elementos HTML estruturais e interativos da barra de composer e seus popovers associados devem possuir atributo `id` semântico e determinístico.
- **Rules:**
  - O container pai do composer deve conter `id="chat-composer-container"`.
  - A textarea deve conter `id="chat-composer-textarea"`.
  - A barra inferior de ferramentas deve conter `id="chat-composer-toolbar"`.
  - O grupo esquerdo deve conter `id="chat-composer-toolbar-left"`.
  - O botão de sliders deve conter `id="composer-btn-sliders-trigger"`.
  - Botões de anexos devem conter `id="composer-btn-attach-file"` e `id="composer-btn-attach-dir"`.
  - Botão de voz: `id="composer-btn-voice"`.
  - Botão de modo: `id="composer-btn-mode-trigger"`.
  - Botão de ferramentas: `id="composer-btn-tools-trigger"`.
  - Botões de envio/cancelamento/pausa: `id="chat-composer-send-btn"`, `id="chat-composer-stop-btn"`, `id="chat-composer-pause-btn"`, `id="chat-composer-resume-btn"`.
  - Todos os itens do menu sliders e submenus de MCP devem conter prefixo `composer-sliders-*`.
- **Input → Output:** Renderização do DOM → Todos os nós inspecionados possuem atributo `id` único e não vazio.

### RF-002: Ícone Central 'Sliders-Up' e Popover de Capacidades
- **Description:** Criar botão com ícone de sliders na barra do composer que abre um popover elegante acima do botão, reunindo as capacidades de execução (Web, Mídia e MCP).
- **Rules:**
  - Posicionado na toolbar à esquerda com `id="composer-btn-sliders-trigger"`.
  - Exibe indicador visual de estado ativo (destaque de cor) se qualquer recurso (web search, mídia ou MCP) estiver ativo.
  - Ao clicar, abre/fecha o popover `composer-sliders-popover`.
  - Clicar fora ou pressionar Escape fecha o popover e quaisquer submenus abertos.

### RF-003: Busca na Web Habilitada por Padrão
- **Description:** A opção de busca na web deve iniciar ativada (`_webSearch = true`) por padrão para todas as novas sessões de chat.
- **Rules:**
  - Ao inicializar uma nova conversa (`ChatId is null`), se `_webSearchAvailable == true`, `_webSearch` é inicializado como `true`.
  - O estado deve ser exibido e alternável dentro do popover 'sliders-up' via toggle switch `composer-sliders-toggle-websearch`.
  - Se o usuário explicitamente desligar o recurso na sessão, seu estado deve ser respeitado para as próximas mensagens daquela sessão.

### RF-004: Unificação dos Controles de Imagem e Vídeo em Mídia
- **Description:** Os botões independentes de imagem e vídeo são removidos da barra principal e integrados em uma seção unificada de "Geração de Mídia" dentro do popover 'sliders-up'.
- **Rules:**
  - No popover de sliders, a seção "Mídia" exibe opções claras para geração de imagens (`image`) e vídeos (`video`).
  - Cada opção possui toggle individual para ativar/desativar as categorias correspondentes em `_toolIds` via `ToggleCategoryAsync("image")` e `ToggleCategoryAsync("video")`.
  - Ícone combinado ou badge de mídia ativa é exibido quando pelo menos uma opção multimídia estiver selecionada.

### RF-005: Submenu em Cascata para Servidores e Ferramentas MCP (Flyout à Direita)
- **Description:** No menu 'sliders-up', a opção "Servidores MCP" deve abrir um submenu flutuante posicionado à sua direita listando os servidores MCP, e cada servidor deve abrir um segundo flyout à direita com suas ferramentas disponíveis.
- **Rules:**
  - O item MCP exibe ícone de MCP, contador de servidores ou tools ativas e um chevron direito `>`.
  - **Submenu Nível 1 (Servidores MCP):** Posicionado à direita (`left-full top-0 ml-1.5`). Lista todos os servidores retornados por `_mcps`. Mostra status, nome do servidor e quantidade de ferramentas disponíveis.
  - **Submenu Nível 2 (Ferramentas MCP):** Ao passar o mouse ou clicar em um servidor MCP, abre um segundo painel flutuante à direita listando as ferramentas do servidor (`McpToolResponse`).
  - As ferramentas são carregadas sob demanda e armazenadas em cache local (`_mcpToolsCache`) para evitar chamadas repetidas à API.
  - Cada ferramenta exibe checkbox de ativação individual sincronizado com `_toolIds`.
  - O cabeçalho do nível 2 fornece botões rápidos "Habilitar todas" e "Desabilitar todas" para aquele servidor.
  - Detecção de limites da tela (viewport boundary check): se não houver espaço à direita na viewport, o submenu deve abrir à esquerda (`right-full mr-1.5`) para não transbordar.

### Business Rules / Invariants
- Nenhuma ferramenta que exija configuração prévia de backend pode ser ativada sem validação.
- O fechamento do menu principal de sliders fecha em cascata todos os submenus de MCP abertos.
- IDs no HTML devem ser únicos na página para evitar colisões no DOM.

## 5. API Contract

A funcionalidade reutiliza contratos já existentes da API sem necessidade de novas rotas no backend:
- `GET /api/v1/mcp/servers` → `List<McpServerResponse>`
- `GET /api/v1/mcp/servers/{serverId}/tools` → `List<McpToolResponse>`
- `POST /api/v1/chats/{chatId}/messages` → Envia payload com `WebSearch: bool` e `ToolIds: List<string>`.

## 6. Acceptance Criteria

### Cenário 1: Web Search habilitado por default em nova conversa
- **Given** que o usuário abre uma nova conversa no chat
- **And** o recurso de busca na web está configurado e disponível no backend
- **When** o componente de chat é carregado
- **Then** a variável `_webSearch` deve ser `true`
- **And** o toggle de busca na web no menu sliders-up deve refletir o estado ativado.

### Cenário 2: Visualização e navegação no menu Sliders-Up
- **Given** que o usuário está no composer do chat
- **When** clica no botão com ícone de sliders (`#composer-btn-sliders-trigger`)
- **Then** o popover `#composer-sliders-popover` deve ser exibido acima da toolbar
- **And** deve exibir os itens de Busca na Web, Mídia e Servidores MCP.

### Cenário 3: Submenu MCP em cascata à direita (estilo Devin Web)
- **Given** que o popover sliders-up está aberto e existem servidores MCP cadastrados
- **When** o usuário passa o cursor ou clica no item "Servidores MCP"
- **Then** um submenu flutuante `#composer-sliders-mcp-servers-flyout` deve abrir alinhado à direita do item
- **And** ao selecionar um servidor MCP, um segundo flyout `#composer-sliders-mcp-tools-flyout-{serverId}` deve abrir à direita exibindo suas ferramentas com checkboxes.

### Cenário 4: Alternância de ferramentas MCP no chat
- **Given** que o flyout de ferramentas de um servidor MCP está aberto
- **When** o usuário marca o checkbox de uma ferramenta MCP
- **Then** o identificador da ferramenta é adicionado à lista `_toolIds` da sessão
- **And** o badge do botão sliders-up indica que ferramentas adicionais estão ativas.

### Cenário 5: Verificação de IDs em todos os elementos
- **Given** a renderização completa do composer de chat
- **When** o DOM é inspecionado
- **Then** todos os botões de ação, inputs, menus e toggles devem conter atributo `id` semântico conforme a convenção especificada.

### Edge Cases
| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Nenhum servidor MCP cadastrado | Usuário abre item MCP | Exibe mensagem indicando nenhum servidor MCP disponível com link para configuração |
| Falha de rede ao carregar tools do MCP | Hover em servidor MCP indisponível | Exibe indicador de erro amigável sem quebrar o componente do chat |
| Viewport estreita (Mobile / tela pequena) | Submenu cascata não cabe à direita | Submenu abre à esquerda ou expande como acordeão vertical |
| Web search desabilitado no servidor | `_webSearchAvailable == false` | Toggle de busca na web aparece desabilitado com tooltip explicativo |

## 7. Task Plan

- **T1: Discovery e setup da branch** (Concluído) — Validação de baseline, leitura de contratos MCP e criação da branch `feature/Devin-20261113-composer-sliders-mcp-ids`.
- **T2: Implementação da atribuição de IDs HTML** — Atualizar `ChatView.razor` aplicando atributos `id` em todos os elementos da toolbar e formulário do composer.
- **T3: Ajuste de Web Search Default e Unificação de Mídia** — Modificar lógica de inicialização de `_webSearch` e agrupar controles de imagem e vídeo.
- **T4: Construção do componente / popover Sliders-Up com Flyout MCP Cascata** — Implementar botão sliders-up, flyout de servidores MCP e flyout de ferramentas com cache local e integração com `_toolIds`.
- **T5: Suporte a i18n nos 8 locales** — Adicionar as chaves de tradução em `src/OpenWebUI.Client/wwwroot/i18n/*.json`.
- **T6: Validação e Testes** — Execução de build (`dotnet build`), verificação de estilos e testes de regressão.
- **T7: Finalização e Commit** — Consolidação dos commits na branch e abertura de PR.

## 8. Organization Guardrails

- Não commitar diretamente em `main`, `master` ou `develop`.
- Não alterar arquivos de workflow CI (`.github/workflows/`).
- Não commitar arquivos de ambiente, segredos ou bases SQLite (`.env`, `*.db`).
- Preservar as camadas arquiteturais: `Client` comunica com o backend via contratos em `Application`.
- Garantir acessibilidade (atributos `aria-label`, `aria-expanded`, navegação por teclado).

## 9. Definition of Done

- [ ] Branch dedicada criada baseada na `main`.
- [ ] Atributos `id` presentes em todos os elementos da toolbar e composer.
- [ ] Ícones de imagem e vídeo unificados em seção de mídia do menu sliders.
- [ ] Busca na web ativada por default em novos chats quando disponível.
- [ ] Ícone sliders-up funcional com popover e submenu cascata para a direita para servidores e tools MCP.
- [ ] Sincronização de seleção de tools do MCP com `_toolIds`.
- [ ] Paridade de chaves i18n nos 8 locales.
- [ ] Build do projeto executa com 0 erros e 0 warnings (`dotnet build OpenWebUI.slnx`).
- [ ] SPEC SDD e plano de implementação comitados na branch.

## Open Questions / Pending Ambiguity

- **Q1:** *Responsividade em dispositivos móveis*: Em telas com largura inferior a 640px (smartphones), os submenus em cascata para a direita devem alternar automaticamente para visualização tipo acordeão empilhado verticalmente para evitar que os submenus saiam da tela. Decisão default: adotar classe Tailwind `sm:left-full` e modo stacked em mobile.
