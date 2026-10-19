// xterm.js wrapper for the /terminal page — sessions multiplexadas por
// WebSocket JSON ({type:'input'|'output'|'resize'|'exit'}) em /ws/terminal/{id}.
window.owuiTerminal = (() => {
    const terms = new Map(); // elementId -> { term, fit, ws, observer, el, lastCols, lastRows }

    // xterm (JS+CSS) só é carregado sob demanda: ~57 KiB fora do caminho
    // crítico de boot para quem nunca abre /terminal nem a aba do painel.
    let loadPromise = null;

    function injectScript(src) {
        return new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src;
            s.onload = resolve;
            s.onerror = () => reject(new Error('failed to load ' + src));
            document.head.appendChild(s);
        });
    }

    function ensureLoaded() {
        if (loadPromise) {
            return loadPromise;
        }
        loadPromise = (async () => {
            if (typeof Terminal === 'undefined') {
                const css = document.createElement('link');
                css.rel = 'stylesheet';
                css.href = 'lib/xterm/xterm.css';
                document.head.appendChild(css);
                // Sequencial de propósito: o addon referencia Terminal no load.
                await injectScript('lib/xterm/xterm.js');
                await injectScript('lib/xterm/xterm-addon-fit.js');
            }
            return typeof Terminal !== 'undefined';
        })();
        return loadPromise;
    }

    function isCompactViewport() {
        return typeof window.matchMedia === 'function'
            && window.matchMedia('(pointer: coarse), (hover: none), (max-width: 767.98px)').matches;
    }

    function send(ws, payload) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(payload));
        }
    }

    async function open(elementId, wsUrl, dotNetRef) {
        try {
            await ensureLoaded();
        } catch {
            return false; // offline/edge: caller exibe estado de erro
        }
        const el = document.getElementById(elementId);
        if (!el || typeof Terminal === 'undefined') {
            return false;
        }
        close(elementId);

        const term = new Terminal({
            cursorBlink: true,
            fontSize: isCompactViewport() ? 12 : 13,
            fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
            scrollback: isCompactViewport() ? 800 : 2000,
            // Paleta ANSI GitHub Dark completa — as 16 cores default do
            // xterm ficam desbotadas sobre #0d1117 (SPEC terminal-color).
            theme: {
                background: '#0d1117',
                foreground: '#e6edf3',
                cursor: '#58a6ff',
                cursorAccent: '#0d1117',
                selectionBackground: '#264f78',
                black: '#484f58',
                red: '#ff7b72',
                green: '#7ee787',
                yellow: '#f2cc60',
                blue: '#79c0ff',
                magenta: '#d2a8ff',
                cyan: '#39c5cf',
                white: '#b1bac4',
                brightBlack: '#6e7681',
                brightRed: '#ffa198',
                brightGreen: '#56d364',
                brightYellow: '#e3b341',
                brightBlue: '#79c0ff',
                brightMagenta: '#d2a8ff',
                brightCyan: '#56d4dd',
                brightWhite: '#f0f6fc'
            }
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(el);
        fit.fit();

        const ws = new WebSocket(wsUrl);
        const entry = { term, fit, ws, observer: null, el, lastCols: 0, lastRows: 0 };

        ws.onmessage = ev => {
            let msg;
            try {
                msg = JSON.parse(ev.data);
            } catch {
                term.write(ev.data);
                return;
            }
            if (msg.type === 'output') {
                term.write(msg.data || '');
            } else if (msg.type === 'exit') {
                term.write(`\r\n\x1b[90m[sessão encerrada: ${msg.code}]\x1b[0m\r\n`);
            }
        };
        ws.onopen = () => reportResize(entry);
        ws.onclose = () => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnTerminalClosed', elementId);
            }
        };
        ws.onerror = () => { /* onclose reports the state change */ };

        term.onData(d => send(ws, { type: 'input', data: d }));
        term.attachCustomKeyEventHandler(ev => {
            if (ev.type !== 'keydown') {
                return true;
            }
            const ctrl = ev.ctrlKey && !ev.altKey && !ev.metaKey;
            if (ctrl && ev.key.toLowerCase() === 'c' && term.hasSelection()) {
                const sel = term.getSelection();
                if (sel && navigator.clipboard?.writeText) {
                    navigator.clipboard.writeText(sel).catch(() => { });
                }
                return false;
            }
            return true;
        });

        entry.observer = new ResizeObserver(() => {
            try {
                reportResize(entry);
            } catch { /* element gone */ }
        });
        entry.observer.observe(el);
        terms.set(elementId, entry);
        return true;
    }

    function reportResize(entry) {
        const el = entry.el;
        if (!el || el.offsetParent === null || el.clientHeight === 0 || el.clientWidth === 0) {
            return;
        }
        entry.fit.fit();
        const { cols, rows } = entry.term;
        if (cols < 2 || rows < 2 || (cols === entry.lastCols && rows === entry.lastRows)) {
            return;
        }
        entry.lastCols = cols;
        entry.lastRows = rows;
        send(entry.ws, { type: 'resize', cols, rows });
    }

    function close(elementId) {
        const entry = terms.get(elementId);
        if (!entry) {
            return;
        }
        terms.delete(elementId);
        try { entry.observer?.disconnect(); } catch { }
        try { entry.ws?.close(); } catch { }
        try { entry.term?.dispose(); } catch { }
    }

    return { open, close };
})();
