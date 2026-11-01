// Colaboração em notas: posição do cursor em elementos de edição.
window.openwebui = window.openwebui || {};
window.openwebui.collab = {
    caret: function (id) {
        const el = document.getElementById(id);
        if (el?.selectionStart == null) return 0;
        return el.selectionStart;
    }
};
