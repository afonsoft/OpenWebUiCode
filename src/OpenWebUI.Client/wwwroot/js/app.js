window.openwebui = {
	setTheme: function (theme) {
		const isDark =
			theme === 'dark' ||
			(theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
		document.documentElement.classList.toggle('dark', isDark);
		document.documentElement.dataset.theme = isDark ? 'dark' : 'light';
		const meta = document.querySelector('meta[name="theme-color"]');
		if (meta) meta.setAttribute('content', isDark ? '#171717' : '#ffffff');
		this._hookSystemTheme();
	},
	// SPEC-20261018-themes-custom RF-002: modo 'system' segue o SO em tempo
	// real — o listener é instalado uma vez e relê webui.theme a cada troca.
	_hookSystemTheme: function () {
		if (this._systemThemeHooked) return;
		this._systemThemeHooked = true;
		const mq = window.matchMedia('(prefers-color-scheme: dark)');
		const reapply = function () {
			const saved = localStorage.getItem('webui.theme') || 'light';
			if (saved === 'system') {
				window.openwebui.setTheme('system');
			}
		};
		if (mq.addEventListener) mq.addEventListener('change', reapply);
		else if (mq.addListener) mq.addListener(reapply); // Safari < 14
	},
	// SPEC-20261018-themes-custom RF-001: injeta <style id="user-theme"> com
	// as vars do tema e marca <html> com theme-{id}. 'default' limpa tudo —
	// a paleta stock do tailwind volta sem reload.
	setCustomTheme: function (themeId, css) {
		const root = document.documentElement;
		for (const cls of Array.from(root.classList)) {
			if (cls.startsWith('theme-')) root.classList.remove(cls);
		}
		const id = themeId && themeId !== 'default' ? themeId : null;
		if (id) root.classList.add('theme-' + id);
		let style = document.getElementById('user-theme');
		if (!style) {
			style = document.createElement('style');
			style.id = 'user-theme';
			document.head.appendChild(style);
		}
		style.textContent = id ? (css || '') : '';
		try {
			localStorage.setItem('webui.theme.id', id || 'default');
			localStorage.setItem('webui.theme.css', id ? (css || '') : '');
		} catch (e) { /* storage negado — o tema aplica mesmo assim nesta sessão */ }
	},
	scrollToEnd: function (element) {
		if (element) {
			element.scrollTop = element.scrollHeight;
		}
	},
	// Auto-scroll do stream: marca _openwebuiStuck quando o usuário está
	// perto do fim; quem sobe para ler não é arrastado de volta pelo stream.
	initPinnedScroll: function (element) {
		if (!element || element._openwebuiPin) return;
		element._openwebuiPin = true;
		element._openwebuiStuck = true;
		element.addEventListener('scroll', function () {
			element._openwebuiStuck =
				element.scrollHeight - element.scrollTop - element.clientHeight < 160;
		});
	},
	scrollToEndIfPinned: function (element) {
		if (element && element._openwebuiStuck !== false) {
			element.scrollTop = element.scrollHeight;
		}
	},
	// SPEC-20261019-chat-window-perf RF-002: sentinel de scroll-up — quando o
	// usuário aproxima do topo, pede ao Blazor a página anterior de mensagens.
	initTopSentinel: function (element, dotNetRef) {
		if (!element || element._owTopSentinel) return;
		element._owTopSentinel = true;
		element.addEventListener('scroll', function () {
			if (element.scrollTop < 240 && !element._owLoadingOlder) {
				element._owLoadingOlder = true;
				dotNetRef.invokeMethodAsync('OnTopSentinelAsync')
					.finally(function () { element._owLoadingOlder = false; });
			}
		});
	},
	getScrollHeight: function (element) {
		return element ? element.scrollHeight : 0;
	},
	// Compensação pós-prepend: devolve ao usuário o mesmo ponto de leitura
	// somando o delta de altura ao scrollTop.
	adjustScrollAfterPrepend: function (element, previousHeight) {
		if (element) {
			element.scrollTop += element.scrollHeight - previousHeight;
		}
	},
	copyText: function (text) {
		return navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve();
	},
	// SPEC-20261013-chat-media-attachments RF-002: entrega autenticada de
	// mídia — a API é Bearer-only (sem cookie), então o JS busca os bytes
	// com o token da sessão e cria um object URL cacheado por fileId.
	// Falha (401/404/rede) devolve null → a UI cai no chip "indisponível".
	media: {
		_cache: {},
		_pending: {},
		_token: function () {
			return localStorage.getItem('webui.token') || '';
		},
		resolve: async function (fileId) {
			if (!fileId) return null;
			if (this._cache[fileId]) return this._cache[fileId];
			if (this._pending[fileId]) return this._pending[fileId];
			var self = this;
			this._pending[fileId] = (async function () {
				try {
					var res = await fetch('/api/v1/files/' + encodeURIComponent(fileId) + '/content', {
						headers: { 'Authorization': 'Bearer ' + self._token() }
					});
					if (!res.ok) return null;
					var blob = await res.blob();
					var url = URL.createObjectURL(blob);
					self._cache[fileId] = url;
					return url;
				} catch (e) {
					return null;
				} finally {
					delete self._pending[fileId];
				}
			})();
			return this._pending[fileId];
		},
		revoke: function (fileId) {
			var url = this._cache[fileId];
			if (url) {
				URL.revokeObjectURL(url);
				delete this._cache[fileId];
			}
		},
		revokeAll: function () {
			for (var id in this._cache) {
				URL.revokeObjectURL(this._cache[id]);
			}
			this._cache = {};
		}
	},
	// Navegação por setas em tablists e menus: Left/Up = anterior,
	// Right/Down = próximo, Home/End = extremos. Foca o alvo sem ativá-lo.
	rovingFocus: function (element, key) {
		if (!element) return;
		const items = Array.prototype.filter.call(
			element.querySelectorAll('[role="tab"], [role="menuitem"]'),
			function (el) { return !el.disabled && el.offsetParent !== null; });
		if (!items.length) return;
		let index = items.indexOf(document.activeElement);
		if (key === 'Home') index = 0;
		else if (key === 'End') index = items.length - 1;
		else if (key === 'ArrowRight' || key === 'ArrowDown') index = (index + 1) % items.length;
		else if (key === 'ArrowLeft' || key === 'ArrowUp') index = (index - 1 + items.length) % items.length;
		else return;
		items[index].focus();
	},
	// Rola a tab de índice `index` para dentro da área visível da tablist —
	// usado pelas setas < > do painel de workspace quando as tabs excedem a
	// largura do painel (sidebar estreita).
	scrollTabIntoView: function (element, index) {
		if (!element) return;
		const items = element.querySelectorAll('[role="tab"]');
		const item = items[index];
		if (item) item.scrollIntoView({ block: 'nearest', inline: 'nearest' });
	},
	openFilePicker: function (element) {
		if (element) {
			element.click();
		}
	},
	// attachments-v2 RF-002: paths relativos (webkitRelativePath) dos arquivos
	// selecionados num input type=file com webkitdirectory.
	dirUploadPaths: function (element) {
		if (!element?.files) return [];
		const paths = [];
		for (const file of element.files) {
			paths.push(file.webkitRelativePath || file.name);
		}
		return paths;
	},
	// attachments-v2 RF-004: intercepta paste no composer — o texto vai p/
	// .NET decidir (vira anexo acima do threshold) ou insere de volta.
	hookPaste: function (element, dotNetRef) {
		if (!element || element._openwebuiPaste) return;
		element._openwebuiPaste = true;
		element.addEventListener('paste', function (e) {
			const text = e.clipboardData?.getData('text/plain');
			if (!text) return; // paste de imagem/arquivo segue o fluxo normal.
			e.preventDefault();
			dotNetRef.invokeMethodAsync('HandlePastedTextAsync', text);
		});
	},
	// Insere texto na posição do cursor (fallback do paste abaixo do threshold).
	insertTextAtCursor: function (element, text) {
		if (!element) return;
		const start = element.selectionStart || 0;
		const end = element.selectionEnd || 0;
		const before = element.value.slice(0, start);
		const after = element.value.slice(end);
		element.value = before + text + after;
		const caret = start + text.length;
		element.selectionStart = element.selectionEnd = caret;
		element.dispatchEvent(new Event('input', { bubbles: true }));
	},
	// Divisor arrastável do painel de workspace: arrasta p/ esquerda
	// alarga o painel (clamp 240–720px), largura persiste em localStorage.
	panelResize: function (handle, panel) {
		if (!handle || !panel || handle._openwebuiRs) return;
		handle._openwebuiRs = true;
		const saved = parseInt(localStorage.getItem('openwebui.panelWidth') || '', 10);
		if (saved >= 240 && saved <= 720) panel.style.width = saved + 'px';
		handle.addEventListener('pointerdown', function (e) {
			e.preventDefault();
			const startX = e.clientX;
			const startW = panel.getBoundingClientRect().width;
			function move(ev) {
				const w = Math.min(720, Math.max(240, startW + (startX - ev.clientX)));
				panel.style.width = w + 'px';
			}
			function up() {
				document.removeEventListener('pointermove', move);
				document.removeEventListener('pointerup', up);
				localStorage.setItem(
					'openwebui.panelWidth',
					String(Math.round(panel.getBoundingClientRect().width)));
			}
			document.addEventListener('pointermove', move);
			document.addEventListener('pointerup', up);
		});
	},
	// Cresce o elemento até scrollHeight, limitado pelo max-height do CSS
	// (ex.: composer max-h-96). Sem teto declarado, cresce sem limite.
	autoResize: function (element) {
		if (!element) return;
		element.style.height = 'auto';
		const max = parseFloat(window.getComputedStyle(element).maxHeight);
		const target = element.scrollHeight;
		element.style.height = (isFinite(max) ? Math.min(target, max) : target) + 'px';
	},
	// Protocolo upstream: abre https://openwebui.com/post?type=<type> em nova aba e
	// posta o item serializado quando a página sinaliza 'loaded'.
	shareCommunity: function (type, payloadJson) {
		const url = 'https://openwebui.com';
		const tab = window.open(url + '/post?type=' + encodeURIComponent(type), '_blank');
		if (!tab) return false;
		const handler = function (event) {
			if (event.origin !== url) return;
			if (event.data === 'loaded') {
				tab.postMessage(payloadJson, url);
				window.removeEventListener('message', handler);
			}
		};
		window.addEventListener('message', handler, false);
		return true;
	},
	setLang: function (lang) {
		document.documentElement.lang = lang || 'pt-BR';
	},
	// Trap de foco para modais acessíveis: foca o primeiro interativo e faz
	// Tab/Shift+Tab circular dentro do elemento (Escape fica no .razor).
	trapFocus: function (element) {
		if (!element) return;
		const selector = 'button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])';
		const focusables = function () {
			return Array.prototype.filter.call(
				element.querySelectorAll(selector),
				function (el) { return !el.disabled && el.offsetParent !== null; });
		};
		element._openwebuiTrap = function (e) {
			if (e.key !== 'Tab') return;
			const f = focusables();
			if (!f.length) return;
			if (e.shiftKey && document.activeElement === f[0]) {
				f[f.length - 1].focus();
				e.preventDefault();
			} else if (!e.shiftKey && document.activeElement === f[f.length - 1]) {
				f[0].focus();
				e.preventDefault();
			}
		};
		element.addEventListener('keydown', element._openwebuiTrap);
		const first = focusables()[0];
		if (first) first.focus();
	},
	releaseFocus: function (element) {
		if (element && element._openwebuiTrap) {
			element.removeEventListener('keydown', element._openwebuiTrap);
			element._openwebuiTrap = null;
		}
	},
	// Navegação por setas em menus/listboxes: ArrowUp/Down/Home/End movem o
	// foco entre os itens focáveis do contêiner (Escape fica no .razor).
	menuNav: function (container, key) {
		if (!container) return;
		const items = Array.prototype.filter.call(
			container.querySelectorAll('button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])'),
			function (el) { return !el.disabled && el.offsetParent !== null; });
		if (!items.length) return;
		const i = items.indexOf(document.activeElement);
		if (key === 'ArrowDown') { (items[i + 1] || items[0]).focus(); }
		else if (key === 'ArrowUp') { (items[i - 1] || items[items.length - 1]).focus(); }
		else if (key === 'Home') { items[0].focus(); }
		else if (key === 'End') { items[items.length - 1].focus(); }
		else { return; }
	},
	// Foca o primeiro item interativo de um menu recém-aberto.
	focusFirst: function (container) {
		if (!container) return;
		const first = container.querySelector(
			'button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])');
		if (first && !first.disabled) first.focus();
	},
	// Devolve o foco a um trigger identificado por seletor (menus sem @ref).
	focusSelector: function (selector) {
		const el = document.querySelector(selector);
		if (el) el.focus();
	}
};

// SPEC-20261007-chat-notifications: Notification API + Web Push para avisar
// quando uma run de chat termina. Permissão só é pedida no gesto do usuário
// (toggle em Settings) — nunca no boot. Toda falha degrada silenciosamente
// para o toast in-app.
window.openwebui.notify = {
	// Notification.permission: 'default' | 'granted' | 'denied' | 'unsupported'.
	permission: function () {
		return ('Notification' in window) ? Notification.permission : 'unsupported';
	},

	// Notificação do SO só dispara com a aba fora de foco.
	isHidden: function () {
		return document.hidden === true;
	},

	// Pede a permissão (somente sob gesto do usuário — chamado pelo toggle).
	ensurePermission: async function () {
		try {
			if (!('Notification' in window)) {
				return 'unsupported';
			}
			if (Notification.permission !== 'default') {
				return Notification.permission;
			}
			return await Notification.requestPermission();
		} catch (e) {
			return 'denied';
		}
	},

	// Exibe notificação do SO; retorna true quando exibida.
	notify: function (title, body, url, tag) {
		try {
			if (!('Notification' in window) || Notification.permission !== 'granted') {
				return false;
			}
			var n = new Notification(title, { body: body || '', tag: tag || 'openwebui' });
			n.onclick = function () {
				try {
					window.focus();
					if (url) {
						window.location.href = url;
					}
				} catch (e) { /* navegação best-effort */ }
				n.close();
			};
			return true;
		} catch (e) {
			return false;
		}
	},

	// ---- Web Push (entrega com a aba totalmente fechada) ----

	_vapidB64ToBytes: function (b64) {
		var pad = '='.repeat((4 - (b64.length % 4)) % 4);
		var raw = atob((b64 + pad).replace(/-/g, '+').replace(/_/g, '/'));
		var out = new Uint8Array(raw.length);
		for (var i = 0; i < raw.length; i++) {
			out[i] = raw.charCodeAt(i);
		}
		return out;
	},

	// O service worker do PWA já trata 'push' — usa o mesmo registro.
	_swRegistration: async function () {
		if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
			return null;
		}
		try {
			return await navigator.serviceWorker.ready;
		} catch (e) {
			return null;
		}
	},

	isPushSubscribed: async function () {
		try {
			var reg = await this._swRegistration();
			var sub = reg ? await reg.pushManager.getSubscription() : null;
			return sub ? sub.endpoint : null;
		} catch (e) {
			return null;
		}
	},

	// Subscreve e grava o endpoint no servidor.
	// Retorna { subscribed: true, endpoint } | { error }.
	subscribePush: async function (vapidPublicKey, apiUrl) {
		try {
			var permission = await this.ensurePermission();
			if (permission !== 'granted') {
				return { error: 'permission ' + permission };
			}
			var reg = await this._swRegistration();
			if (!reg) {
				return { error: 'service worker unsupported' };
			}
			var sub = await reg.pushManager.subscribe({
				userVisibleOnly: true,
				applicationServerKey: this._vapidB64ToBytes(vapidPublicKey)
			});
			var json = sub.toJSON();
			json.userAgent = navigator.userAgent;
			var res = await fetch(apiUrl, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				credentials: 'same-origin',
				body: JSON.stringify(json)
			});
			if (!res.ok) {
				return { error: 'server rejected (' + res.status + ')' };
			}
			return { subscribed: true, endpoint: sub.endpoint };
		} catch (e) {
			return { error: String(e) };
		}
	},

	// Remove a subscription no navegador e no servidor.
	unsubscribePush: async function (apiUrl) {
		try {
			var reg = await this._swRegistration();
			var sub = reg ? await reg.pushManager.getSubscription() : null;
			if (sub) {
				await fetch(apiUrl + '?endpoint=' + encodeURIComponent(sub.endpoint), {
					method: 'DELETE',
					credentials: 'same-origin'
				});
				await sub.unsubscribe();
			}
			return { unsubscribed: true };
		} catch (e) {
			return { error: String(e) };
		}
	}
};
