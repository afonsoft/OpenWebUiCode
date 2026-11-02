# SPEC-20261018-themes-custom

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `themes-custom` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-themes-custom` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P2.8) |

## 1. User Story

**As a** usuário do app web
**I want** temas customizados por JSON (tokens → CSS custom properties) com presets embutidos, por usuário e opcionalmente por workspace
**So that** a UI case com branding interno ou preferência pessoal — paridade com opencode v2 `themes` (JSON token themes, dark/light palettes, system mode).

## 2. Scope

**In scope:**
- Modelo `Theme {id, name, dark{}, light{}}` com tokens mapeados para CSS custom properties (`--bg`, `--surface`, `--text`, `--accent`, `--border`, `--code-bg`, `--danger`...).
- Presets builtin: default (atual), `midnight`, `solarized`, `high-contrast`; themes custom via JSON upload (user-level) e `WEBUI_THEME_JSON` (instance-level).
- Seletor em Settings → Appearance: tema + modo `system|light|dark`; token `system` segue `prefers-color-scheme`.
- Aplicação via `<style id="user-theme">` injetado — sem rebuild do tailwind.css (tokens viram utilities CSS vars já consumidas ou classe `theme-{id}` no root).

**Out of scope:**
- Theme por chat; editor visual de tema (v1 JSON only); themes que mudam fonte/iconografia.

## 3. Technical Context

**Where the change happens:**
`App.razor`/layout (injeta style block), `ThemeService` client-side (localStorage + API), endpoint de themes do usuário, `Settings/Appearance.razor`.

**Files to read before implementing:**
- `src/OpenWebUI.Client/Layout/MainLayout.razor` e `App.razor`
- `src/OpenWebUI.Client/Pages/Settings/` (appearance atual)
- `src/OpenWebUI.Client/wwwroot/css/tailwind.css` (uso de vars)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                            # UserTheme {UserId, Json, Active}
src/OpenWebUI.Api/Endpoints/ThemeEndpoints.cs               # NOVO CRUD
src/OpenWebUI.Client/Services/ThemeService.cs               # NOVO: aplica vars, system mode
src/OpenWebUI.Client/wwwroot/themes/builtin.json            # presets
src/OpenWebUI.Client/Pages/Settings/SettingsAppearance.razor# seletor
src/OpenWebUI.Client/Layout/MainLayout.razor                # hook <style id=user-theme>
tests/OpenWebUI.Api.Tests/ThemeEndpointsTests.cs            # NOVO
```

## 4. Requirements

### RF-001: Modelo e aplicação
- **Description:** theme JSON `{dark:{bg,surface,text,accent,...}, light:{...}}`; `ThemeService` gera `:root{--bg:...;}` para o modo ativo e injeta/atualiza o style block; troca instantânea sem reload.
- **Rules:** tokens ausentes caem no default; valores sanitizados (cor hex/rgb/var only) — rejeita `url(`/`)`/JS.

### RF-002: Modos
- **Description:** `system` escuta `prefers-color-scheme` e troca palette sem recarregar; `light`/`dark` força; escolha persistida por usuário (fallback localStorage para anônimo/demo).

### RF-003: Presets e upload
- **Description:** presets servidos de `wwwroot/themes/builtin.json`; upload JSON valida schema (máx 64KB, ≤32 tokens) e salva por usuário; `WEBUI_THEME_JSON` marca theme da instância disponível a todos.

## 5. Acceptance Criteria

- Selecionar `midnight` aplica imediatamente; recarregar persiste.
- Upload JSON inválido (token com `url(`) → 400 com erro claro.
- `system` alterna ao trocar tema do SO (testável via CDP emulate).
- Variáveis afetam superfícies principais (sidebar, composer, mensagens); testes de endpoint + sanitização.
