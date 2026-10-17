# SPEC-20261017-calendar-mobile-ui — `}` literal na tela + calendário mobile

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `calendar-mobile-ui` |
| Type | `Bugfix` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `fix/devin-20261017-calendar-mobile` |
| Status | `Delivered` |
| Fonte | prints do usuário (`C:\print`): `}` literal na tela + layout do calendário quebrado no mobile |

## 1. Contexto

Prints do ambiente (4 imagens mobile, `~/.openclaw/media/inbound/`) mostram:

1. **`}` literal na Sidebar** — entre "Canais" e "Nenhuma conversa ainda".
   Causa-raiz confirmada: `Sidebar.razor` tinha um `}` órfão em markup
   (remanescente de um `@if` removido); Razor renderiza `}` solto como
   texto. Auditoria adicional revelou chaves i18n com `{nome}` (chave
   simples) consumidas por `L.T`, que só substitui `{{nome}}` — mesma
   classe de defeito. Casos: `ide.goto_line_hint` (`{line}`),
   `settings.model_role_inherited` (`{chain}` + arg `("models")`
   desencontrado) e as chaves novas do SPEC de settings UX.
2. **Título do calendário cru** — `calendar.m10` renderizado como chave:
   `MonthTitle`/`DayTitle` usavam `calendar.m{Mês}` (chaves inexistentes);
   a lista traduzida correta é `calendar.months` (CSV com 12 nomes).
3. **`/calendar` com overflow horizontal no mobile (≤640px)**: formulário
   "novo evento" com inputs de largura fixa (`w-48`, `w-56`, `w-40`),
   células `h-20` alongadas, e `.page-header` sem wrap forçando a página a
   rolar lateralmente (título + toggle + prev/next não cabem).

## 2. Requisitos

- **RF-001 — `}` e placeholders consistentes**: remover `}` órfão de
  `Sidebar.razor`; todo valor de locale consumido por `L.T` usa `{{nome}}`;
  args do call site casam com os placeholders. Guard novo
  `tools/check-i18n-placeholders.py` valida os dois lados.
- **RF-001a — Título do mês traduzido**: `MonthTitle`/`DayTitle` usam
  `calendar.months` (CSV) em vez de `calendar.m{Mês}` inexistente.
- **RF-002 — Form do calendário responsivo**: inputs passam a `w-full` no
  mobile (mantém largura fixa em `sm:`+); botão criar full-width ≤640px.
- **RF-003 — Grid do mês legível em ≤640px**: células com altura menor
  (`h-16 sm:h-20`), padding reduzido, indicadores cabem sem overflow; legenda
  e cabeçalho do dia quebram linha (`flex-wrap`) quando necessário.
- **RF-004 — Header da página**: controles (toggle de camada, prev/next)
  agrupam com wrap, tap targets ≥44px.

## 3. Não-objetivos

- Redesenhar o calendário (agenda semanal, drag-and-drop) — fora de escopo.
- Alterar regras de negócio de eventos/automações.

## 4. Plano

1. T1 — Remover `}` órfão do Sidebar; fix de placeholders (`{{x}}`) já
   entregue no PR #439; adicionar guard `check-i18n-placeholders.py` aqui
   para impedir regressão.
2. T2 — Calendar.razor: `MonthName` via `calendar.months`; classes
   responsivas no form/grid/header.
3. T3 — Build + guards + suíte.

## 5. Critérios de aceite

- AC-1: `check-i18n-placeholders.py` falha se `{x}` simples aparecer em chave
  interpolada ou se arg de `L.T` não casar com placeholder.
- AC-2: Em 375px o formulário do calendário não transborda horizontalmente.
- AC-3: `dotnet build` + `check-css-classes` + `check-i18n-parity` verdes.
