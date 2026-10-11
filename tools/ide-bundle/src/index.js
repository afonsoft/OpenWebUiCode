// Vendored CodeMirror 6 bundle — fonte de wwwroot/js/ide-editor.bundle.js.
// Build: cd tools/ide-bundle && npm install && npm run build  (esbuild iife).
// Global: window.owuiIdeEditor — mesma API do bundle original + Fase E:
//   getCursor/setCursor/getSelection/setSelection, minimap (showMinimap),
//   mergeView (createDiff/destroyDiff), keybinds LSP → dotnet callbacks,
//   LSP completion via autocompletion override assíncrono.
import {
  EditorView, keymap, drawSelection, highlightActiveLine,
  highlightActiveLineGutter, highlightSpecialChars, lineNumbers,
  rectangularSelection, crosshairCursor, dropCursor, placeholder as cmPlaceholder,
} from "@codemirror/view";
import { EditorState, Prec } from "@codemirror/state";
import {
  history, defaultKeymap, historyKeymap, indentWithTab,
} from "@codemirror/commands";
import {
  bracketMatching, foldGutter, foldKeymap, indentOnInput,
  syntaxHighlighting, defaultHighlightStyle,
} from "@codemirror/language";
import {
  autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap,
} from "@codemirror/autocomplete";
import { searchKeymap, openSearchPanel } from "@codemirror/search";
import { javascript } from "@codemirror/lang-javascript";
import { python } from "@codemirror/lang-python";
import { css } from "@codemirror/lang-css";
import { html } from "@codemirror/lang-html";
import { json } from "@codemirror/lang-json";
import { markdown } from "@codemirror/lang-markdown";
import { cpp } from "@codemirror/lang-cpp";
import { yaml } from "@codemirror/lang-yaml";
import { MergeView } from "@codemirror/merge";
import { showMinimap } from "@replit/codemirror-minimap";
import { oneDark } from "@codemirror/theme-one-dark";

// ---------- language map (mesmo do bundle original) ----------
const LANG = {
  js: () => javascript(),
  jsx: () => javascript({ jsx: true }),
  ts: () => javascript({ typescript: true }),
  tsx: () => javascript({ typescript: true, jsx: true }),
  py: () => python(),
  css: () => css(),
  html: () => html(),
  json: () => json(),
  md: () => markdown(),
  yml: () => yaml(),
  yaml: () => yaml(),
  cs: () => cpp(),
  c: () => cpp(),
  cpp: () => cpp(),
  cxx: () => cpp(),
  h: () => cpp(),
};
function langFor(path) {
  const ext = (path || "").split(".").pop().toLowerCase();
  return LANG[ext] ? LANG[ext]() : null;
}

// ---------- compartments: língua / dark / readonly / minimap ----------
const langComp = { of: null };
const darkComp = { of: null };
const roComp = { of: null };
const mmComp = { of: null };
// Facets reais criadas por bundle para reconfigure:
import { Compartment } from "@codemirror/state";
const langC = new Compartment();
const darkC = new Compartment();
const roC = new Compartment();
const mmC = new Compartment();
const linkC = new Compartment(); // split: sync de docs entre panes

const editors = new Map(); // elId -> EditorView
const metas = new Map();   // elId -> {dotnet, path}
const diffs = new Map();   // elId -> MergeView

// pos <-> offset (0-based line/col em UTF-16, igual à API LSP)
function posToOffset(view, line, col) {
  const l = view.state.doc.line(Math.min(line + 1, view.state.doc.lines));
  return Math.min(l.from + col, l.to);
}
function offsetToPos(view, off) {
  const l = view.state.doc.line(Math.max(0, Math.min(off, view.state.doc.length)));
  return { line: l.number - 1, col: off - l.from };
}

// ---------- LSP keybinds → dotnet ----------
function dotnetCall(dotnet, method, arg) {
  if (!dotnet) return;
  try { dotnet.invokeMethodAsync(method, arg).catch(() => {}); } catch (e) { /* noop */ }
}

// Completion: dotnet retorna Promise<{from:{line,col}, items:[{label,kind,detail,applyText}]} | null>
function lspCompletionSource(elId) {
  return (ctx) => {
    const meta = metas.get(elId);
    const dotnet = meta && meta.dotnet;
    const path = meta && meta.path;
    if (!dotnet || !path) return null;
    const pos = ctx.pos;
    const p = offsetToPos(ctx.view ?? { state: ctx.state }, pos);
    // ctx.view pode não existir no tipo; offsetToPos usa state.
    try {
      return Promise.resolve(
        dotnet.invokeMethodAsync("OnLspCompletionRequested", {
          path, line: p.line, col: p.col,
        })
      ).then((r) => {
        if (!r || !r.items || !r.items.length) return null;
        const from = posToOffset({ state: ctx.state }, r.from.line, r.from.col);
        return {
          from,
          options: r.items.map((it) => ({
            label: it.label,
            type: it.type || undefined,
            detail: it.detail || undefined,
            info: it.info || undefined,
            apply: it.applyText || it.label,
          })),
          validFor: /^[\w.]*$/,
        };
      }).catch(() => null);
    } catch (e) { return null; }
  };
}

