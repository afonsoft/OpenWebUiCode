// Layout do workbench /ide (SPEC-20261011-ide-workbench Fase A):
// atalhos globais estilo VS Code (Ctrl+B sidebar, Ctrl+J panel, Ctrl+`
// terminal, Ctrl+K Z zen) + sashes arrastáveis com persistência em
// localStorage (tamanho por usuário).
(function () {
    "use strict";
    const LS = "ide:layout:";
    const store = {
        get: (k) => { try { return localStorage.getItem(LS + k); } catch { return null; } },
        set: (k, v) => { try { localStorage.setItem(LS + k, v); } catch { /* quota */ } },
    };

    // ---- atalhos globais ----
    let dotnet = null;
    let chord = 0; // timestamp do "prefix" (Ctrl+K …)
    window.owuiIdeLayout = {
        attachKeys: (ref) => { dotnet = ref; },
        detachKeys: () => { dotnet = null; },
        sashValue: (k) => store.get("sash:" + k),
        copyText: (t) => { try { navigator.clipboard.writeText(t); } catch { /* sem permissão */ } },
        getAutosave: () => store.get("autosave") === "1",
        setAutosave: (on) => store.set("autosave", on ? "1" : "0"),
    };

    window.addEventListener("keydown", (e) => {
        if (!dotnet) return;
        const mod = e.ctrlKey || e.metaKey;
        // Fase D: Shift+Alt+F = Format Document (antes do gate ctrl/meta).
        if (!mod && e.altKey && e.shiftKey && (e.key === "F" || e.key === "f")) {
            e.preventDefault();
            dotnet.invokeMethodAsync("OnLayoutKey", "format");
            return;
        }
        if (!mod) { chord = 0; return; }
        const now = Date.now();
        if (chord && now - chord < 900 && (e.key === "z" || e.key === "Z")) {
            chord = 0;
            e.preventDefault();
            dotnet.invokeMethodAsync("OnLayoutKey", "zen");
            return;
        }
        chord = 0;
        switch (e.key) {
            case "b": case "B": e.preventDefault(); dotnet.invokeMethodAsync("OnLayoutKey", "side"); break;
            case "j": case "J": e.preventDefault(); dotnet.invokeMethodAsync("OnLayoutKey", "bottom"); break;
            case "`": e.preventDefault(); dotnet.invokeMethodAsync("OnLayoutKey", "terminal"); break;
            case "k": case "K": chord = now; break;
            // SPEC-20261011-ide-editor-core Fase B: quick open / palette / goto.
            case "p": case "P":
                e.preventDefault();
                dotnet.invokeMethodAsync("OnLayoutKey", e.shiftKey ? "palette" : "quickopen");
                break;
            case "g": case "G": e.preventDefault(); dotnet.invokeMethodAsync("OnLayoutKey", "gotoline"); break;
            case "o": case "O": e.preventDefault(); dotnet.invokeMethodAsync("OnLayoutKey", "symbol"); break;
            // Fase C: Ctrl+Shift+F busca global (Ctrl+F simples fica pro browser).
            case "f": case "F":
                if (e.shiftKey) {
                    e.preventDefault();
                    dotnet.invokeMethodAsync("OnLayoutKey", "search");
                }
                break;
        }
    }, { capture: true });

    // ---- sashes arrastáveis ----
    // Uso: <div class="ide-sash" data-ide-sash="explorer" data-ide-target="#id"
    //           data-ide-axis="x" data-ide-min="140" data-ide-max="600">
    // width/height do alvo segue o pointer; persistido por data-ide-sash.
    window.addEventListener("pointerdown", (e) => {
        const sash = e.target.closest?.("[data-ide-sash]");
        if (!sash) return;
        const target = document.querySelector(sash.dataset.ideTarget);
        if (!target) return;
        e.preventDefault();
        const axis = sash.dataset.ideAxis || "x";
        const min = parseInt(sash.dataset.ideMin || "0", 10);
        const max = parseInt(sash.dataset.ideMax || "2000", 10);
        const start = axis === "x" ? target.offsetWidth : target.offsetHeight;
        const origin = axis === "x" ? e.clientX : e.clientY;
        const grow = sash.dataset.ideGrow !== "reverse" ? 1 : -1;
        const move = (ev) => {
            const d = ((axis === "x" ? ev.clientX : ev.clientY) - origin) * grow;
            const v = Math.min(max, Math.max(min, start + d));
            if (axis === "x") target.style.width = v + "px";
            else target.style.height = v + "px";
            sash.dataset.ideValue = v;
        };
        const up = () => {
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            if (sash.dataset.ideValue) store.set("sash:" + sash.dataset.ideSash, sash.dataset.ideValue);
        };
        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up);
    }, { capture: false });
})();
