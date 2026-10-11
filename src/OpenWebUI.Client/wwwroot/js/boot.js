// Proxies corporativos podem bloquear assets de boot pela extensão na URL
// (.dat/.wasm/...) ou por sniffing de payload binário. Todo asset de
// _framework que não seja .js passa por uma cadeia de três camadas
// (mesma abordagem do KnowledgeHub/agent-harness):
//   1. /framework-assets/{stem}/{ext}          — sem sufixo bloqueado na URL,
//      bytes idênticos, então o hash de integridade do boot valida nativamente.
//   2. /framework-assets/{stem}/{ext}?enc=b64  — base64 text/plain, derrota
//      content-sniffing; o SHA-256 é verificado no cliente via crypto.subtle.
//   3. defaultUri                              — dev / ambientes sem proxy.
// Se todas falharem, Blazor.start rejeita e #app mostra mensagem legível.
(function () {
    "use strict";

    // O runtime WASM reporta erros não tratados via console.error e exibe
    // #blazor-error-ui, mas nunca renderiza o texto da exceção. Mantemos um
    // buffer dos últimos erros para o link "Details" do banner poder mostrá-los.
    var errorLog = [];
    var MAX_ERROR_LOG = 10;

    // Copy de erro pré-boot: os dicionários i18n ainda não existem quando o
    // WASM falha ao carregar, então mantemos um mapa inline mínimo. O locale
    // vem do localStorage (mesma chave do app) com navigator.language como
    // fallback; idiomas ausentes caem para pt-BR (padrão do produto).
    var BOOT_STRINGS = {
        "pt-BR": {
            error: "Ocorreu um erro não tratado.", reload: "Recarregar",
            details: "Detalhes", hideDetails: "Ocultar detalhes", dismiss: "Dispensar",
            bootTitle: "Não foi possível iniciar o aplicativo",
            bootBody1: "A rede ou o proxy corporativo bloqueou arquivos necessários ao carregamento (filtro de downloads por tipo de mídia).",
            bootBody2: "Recarregue a página. Se o problema persistir, contate o suporte de TI ou acesse por outra rede."
        },
        "en-US": {
            error: "An unhandled error has occurred.", reload: "Reload",
            details: "Details", hideDetails: "Hide details", dismiss: "Dismiss",
            bootTitle: "The app could not be started",
            bootBody1: "The network or corporate proxy blocked files required for startup (media-type download filtering).",
            bootBody2: "Reload the page. If the problem persists, contact IT support or try another network."
        },
        "de-DE": {
            error: "Ein unbehandelter Fehler ist aufgetreten.", reload: "Neu laden",
            details: "Details", hideDetails: "Details ausblenden", dismiss: "Verwerfen",
            bootTitle: "Die App konnte nicht gestartet werden",
            bootBody1: "Das Netzwerk oder der Unternehmensproxy hat für den Start benötigte Dateien blockiert (Download-Filterung nach Medientyp).",
            bootBody2: "Laden Sie die Seite neu. Bleibt das Problem bestehen, wenden Sie sich an den IT-Support oder nutzen Sie ein anderes Netzwerk."
        },
        "es-ES": {
            error: "Se ha producido un error no controlado.", reload: "Recargar",
            details: "Detalles", hideDetails: "Ocultar detalles", dismiss: "Descartar",
            bootTitle: "No se pudo iniciar la aplicación",
            bootBody1: "La red o el proxy corporativo bloqueó archivos necesarios para la carga (filtro de descargas por tipo de medio).",
            bootBody2: "Recarga la página. Si el problema persiste, contacta con soporte de TI o prueba otra red."
        },
        "fr-FR": {
            error: "Une erreur non gérée s'est produite.", reload: "Recharger",
            details: "Détails", hideDetails: "Masquer les détails", dismiss: "Ignorer",
            bootTitle: "Impossible de démarrer l'application",
            bootBody1: "Le réseau ou le proxy d'entreprise a bloqué des fichiers nécessaires au chargement (filtrage des téléchargements par type de média).",
            bootBody2: "Rechargez la page. Si le problème persiste, contactez le support informatique ou essayez un autre réseau."
        },
        "it-IT": {
            error: "Si è verificato un errore non gestito.", reload: "Ricarica",
            details: "Dettagli", hideDetails: "Nascondi dettagli", dismiss: "Ignora",
            bootTitle: "Impossibile avviare l'applicazione",
            bootBody1: "La rete o il proxy aziendale ha bloccato file necessari al caricamento (filtro download per tipo di media).",
            bootBody2: "Ricarica la pagina. Se il problema persiste, contatta il supporto IT o prova un'altra rete."
        },
        "ja-JP": {
            error: "未処理のエラーが発生しました。", reload: "再読み込み",
            details: "詳細", hideDetails: "詳細を隠す", dismiss: "閉じる",
            bootTitle: "アプリを起動できませんでした",
            bootBody1: "ネットワークまたは社内プロキシが起動に必要なファイルをブロックしました（メディアタイプによるダウンロードフィルター）。",
            bootBody2: "ページを再読み込みしてください。解決しない場合は IT サポートに連絡するか、別のネットワークをお試しください。"
        },
        "zh-CN": {
            error: "发生未处理的错误。", reload: "重新加载",
            details: "详情", hideDetails: "隐藏详情", dismiss: "忽略",
            bootTitle: "应用无法启动",
            bootBody1: "网络或企业代理阻止了启动所需的文件（按媒体类型过滤下载）。",
            bootBody2: "请重新加载页面。若问题仍存在，请联系 IT 支持或改用其他网络。"
        }
    };

    function bootStrings() {
        var stored = null;
        try {
            stored = localStorage.getItem("webui.locale");
        } catch {
            // localStorage bloqueado — segue com heurística do navegador.
        }
        var candidates = [];
        if (stored) candidates.push(stored);
        if (navigator.languages) candidates.push.apply(candidates, navigator.languages);
        if (navigator.language) candidates.push(navigator.language);
        for (const cand of candidates) {
            if (BOOT_STRINGS[cand]) {
                return BOOT_STRINGS[cand];
            }
            var prefix = String(cand).split("-")[0];
            for (const code in BOOT_STRINGS) {
                if (code.split("-")[0] === prefix) {
                    return BOOT_STRINGS[code];
                }
            }
        }
        return BOOT_STRINGS["pt-BR"];
    }

    var strings = bootStrings();

    // Localiza o texto estático do #blazor-error-ui (vem em inglês do HTML).
    function localizeErrorUi() {
        var ui = document.getElementById("blazor-error-ui");
        if (!ui) {
            return;
        }
        if (ui.firstChild && ui.firstChild.nodeType === Node.TEXT_NODE) {
            ui.firstChild.textContent = " " + strings.error + " ";
        }
        var reload = ui.querySelector(".reload");
        if (reload) reload.textContent = strings.reload;
        var toggle = ui.querySelector(".error-details-toggle");
        if (toggle) toggle.textContent = strings.details;
        var dismiss = ui.querySelector(".dismiss");
        if (dismiss) dismiss.setAttribute("aria-label", strings.dismiss);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", localizeErrorUi);
    } else {
        localizeErrorUi();
    }

    function errorText(value) {
        if (value === null || value === undefined) {
            return "(null)";
        }
        return value.stack || value.message || String(value);
    }

    function renderErrorLog(panel) {
        panel.textContent = errorLog.length === 0
            ? "(no error details captured)"
            : errorLog.map(function (e) { return "[" + e.at + "] " + e.text; }).join("\n\n");
    }

    // Falhas transitórias (rede/offline/storage) nunca devem derrubar a UI.
    var transientRe = /failed to fetch|network ?error|err_(internet|connection|network|name|timed|address|empty_response|aborted)|aborterror|\babort(ed)?\b|timed? ?out|service ?worker|jsdisconnected|quotaexceeded|securityerror|notallowederror|offline/i;
    var lastTransientAt = 0;

    function recordError(value) {
        var text = errorText(value);
        if (transientRe.test(text)) {
            lastTransientAt = Date.now();
        }
        var last = errorLog.at(-1);
        if (last && last.text === text) {
            return; // falha do renderer relança por evento — manter uma cópia
        }
        errorLog.push({ at: new Date().toISOString(), text: text });
        if (errorLog.length > MAX_ERROR_LOG) {
            errorLog.shift();
        }
        var panel = document.querySelector("#blazor-error-ui .error-details");
        if (panel && !panel.hidden) {
            renderErrorLog(panel);
        }
    }

    // Última linha de defesa no DOM: QUALQUER código que exiba #blazor-error-ui
    // escreve style.display — se a exibição veio logo após um erro transitório
    // registrado, reverte para oculto. Bugs reais (sem erro transitório
    // precedente) seguem mostrando o banner normalmente.
    function guardErrorUi() {
        var el = document.getElementById("blazor-error-ui");
        if (!el || el.__guardObserved) {
            return;
        }
        el.__guardObserved = true;
        new MutationObserver(function () {
            if (el.style.display === "block" && Date.now() - lastTransientAt < 4000) {
                el.style.display = "none";
                console.warn("[boot] banner de erro suprimido: falha transitória em curso");
            }
        }).observe(el, { attributes: true, attributeFilter: ["style"] });
    }
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", guardErrorUi);
    } else {
        guardErrorUi();
    }

    var originalConsoleError = console.error.bind(console);
    console.error = function () {
        for (const arg of arguments) {
            recordError(arg);
        }
        return originalConsoleError(...arguments);
    };

    window.addEventListener("error", function (event) {
        recordError(event.error || event.message);
    });
    window.addEventListener("unhandledrejection", function (event) {
        recordError(event.reason);
    });

    document.addEventListener("click", function (event) {
        var target = event.target;
        var toggle = target?.closest
            ? target.closest("#blazor-error-ui .error-details-toggle")
            : null;
        if (!toggle) {
            return;
        }
        event.preventDefault();
        var panel = document.querySelector("#blazor-error-ui .error-details");
        if (!panel) {
            return;
        }
        panel.hidden = !panel.hidden;
        if (panel.hidden) {
            toggle.textContent = strings.details;
        } else {
            renderErrorLog(panel);
            toggle.textContent = strings.hideDetails;
        }
    });

    var frameworkSegment = "/_framework/";
    var mimeByExt = { wasm: "application/wasm", json: "application/json", js: "text/javascript" };

    function mirrorUrl(stem, ext, enc) {
        var url = new URL("framework-assets/" + stem + "/" + ext, document.baseURI);
        if (enc) {
            url.searchParams.set("enc", enc);
        }
        return url;
    }

    function decodeBase64(text) {
        var clean = text.replace(/\s+/g, "");
        if (typeof Uint8Array.fromBase64 === "function") {
            return Uint8Array.fromBase64(clean);
        }
        var bin = atob(clean);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) {
            bytes[i] = bin.codePointAt(i);
        }
        return bytes;
    }

    // A integridade do manifesto de boot é "sha256-<base64>" (pode ser lista);
    // o payload b64 transforma os bytes no fio, então o browser não consegue
    // conferir — revalidamos os bytes decodificados antes de usar.
    function verifyIntegrity(bytes, integrity) {
        if (!integrity) {
            return Promise.resolve(true);
        }
        var tokens = integrity.split(/\s+/);
        var hash = null;
        for (const token of tokens) {
            if (token.indexOf("sha256-") === 0) {
                hash = token.substring(7);
                break;
            }
        }
        if (hash === null || !window.crypto || !crypto.subtle) {
            return Promise.resolve(false);
        }
        return crypto.subtle.digest("SHA-256", bytes).then(function (buf) {
            var digest = new Uint8Array(buf);
            var bin = "";
            for (const byte of digest) {
                bin += String.fromCodePoint(byte);
            }
            return btoa(bin) === hash;
        });
    }

    function fetchEncoded(url, ext, integrity) {
        return fetch(url).then(function (response) {
            if (!response.ok) {
                throw new Error("b64 mirror " + response.status);
            }
            return response.text();
        }).then(function (text) {
            var bytes = decodeBase64(text);
            return verifyIntegrity(bytes, integrity).then(function (ok) {
                if (!ok) {
                    throw new Error("integrity mismatch for " + url.pathname);
                }
                var headers = new Headers();
                headers.set("Content-Type", mimeByExt[ext] || "application/octet-stream");
                return new Response(bytes, { headers: headers });
            });
        });
    }

    function loadAsset(defaultUri, integrity, stem, ext) {
        var init = integrity ? { integrity: integrity } : {};
        var raw = mirrorUrl(stem, ext, null);
        var encoded = function () {
            return fetchEncoded(mirrorUrl(stem, ext, "b64"), ext, integrity);
        };
        return fetch(raw, init).then(
            function (response) {
                return response.ok ? response : encoded();
            },
            encoded
        ).catch(function () {
            return fetch(defaultUri, init);
        });
    }

    function showBootError() {
        var app = document.getElementById("app");
        if (!app) {
            return;
        }
        app.innerHTML =
            '<div style="max-width:36rem;margin:4rem auto;padding:0 1rem;font-family:sans-serif">' +
            '<h1 style="font-size:1.25rem">' + strings.bootTitle + '</h1>' +
            '<p>' + strings.bootBody1 + '</p>' +
            '<p>' + strings.bootBody2 + '</p>' +
            '<p><a href=".">' + strings.reload + '</a></p></div>';
    }

    try {
        Blazor.start({
            loadBootResource: function (type, name, defaultUri, integrity) {
                try {
                    var path = new URL(defaultUri, document.baseURI).pathname;
                    if (!path.includes(frameworkSegment) || path.endsWith(".js")) {
                        return null; // carregamento padrão — proxies liberam .js
                    }
                    var fileName = path.substring(path.lastIndexOf("/") + 1);
                    var dot = fileName.lastIndexOf(".");
                    if (dot <= 0) {
                        return null; // sem extensão para separar — padrão
                    }
                    return loadAsset(
                        defaultUri,
                        integrity,
                        fileName.substring(0, dot),
                        fileName.substring(dot + 1));
                } catch {
                    return null;
                }
            }
        }).then(function () {
            // Última linha de defesa no lado JS: exceções do .NET que cheguem ao
            // renderer chamam dotNetCriticalError, que exibe #blazor-error-ui
            // (uma trava que só sai com reload). Falhas transitórias de rede/
            // storage/offline NÃO devem derrubar a UI — o app continua rodando
            // e os componentes já degradam sozinhos. Erros de lógica/bug real
            // seguem exibindo o banner normalmente.
            //
            // _internal só é populado depois que o runtime WASM inicia — pode
            // chegar DEPOIS deste .then. Faz polling até a função existir.
            var transientRe = /failed to fetch|network ?error|err_(internet|connection|network|name|timed|address|empty_response|aborted)|aborterror|\babort(ed)?\b|timed? ?out|service ?worker|jsdisconnected|quotaexceeded|securityerror|notallowederror|offline/i;
            var attempts = 0;
            var wrap = function () {
                var internal = window.Blazor && window.Blazor._internal;
                var original = internal && internal.dotNetCriticalError;
                if (typeof original !== "function" || internal.__critGuarded) {
                    if (!internal || !internal.__critGuarded) {
                        if (++attempts < 100) setTimeout(wrap, 100);
                    }
                    return;
                }
                try {
                    internal.__critGuarded = true;
                    internal.dotNetCriticalError = function (error) {
                        var text;
                        try {
                            text = String((error && (error.stack || error.message)) || error || "");
                        } catch {
                            text = "";
                        }
                        if (transientRe.test(text)) {
                            console.warn("[boot] falha transitória suprimida do banner: " + text.substring(0, 500));
                            return;
                        }
                        return original.call(this, error);
                    };
                } catch (e) {
                    if (++attempts < 100) setTimeout(wrap, 100);
                }
            };
            wrap();
        }).catch(showBootError);
    } catch {
        showBootError();
    }
})();
