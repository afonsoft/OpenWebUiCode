# Funcionalidades

Status detalhado de paridade com o upstream: ver `docs/MIGRACAO-DOTNET.md`.

## Implementado

- **Auth**: registro/login JWT, chaves de API `sk-*`, primeiro usuário = admin, página de auth fiel ao upstream.
- **Chat**: conversas com streaming (SSE), anexos, avaliação 👍/👎, edição e regeneração por mensagem, follow-ups, autocomplete de prompts, compartilhamento público (`/s/{id}`).
- **Sidebar**: fixados, pastas, menu de contexto por chat, busca, seções, rail recolhida, menu do usuário.
- **Modelos**: seletor dropdown agrupado por provider; modelos custom; conexões Ollama/OpenAI gerenciadas ou semeadas por env.
- **Usuários**: perfil, senha, chaves; admin de usuários (`/admin`).
- **Workspace**: prompts, modelos custom, arquivos (`/workspace`).
- **Notas** (`/notes`), **Memórias**, **Arquivados** (`/archived`).
- **Tarefas**: geração de título, follow-ups e tags via LLM (`/api/v1/tasks`).
- **IDE/LSP**: diagnósticos (squiggles + painel Problems), hover e símbolos via language servers por workdir — `Lsp:*` config (defaults csharp-ls, typescript-language-server, pylsp, vscode-json-languageserver; binários ausentes degradam para `unavailable`).
- **Web IDE** (`/ide`): painel com tabs Chat/Mudanças/PRs/Testes/Terminal/Jobs/Preview, diff git do workspace, terminal PTY real via WebSocket + xterm.js.
- **Runs desacopladas**: a resposta roda no servidor — recarregar a aba reanexa com replay do stream; pause/resume cooperativo; `delegate_task` para subtarefas.
- **Agent tools builtin**: 21 tools (`shell_exec` + jobs, `file_*` com diff, `code_interpreter`, `web_search`, `fetch_url`, `todo_write`, `ask_user`, `delegate_task`, `browser_screenshot`, `browser_navigate`/`browser_act`/`browser_observe`/`browser_close` (sessão Playwright por chat, auto-install/auto-bake de Chromium na imagem, self-healing por texto/OCR tesseract), `generate_image`/`generate_video`, n8n), gate de aprovação por preset e risco LOW-MED-HIGH.
- **Painel Workspace** no chat: tabs Tasks/Changes/Jobs/MCPs/Info, divisor arrastável.
- **Slash commands**: `/` no composer — prompts do workspace + skills/commands do repo vinculado.
- **Prompt base**: system prompt de qualidade injetado pelo servidor antes do system do modelo custom.
- **Canais em grupo** com realtime SignalR (`/ws`), menção `@modelo`, typing/presence.
- **Knowledge/RAG**: coleções, embeddings, retrieval vetorial híbrido (cosseno + BM25).
- **i18n**: 8 locales com troca sem reload.
- **PWA**: manifest + service worker dual-cache (shell network-first / framework cache-first fingerprinted), offline-safe.
- **Voz**: STT/TTS/Call via Web Speech API.
- **Geração de mídia**: imagens (OpenAI/ComfyUI) e vídeo (Sora).
- **Automações**: execuções agendadas + calendário mensal; webhooks inbound (`/api/v1/hooks/{token}`).
- **Auth**: OAuth/OIDC (Google/GitHub/Microsoft), SAML, LDAP, SCIM, grupos/RBAC.
- **Analytics admin**: tokens/custos/runs, leaderboard.
- **Notificações**: toast, Notification API e Web Push (VAPID).
- **Tema**: dark/light/system via `.dark` + `localStorage webui.theme`.

## Parcial / pendente (roadmap)

Vault de secrets por usuário, árvore de runs filhas (visual), "meu uso" (analytics por usuário), boards, bot de review próprio — ver tabela de paridade.
