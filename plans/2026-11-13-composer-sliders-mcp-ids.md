# Plano: Composer Sliders-Up, MCP Cascade e IDs HTML

## Summary
Modernização da barra de ações do chat composer (`ml-1 self-end flex items-center flex-1 min-w-0` em `src/OpenWebUI.Client/Components/ChatView.razor`) inspirada na interface do **Devin Web**:
1. Introdução de um controle central de capacidades com ícone **'sliders-up'** (ajustes e capacidades de runtime).
2. Unificação dos ícones individuais de 'imagem' e 'vídeo' em uma única opção de mídia integrada.
3. Habilitação padrão de 'Buscar na Web' (`_webSearch = true` por default para novas conversas quando o recurso estiver disponível).
4. Menu suspenso em cascata (flyout para a direita) para servidores MCP configurados:
   - Submenu nível 1 (à direita): lista de servidores MCP configurados com status e contagem de ferramentas.
   - Submenu nível 2 (à direita): lista de ferramentas do servidor MCP selecionado com toggles individuais integrados ao `_toolIds` do chat.
5. Inclusão determinística e semântica de atributos `id` em todos os elementos HTML da barra de composer e seus menus para automação e testes e2e.

## Implementation Steps
### Fase 1: Arquitetura e Estrutura de Dados
- Passo 1.1: Cache e estado de MCPs no Client: Adicionar estrutura de cache de ferramentas MCP por servidor (`Dictionary<string, List<McpToolResponse>> _mcpToolsCache`) e controle de estado do flyout em `ChatView.razor`.
- Passo 1.2: Default de Web Search: Ajustar `_webSearch` para inicializar como `true` por padrão em novas sessões de chat (`ChatId is null`) quando `_webSearchAvailable` for verdadeiro.

### Fase 2: Estrutura HTML e Atribuição de IDs
- Passo 2.1: Inclusão de atributos `id` em todos os elementos HTML da barra de ferramentas do composer:
  - `chat-composer-container`
  - `chat-composer-textarea`
  - `chat-composer-toolbar`
  - `chat-composer-toolbar-left`
  - `composer-btn-sliders-trigger`
  - `composer-btn-attach-file`
  - `composer-btn-attach-dir`
  - `composer-input-file-hidden`
  - `composer-input-dir-hidden`
  - `composer-btn-voice`
  - `composer-btn-mode-trigger`
  - `composer-btn-tools-trigger`
  - `composer-btn-send`, `composer-btn-stop`, `composer-btn-pause`, `composer-btn-resume`.

### Fase 3: Criação do Menu Sliders-Up e Flyout de MCPs
- Passo 3.1: Botão Sliders-Up com ícone SVG estilizado e indicador de estado ativo.
- Passo 3.2: Popover Principal Sliders-Up com toggles de Busca na Web, Mídia (Imagem e Vídeo) e item Servidores MCP.
- Passo 3.3: Submenu Cascata Nível 1: lista de servidores MCP com status e indicador `>`.
- Passo 3.4: Submenu Cascata Nível 2: lista de ferramentas do servidor MCP selecionado com checkboxes vinculados a `_toolIds`.

### Fase 4: i18n e Estilos
- Passo 4.1: Chaves de tradução nos 8 locales JSON para os novos textos.
- Passo 4.2: Estilização Tailwind e responsividade para menus em cascata.

### Fase 5: Verificação e Validação
- Passo 5.1: Build `dotnet build OpenWebUI.slnx` garantindo 0 erros e 0 warnings.
- Passo 5.2: Verificação do comportamento visual e de acessibilidade.

## Files to Modify
- `src/OpenWebUI.Client/Components/ChatView.razor`
- `src/OpenWebUI.Client/wwwroot/i18n/*.json`
- `.specs/SPEC-20261113-composer-sliders-mcp-ids.md`

## Verification
- Compilação limpa do projeto Client: `dotnet build src/OpenWebUI.Client/OpenWebUI.Client.csproj`
- Validação estática de tags HTML e unicidade de IDs.
- Testes automatizados da API: `dotnet test tests/OpenWebUI.Api.Tests`

## Risks
- Viewport overflow com flyout para a direita em telas mobile (resolvido com posicionamento dinâmico).
- Latência de chamada de tools de MCP (resolvido com cache em memória `_mcpToolsCache`).

## Open Questions
- Nenhuma dúvida aberta.
