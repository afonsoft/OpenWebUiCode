// SPEC-20261019-codeblock-toolbar: barra de ferramentas por bloco
// <pre><code> (label da linguagem + copiar + formatar JSON + wrap +
// executar) e click-to-copy em <code> inline. Idempotente via
// data-codeblock/data-inlinecopy — seguro rodar a cada re-render.
window.openwebui = window.openwebui || {};

window.openwebui.codeBlocks = (function () {
	const FORMATTABLE = { json: 1, jsonc: 1 };
	const DEFAULT_LABELS = {
		copy: 'Copy', copied: 'Copied', format: 'Format',
		minify: 'Minify', wrap: 'Wrap lines', unwrap: 'No wrap',
		run: '▶ Run', invalidJson: 'Invalid JSON', code: 'code'
	};

	function enhance(root, options) {
		if (!root) {
			return;
		}
		const opts = options || {};
		const labels = {};
		for (const k in DEFAULT_LABELS) {
			labels[k] = opts.labels?.[k] || DEFAULT_LABELS[k];
		}
		root.querySelectorAll('pre > code').forEach(function (codeEl) {
			const pre = codeEl.parentElement;
			if (pre.dataset.codeblock) {
				return;
			}
			pre.dataset.codeblock = '1';
			buildBar(pre, codeEl, labels, opts);
		});
		root.querySelectorAll('code').forEach(function (codeEl) {
			if (codeEl.dataset.inlinecopy
				|| codeEl.closest('pre,a,button,[contenteditable="true"]')) {
				return;
			}
			codeEl.dataset.inlinecopy = '1';
			codeEl.classList.add('codeblock-inline');
			codeEl.title = labels.copy;
			codeEl.addEventListener('click', function () {
				copyWithTitleFeedback(codeEl, labels);
			});
		});
	}

	function copyWithTitleFeedback(codeEl, labels) {
		copyText(codeEl.textContent || '', function (ok) {
			codeEl.title = ok ? labels.copied : labels.copy;
			setTimeout(function () { codeEl.title = labels.copy; }, 1500);
		});
	}

	function langOf(codeEl) {
		const cls = Array.from(codeEl.classList).find(function (c) {
			return c.startsWith('language-');
		});
		return cls ? cls.substring('language-'.length).toLowerCase() : null;
	}

	function mkBtn(label, onClick) {
		const btn = document.createElement('button');
		btn.type = 'button';
		btn.className = 'codeblock-btn';
		btn.textContent = label;
		btn.setAttribute('aria-label', label);
		btn.addEventListener('click', function () { onClick(btn); });
		return btn;
	}

	function buildBar(pre, codeEl, labels, opts) {
		const wrapper = document.createElement('div');
		wrapper.className = 'codeblock';
		pre.parentNode.insertBefore(wrapper, pre);

		const bar = document.createElement('div');
		bar.className = 'codeblock-bar';

		const lang = langOf(codeEl);
		const langEl = document.createElement('span');
		langEl.className = 'codeblock-lang';
		langEl.textContent = lang || labels.code;
		bar.appendChild(langEl);

		const actions = document.createElement('div');
		actions.className = 'codeblock-actions';

		if (FORMATTABLE[lang]) {
			actions.appendChild(mkBtn(labels.format, function (btn) {
				toggleFormat(codeEl, btn, labels);
			}));
		}
		actions.appendChild(mkBtn(labels.wrap, function (btn) {
			const wrapped = pre.classList.toggle('codeblock-wrapped');
			btn.textContent = wrapped ? labels.unwrap : labels.wrap;
			btn.setAttribute('aria-label', btn.textContent);
			btn.setAttribute('aria-pressed', wrapped ? 'true' : 'false');
		}));
		actions.appendChild(mkBtn(labels.copy, function (btn) {
			copyText(codeEl.innerText, function (ok) {
				btn.textContent = ok ? labels.copied : labels.copy;
				btn.setAttribute('aria-label', btn.textContent);
				if (ok) {
					setTimeout(function () {
						btn.textContent = labels.copy;
						btn.setAttribute('aria-label', labels.copy);
					}, 1500);
				}
			});
		}));
		if (opts.run && window.openwebui.codeExec
			&& openwebui.codeExec.isRunnable(codeEl)) {
			actions.appendChild(mkBtn(labels.run, function (btn) {
				openwebui.codeExec.runInBlock(codeEl, pre, btn);
			}));
		}

		bar.appendChild(actions);
		wrapper.appendChild(bar);
		wrapper.appendChild(pre);
		pre.classList.add('codeblock-pre');
	}

	// Format ↔ minify: view-only — só altera o texto visível do bloco.
	function toggleFormat(codeEl, btn, labels) {
		try {
			const obj = JSON.parse(codeEl.innerText);
			const minified = codeEl.dataset.formatted === '1';
			codeEl.textContent = minified
				? JSON.stringify(obj)
				: JSON.stringify(obj, null, 2);
			codeEl.dataset.formatted = minified ? '0' : '1';
			btn.textContent = minified ? labels.format : labels.minify;
			btn.setAttribute('aria-label', btn.textContent);
		} catch (e) {
			// JSON inválido → feedback no botão, sem alterar o bloco.
			const prev = btn.textContent;
			btn.textContent = labels.invalidJson;
			btn.setAttribute('aria-label', labels.invalidJson);
			setTimeout(function () {
				btn.textContent = prev;
				btn.setAttribute('aria-label', prev);
			}, 1500);
		}
	}

	function copyText(text, done) {
		const fallback = function () {
			try {
				const ta = document.createElement('textarea');
				ta.value = text;
				ta.style.position = 'fixed';
				ta.style.opacity = '0';
				document.body.appendChild(ta);
				ta.select();
				const ok = document.execCommand('copy');
				ta.remove();
				done(ok);
			} catch (e) {
				done(false); // execCommand falhou → cópia indisponível
			}
		};
		try {
			let p;
			if (window.openwebui.copyText) {
				p = window.openwebui.copyText(text);
			} else if (navigator.clipboard) {
				p = navigator.clipboard.writeText(text);
			}
			if (p && typeof p.then === 'function') {
				p.then(function () { done(true); }, fallback);
			} else {
				fallback();
			}
		} catch (e) {
			fallback(); // clipboard API lançou → execCommand
		}
	}

	return { enhance: enhance };
})();