function buildExtensions(opts, dotnet) {
  const saveKey = {
    key: "Mod-s", preventDefault: true,
    run: () => { dotnetCall(dotnet, "OnSaveRequested", null); return true; },
  };
  const searchKey = {
    key: "Mod-f", preventDefault: true,
    run: (v) => { openSearchPanel(v); return true; },
  };
  // Fase E — teclas LSP (VS Code): F12 def, Shift+F12 refs, F2 rename.
  const lspKeys = [
    {
      key: "F12", preventDefault: true,
      run: (v) => { dotnetCall(dotnet, "OnDefinitionRequested", selPos(v)); return true; },
    },
    {
      key: "Shift-F12", preventDefault: true,
      run: (v) => { dotnetCall(dotnet, "OnReferencesRequested", selPos(v)); return true; },
    },
    {
      key: "F2", preventDefault: true,
      run: (v) => { dotnetCall(dotnet, "OnRenameRequested", selPos(v)); return true; },
    },
  ];
  function selPos(v) {
    const s = v.state.selection.main;
    const meta = metas.get(opts.elId);
    return {
      path: meta ? meta.path : opts.path,
      anchor: offsetToPos(v, s.anchor),
      head: offsetToPos(v, s.head),
    };
  }

  // Alt+click adiciona cursor (multi-cursor estilo VS Code).
  const multiCursor = EditorView.domEventHandlers({
    mousedown: (e, view) => {
      if (!e.altKey || e.button !== 0) return false;
      const pos = view.posAtCoords({ x: e.clientX, y: e.clientY });
      if (pos == null) return false;
      e.preventDefault();
      const sel = view.state.selection;
      view.dispatch({
        selection: EditorState.create().selection.create(
          [...sel.ranges, { anchor: pos, head: pos }]
        ),
      });
      return true;
    },
  });

  return [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter(),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    bracketMatching(),
    closeBrackets(),
    autocompletion({ override: [lspCompletionSource(opts.elId)] }),
    rectangularSelection(),
    crosshairCursor(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    multiCursor,
    langC.of(langFor(opts.path) ?? []),
    darkC.of(opts.dark ? oneDark : []),
    roC.of(EditorState.readOnly.of(!!opts.readOnly)),
    mmC.of(showMinimap.compute(["doc"], () =>
      opts.minimap ? { create: (v) => ({ dom: document.createElement("div") }), displayText: "blocks", showOverlay: "always" } : null
    )),
    linkC.of([]),
    EditorView.updateListener.of((u) => {
      if (u.docChanged && dotnet) {
        dotnet.invokeMethodAsync("OnDocChanged", u.state.doc.toString()).catch(() => {});
      }
    }),
    keymap.of([
      saveKey, searchKey, ...lspKeys,
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...foldKeymap,
      ...completionKeymap,
      indentWithTab,
    ]),
    opts.placeholder ? cmPlaceholder(opts.placeholder) : [],
  ];
}

window.owuiIdeEditor = {
  create(elId, opts, dotnet) {
    const el = document.getElementById(elId);
    if (!el) return false;
    this.destroy(elId);
    const eo = opts || {};
    eo.elId = elId;
    metas.set(elId, { dotnet, path: eo.path });
    const view = new EditorView({
      state: EditorState.create({
        doc: eo.doc || "",
        extensions: buildExtensions(eo, dotnet),
      }),
      parent: el,
    });
    editors.set(elId, view);
    return true;
  },
  getDoc(elId) {
    const v = editors.get(elId);
    return v ? v.state.doc.toString() : null;
  },
  setDoc(elId, doc, path) {
    const v = editors.get(elId);
    if (!v) return;
    v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: doc } });
    if (path !== undefined) {
      const m = metas.get(elId);
      if (m) m.path = path;
      v.dispatch({ effects: langC.reconfigure(langFor(path) ?? []) });
    }
  },
  setLanguage(elId, path) {
    const m = metas.get(elId);
    if (m) m.path = path;
    const v = editors.get(elId);
    if (v) v.dispatch({ effects: langC.reconfigure(langFor(path) ?? []) });
  },
  setReadonly(elId, ro) {
    const v = editors.get(elId);
    if (v) v.dispatch({ effects: roC.reconfigure(EditorState.readOnly.of(!!ro)) });
  },
  setDark(elId, dark) {
    const v = editors.get(elId);
    if (v) v.dispatch({ effects: darkC.reconfigure(dark ? oneDark : []) });
  },
  openSearch(elId) {
    const v = editors.get(elId);
    if (v) openSearchPanel(v);
  },
  focus(elId) {
    const v = editors.get(elId);
    if (v) v.focus();
  },
  destroy(elId) {
    const v = editors.get(elId);
    if (v) { v.destroy(); editors.delete(elId); metas.delete(elId); }
    const d = diffs.get(elId);
    if (d) { d.destroy(); diffs.delete(elId); }
  },

  // ---------- Fase E: cursor / seleção ----------
  getCursor(elId) {
    const v = editors.get(elId);
    if (!v) return null;
    const s = v.state.selection.main;
    return { anchor: offsetToPos(v, s.anchor), head: offsetToPos(v, s.head) };
  },
  setCursor(elId, line, col) {
    const v = editors.get(elId);
    if (!v) return;
    const off = posToOffset(v, line, col);
    v.dispatch({ selection: { anchor: off }, scrollIntoView: true });
    v.focus();
  },
  getSelection(elId) {
    const v = editors.get(elId);
    if (!v) return null;
    const s = v.state.selection.main;
    return {
      anchor: offsetToPos(v, s.anchor),
      head: offsetToPos(v, s.head),
      text: v.state.sliceDoc(s.from, s.to),
    };
  },
  setSelection(elId, a, h) {
    const v = editors.get(elId);
    if (!v) return;
    v.dispatch({
      selection: {
        anchor: posToOffset(v, a.line, a.col),
        head: h ? posToOffset(v, h.line, h.col) : undefined,
      },
      scrollIntoView: true,
    });
  },
  replaceRange(elId, from, to, text) {
    const v = editors.get(elId);
    if (!v) return;
    v.dispatch({
      changes: {
        from: posToOffset(v, from.line, from.col),
        to: posToOffset(v, to.line, to.col),
        insert: text,
      },
    });
  },
  wordAt(elId, line, col) {
    const v = editors.get(elId);
    if (!v) return null;
    const off = posToOffset(v, line, col);
    const w = v.state.wordAt(off);
    if (!w) return null;
    return { from: offsetToPos(v, w.from), to: offsetToPos(v, w.to), text: v.state.sliceDoc(w.from, w.to) };
  },

  // ---------- Fase E: minimap ----------
  setMinimap(elId, on) {
    const v = editors.get(elId);
    if (!v) return;
    v.dispatch({
      effects: mmC.reconfigure(showMinimap.compute(["doc"], () =>
        on ? { create: (x) => ({ dom: document.createElement("div") }), displayText: "blocks", showOverlay: "always" } : null
      )),
    });
  },

  // ---------- Fase E: split editor (mesma doc, 2 panes sync) ----------
  createSplit(elId, srcId, opts) {
    const src = editors.get(srcId);
    const el = document.getElementById(elId);
    if (!src || !el) return false;
    this.destroy(elId);
    const eo = opts || {};
    eo.elId = elId;
    metas.set(elId, metas.get(srcId) || { dotnet: null, path: eo.path });
    const view = new EditorView({
      state: EditorState.create({
        doc: src.state.doc.toString(),
        extensions: buildExtensions(eo, eo.dotnet),
      }),
      parent: el,
    });
    editors.set(elId, view);
    this.linkCompanions(srcId, elId);
    return true;
  },
  linkCompanions(a, b) {
    const va = editors.get(a), vb = editors.get(b);
    if (!va || !vb) return;
    const g = { echo: false };
    const syncTo = (to) => EditorView.updateListener.of((u) => {
      if (u.docChanged && !g.echo) {
        g.echo = true;
        to.dispatch({ changes: u.changes });
        g.echo = false;
      }
    });
    va.dispatch({ effects: linkC.reconfigure(syncTo(vb)) });
    vb.dispatch({ effects: linkC.reconfigure(syncTo(va)) });
  },
  unlinkCompanion(elId) {
    const v = editors.get(elId);
    if (v) v.dispatch({ effects: linkC.reconfigure([]) });
    // outros panes apontando pra elId também limpam
    for (const [id, other] of editors) {
      if (id !== elId) {
        other.dispatch({ effects: linkC.reconfigure([]) });
      }
    }
  },

  // ---------- Fase E: diff (MergeView) num host próprio ----------
  createDiff(elId, optsA, optsB, dotnet) {
    const el = document.getElementById(elId);
    if (!el) return false;
    const old = diffs.get(elId);
    if (old) { old.destroy(); diffs.delete(elId); }
    const shared = [
      highlightActiveLine(), drawSelection(),
      syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
      (optsA && optsA.path) ? (langFor(optsA.path) ?? []) : [],
      (optsA && optsA.dark) ? oneDark : [],
      EditorState.readOnly.of(true),
    ];
    const editable = shared.map((x) => x === shared[shared.length - 1] ? EditorState.readOnly.of(false) : x);
    const mv = new MergeView({
      a: { doc: (optsA && optsA.doc) || "", extensions: shared },
      b: { doc: (optsB && optsB.doc) || "", extensions: editable },
      parent: el,
      gutter: true,
      highlightChanges: true,
      collapseUnchanged: { margin: 3, minSize: 4 },
    });
    diffs.set(elId, mv);
    return true;
  },
  getDiffDoc(elId) {
    const d = diffs.get(elId);
    return d ? d.b.state.doc.toString() : null;
  },
  destroyDiff(elId) {
    const d = diffs.get(elId);
    if (d) { d.destroy(); diffs.delete(elId); }
  },
};
