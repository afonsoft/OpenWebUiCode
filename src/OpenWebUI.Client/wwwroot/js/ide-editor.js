// Lazy loader + facade for the vendored CodeMirror bundle (ide-editor.bundle.js).
// Loaded via <script src="js/ide-editor.js"> in index.html; the heavy bundle is
// only fetched on first use (route /ide), keeping the chat boot cheap.
// Globals used by Blazor JS interop: owuiIde.{create,getDoc,setDoc,...}.
(function () {
    let loading = null;
    function ensureBundle() {
        if (window.owuiIdeEditor) {
            return Promise.resolve();
        }
        if (!loading) {
            loading = new Promise(function (resolve, reject) {
                const s = document.createElement("script");
                s.src = "js/ide-editor.bundle.js";
                s.onload = resolve;
                s.onerror = () => reject(new Error("ide editor bundle failed to load"));
                document.head.appendChild(s);
            });
        }
        return loading;
    }
    async function call(name, args) {
        await ensureBundle();
        return window.owuiIdeEditor[name](...args);
    }
    // ---------- LSP marker layer (SPEC-20261009-lsp-diagnostics, S8) ----------
    // O bundle CM vendored não traz @codemirror/lint — squiggles/hover viram
    // uma camada DOM leve aqui (zero npm/CDN). Degrada limpo: sem diagnostics
    // nada é renderizado.
    const lsp = {}; // elId -> {dotnet, diags, overlay, tooltip, hoverTimer, lastHover}
    const SQUIGGLE_COLORS = { 1: "#ef4444", 2: "#f59e0b", 3: "#3b82f6", 4: "#6b7280" };
    function wave(color) {
        return "url(\"data:image/svg+xml," + encodeURIComponent(
            "<svg xmlns='http://www.w3.org/2000/svg' width='6' height='3'>" +
            "<path d='M0 1.5 L1.5 0.5 L3 1.5 L4.5 2.5 L6 1.5' fill='none' stroke='" +
            color + "' stroke-width='1'/></svg>") + "\")";
    }
    function st(elId) {
        return lsp[elId] || (lsp[elId] = { diags: [], overlay: null, tooltip: null, dotnet: null });
    }
    function cmRoot(el) { return el?.querySelector(".cm-editor"); }
    function cmContent(el) { return el?.querySelector(".cm-content"); }
    // Mapeia elemento .cm-line → doc line (0-based) via gutter de números;
    // fallback: índice na ordem renderizada (docs pequenos renderizam tudo).
    function lineMap(el) {
        const lines = Array.from(el.querySelectorAll(".cm-line"));
        const nums = Array.from(el.querySelectorAll(".cm-lineNumbers .cm-gutterElement"));
        const map = new Map();
        if (nums.length === lines.length && nums.length > 0) {
            lines.forEach((l, i) => {
                const n = Number.parseInt(nums[i].textContent, 10);
                if (!Number.isNaN(n)) map.set(n - 1, l);
            });
            return map;
        }
        lines.forEach((l, i) => map.set(i, l));
        return map;
    }
    function ensureOverlay(elId) {
        const s = st(elId), el = document.getElementById(elId);
        const root = cmRoot(el);
        if (!root) return null;
        if (!s.overlay || s.overlay.parentElement !== root.parentElement) {
            const ov = document.createElement("div");
            ov.className = "owui-lsp-overlay";
            ov.style.cssText = "position:absolute;inset:0;pointer-events:none;overflow:hidden;z-index:5";
            root.parentElement.style.position = "relative";
            root.parentElement.appendChild(ov);
            s.overlay = ov;
        }
        return s.overlay;
    }
    function offsetInLine(lineEl, charCol) {
        // charCol (0-based UTF-16) → {node, offset} dentro do .cm-line.
        const walker = document.createTreeWalker(lineEl, NodeFilter.SHOW_TEXT);
        let acc = 0, node;
        while ((node = walker.nextNode())) {
            if (acc + node.data.length > charCol) {
                return { node, offset: charCol - acc };
            }
            acc += node.data.length;
        }
        return node ? { node, offset: node.data.length } : null;
    }
    function renderDiags(elId) {
        const s = st(elId), el = document.getElementById(elId);
        const ov = ensureOverlay(elId);
        if (!ov) return;
        ov.replaceChildren();
        if (!s.diags.length) return;
        const content = cmContent(el);
        if (!content) return;
        const map = lineMap(el);
        const ovRect = ov.getBoundingClientRect();
        for (const d of s.diags) {
            const lineEl = map.get(d.line);
            if (!lineEl) continue; // linha fora do viewport renderizado
            const from = offsetInLine(lineEl, d.col);
            const to = d.endLine === d.line ? offsetInLine(lineEl, d.endCol) : null;
            if (!from) continue;
            const range = document.createRange();
            try {
                range.setStart(from.node, from.offset);
                range.setEnd(to ? to.node : from.node, to ? to.offset : from.node.data.length);
            } catch { continue; }
            const color = SQUIGGLE_COLORS[d.severity] || SQUIGGLE_COLORS[3];
            appendSquiggles(ov, ovRect, range, color, d.message);
        }
    }
    function appendSquiggles(ov, ovRect, range, color, msg) {
        for (const r of range.getClientRects()) {
            if (r.width === 0) continue;
            const m = document.createElement("div");
            m.className = "owui-lsp-squiggle";
            m.style.cssText = "position:absolute;pointer-events:none;height:4px;" +
                "left:" + (r.left - ovRect.left) + "px;top:" + (r.bottom - ovRect.top - 2) +
                "px;width:" + r.width + "px;background-repeat:repeat-x;background-image:" +
                wave(color);
            m.dataset.msg = msg;
            ov.appendChild(m);
        }
    }
    function attachScrollOnce(elId) {
        const s = st(elId);
        if (s.scrollBound) return;
        const el = document.getElementById(elId);
        const scroller = el?.querySelector(".cm-scroller");
        if (scroller) {
            scroller.addEventListener("scroll", () => renderDiags(elId), { passive: true });
            s.scrollBound = true;
        }
    }
    function showTooltip(elId, x, y, text) {
        const s = st(elId), el = document.getElementById(elId);
        if (!el) return;
        hideTooltip(elId);
        const tip = document.createElement("div");
        tip.className = "owui-lsp-tooltip";
        tip.textContent = text;
        tip.style.cssText = "position:fixed;max-width:480px;max-height:240px;overflow:auto;" +
            "z-index:60;padding:6px 8px;font-size:12px;border-radius:6px;" +
            "background:#1f2937;color:#f9fafb;box-shadow:0 4px 16px rgba(0,0,0,.35);" +
            "white-space:pre-wrap;pointer-events:none;left:" + Math.min(x, window.innerWidth - 500) +
            "px;top:" + (y + 14) + "px";
        document.body.appendChild(tip);
        s.tooltip = tip;
    }
    function hideTooltip(elId) {
        const s = st(elId);
        if (s.tooltip) { s.tooltip.remove(); s.tooltip = null; }
    }
    function posFromPoint(el, x, y) {
        const doc = document;
        const caret = doc.caretRangeFromPoint ? doc.caretRangeFromPoint(x, y)
            : (() => { const p = doc.caretPositionFromPoint(x, y);
                return p && { startContainer: p.offsetNode, startOffset: p.offset }; })();
        if (!caret) return null;
        const content = cmContent(el);
        if (!content?.contains(caret.startContainer)) return null;
        // Acha a .cm-line que contém o caret e o offset de char dentro dela.
        let lineEl = caret.startContainer.nodeType === 1
            ? caret.startContainer : caret.startContainer.parentElement;
        while (lineEl && !lineEl.classList.contains("cm-line")) lineEl = lineEl.parentElement;
        if (!lineEl) return null;
        const map = lineMap(el);
        let lineNo = -1;
        for (const [n, le] of map) if (le === lineEl) { lineNo = n; break; }
        if (lineNo < 0) return null;
        // Offset do caret dentro da linha (soma text nodes anteriores).
        const r = doc.createRange();
        r.selectNodeContents(lineEl);
        r.setEnd(caret.startContainer, caret.startOffset);
        return { line: lineNo, col: r.toString().length };
    }
    function attachHover(elId) {
        const s = st(elId), el = document.getElementById(elId);
        if (!el || s.hoverBound) return;
        const content = cmContent(el);
        if (!content) return;
        s.hoverBound = true;
        content.addEventListener("mousemove", (ev) => {
            if (!s.dotnet) return;
            clearTimeout(s.hoverTimer);
            const x = ev.clientX, y = ev.clientY;
            s.hoverTimer = setTimeout(async () => {
                const pos = posFromPoint(el, x, y);
                const key = pos && (pos.line + ":" + pos.col);
                if (pos && key !== s.lastHover) {
                    s.lastHover = key;
                    try {
                        const text = await s.dotnet.invokeMethodAsync(
                            "OnLspHoverAsync", pos.line + 1, pos.col + 1);
                        if (text) { showTooltip(elId, x, y, text); return; }
                    } catch { /* circuit/hover falhou → sem tooltip */ }
                }
                if (!pos) hideTooltip(elId);
            }, 350);
        });
        content.addEventListener("mouseleave", () => hideTooltip(elId));
        content.addEventListener("mousedown", () => hideTooltip(elId));
    }
    window.owuiIde = {
        create: (elId, opts, dotnet) => {
            st(elId).dotnet = dotnet;
            return call("create", [elId, opts, dotnet]).then((r) => {
                attachHover(elId);
                attachScrollOnce(elId);
                return r;
            });
        },
        getDoc: (elId) => call("getDoc", [elId]),
        setDoc: (elId, doc, path) => {
            st(elId).lastHover = null;
            return call("setDoc", [elId, doc, path]);
        },
        setLanguage: (elId, path) => call("setLanguage", [elId, path]),
        setReadonly: (elId, v) => call("setReadonly", [elId, v]),
        setDark: (elId, dark) => call("setDark", [elId, dark]),
        openSearch: (elId) => call("openSearch", [elId]),
        focus: (elId) => call("focus", [elId]),
        // ---- Fase E: cursor/seleção/minimap/diff ----
        getCursor: (elId) => call("getCursor", [elId]),
        setCursor: (elId, line, col) => call("setCursor", [elId, line, col]),
        getSelection: (elId) => call("getSelection", [elId]),
        setSelection: (elId, a, h) => call("setSelection", [elId, a, h]),
        replaceRange: (elId, from, to, text) => call("replaceRange", [elId, from, to, text]),
        wordAt: (elId, line, col) => call("wordAt", [elId, line, col]),
        setMinimap: (elId, on) => call("setMinimap", [elId, on]),
        createDiff: (elId, optsA, optsB, dotnet) => call("createDiff", [elId, optsA, optsB, dotnet]),
        getDiffDoc: (elId) => call("getDiffDoc", [elId]),
        destroyDiff: (elId) => call("destroyDiff", [elId]),
        createSplit: (elId, srcId, opts) => call("createSplit", [elId, srcId, opts]),
        unlinkCompanion: (elId) => call("unlinkCompanion", [elId]),
        destroy: (elId) => {
            hideTooltip(elId);
            delete lsp[elId];
            return call("destroy", [elId]);
        },
        // ---- superfície LSP ----
        setDiagnostics: (elId, diags) => {
            st(elId).diags = Array.isArray(diags) ? diags : [];
            attachScrollOnce(elId);
            renderDiags(elId);
        },
        goto: (elId, line, col) => {
            const el = document.getElementById(elId);
            const map = lineMap(el);
            const lineEl = map.get(Math.max(0, line));
            if (!lineEl) return;
            lineEl.scrollIntoView({ block: "center" });
            const pos = offsetInLine(lineEl, Math.max(0, col));
            if (!pos) return;
            const sel = window.getSelection();
            const r = document.createRange();
            r.setStart(pos.node, pos.offset);
            r.collapse(true);
            sel.removeAllRanges();
            sel.addRange(r);
            const root = cmRoot(el);
            if (root) root.focus();
        },
        refreshDiagnostics: (elId) => renderDiags(elId),
    };
})();
