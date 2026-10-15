// Execução de código em blocos markdown (JavaScript via Web Worker, Python via Pyodide WASM).
window.openwebui = window.openwebui || {};

window.openwebui.codeExec = (function () {
	const JS_TIMEOUT_MS = 10000;
	const PYODIDE_CDN = 'https://cdn.jsdelivr.net/pyodide/v0.27.7/full/';
	const RUNNABLE = {
		javascript: 'js',
		js: 'js',
		python: 'python',
		py: 'python'
	};
	const LABELS = {
		js: 'JavaScript',
		python: 'Python'
	};

	let pyWorker = null;
	let pyReady = null;

	// Injeta o botão "Executar" em blocos de código executáveis dentro de root.
	function enhance(root) {
		if (!root) {
			return;
		}
		root.querySelectorAll('pre > code').forEach(function (codeEl) {
			const pre = codeEl.parentElement;
			if (pre.dataset.codeexec) {
				return;
			}
			const lang = detectLang(codeEl);
			if (!lang) {
				return;
			}
			pre.dataset.codeexec = '1';
			pre.classList.add('relative');

			const button = document.createElement('button');
			button.type = 'button';
			button.textContent = '▶ Executar';
			button.title = 'Executar ' + LABELS[lang] + ' no navegador';
			button.className =
				'absolute top-1.5 right-1.5 px-2 py-1 text-[0.7rem] font-medium rounded-md ' +
				'bg-gray-100 dark:bg-gray-800 text-gray-600 dark:text-gray-300 ' +
				'hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer';
			button.addEventListener('click', function () {
				run(codeEl.innerText, lang, button, pre).catch(function (err) {
					pre.textContent += '\n(execução falhou: ' + (err && err.message ? err.message : err) + ')';
				});
			});
			pre.appendChild(button);
		});
	}

	function detectLang(codeEl) {
		const cls = Array.from(codeEl.classList).find(function (c) {
			return c.startsWith('language-');
		});
		if (!cls) {
			return null;
		}
		return RUNNABLE[cls.substring('language-'.length).toLowerCase()] || null;
	}

	function outputPanel(pre) {
		let panel = pre.nextElementSibling;
		if (!panel || !panel.classList.contains('codeexec-out')) {
			panel = document.createElement('div');
			panel.className =
				'codeexec-out mt-1 rounded-lg border border-gray-200 dark:border-gray-800 ' +
				'bg-gray-50 dark:bg-gray-900 px-3 py-2 text-[0.8rem] font-mono whitespace-pre-wrap';
			pre.parentNode.insertBefore(panel, pre.nextSibling);
		}
		return panel;
	}

	async function run(code, lang, button, pre) {
		const panel = outputPanel(pre);
		button.disabled = true;
		button.classList.add('opacity-50');
		const started = performance.now();
		panel.textContent = lang === 'python' ? 'Carregando Python (Pyodide)…' : 'Executando…';

		try {
			const result =
				lang === 'js' ? await runJs(code) : await runPython(code, panel);
			const elapsed = ((performance.now() - started) / 1000).toFixed(1);
			renderResult(panel, result, elapsed);
		} catch (err) {
			panel.textContent = 'Erro: ' + (err && err.message ? err.message : String(err));
		} finally {
			button.disabled = false;
			button.classList.remove('opacity-50');
		}
	}

	function renderResult(panel, result, elapsed) {
		panel.textContent = '';
		if (result.stdout) {
			const out = document.createElement('div');
			out.textContent = result.stdout;
			panel.appendChild(out);
		}
		if (result.stderr) {
			const err = document.createElement('div');
			err.className = 'text-red-600 dark:text-red-400';
			err.textContent = result.stderr;
			panel.appendChild(err);
		}
		if (!result.stdout && !result.stderr) {
			const empty = document.createElement('div');
			empty.className = 'text-gray-500 dark:text-gray-400';
			empty.textContent = '(sem saída)';
			panel.appendChild(empty);
		}
		const meta = document.createElement('div');
		meta.className = 'mt-1 text-[0.7rem] text-gray-400 dark:text-gray-500';
		meta.textContent = elapsed + 's';
		panel.appendChild(meta);
	}

	// ---------------- JavaScript ----------------

	function runJs(code) {
		return new Promise(function (resolve, reject) {
			const workerSource =
				'const stdout=[];const stderr=[];' +
				'["log","info","debug"].forEach(m=>{console[m]=(...a)=>stdout.push(a.map(String).join(" "));});' +
				'["warn","error"].forEach(m=>{console[m]=(...a)=>stderr.push(a.map(String).join(" "));});' +
				'onmessage=function(e){try{const r=eval(e.data);' +
				'if(r!==undefined)stdout.push(String(r));' +
				'postMessage({stdout:stdout.join("\\n"),stderr:stderr.join("\\n")});' +
				'}catch(err){stderr.push(String(err));' +
				'postMessage({stdout:stdout.join("\\n"),stderr:stderr.join("\\n")});}};';

			const worker = new Worker(URL.createObjectURL(new Blob([workerSource], { type: 'text/javascript' })));
			const timer = setTimeout(function () {
				worker.terminate();
				resolve({ stdout: '', stderr: 'Execução abortada: timeout de ' + JS_TIMEOUT_MS / 1000 + 's' });
			}, JS_TIMEOUT_MS);

			worker.onmessage = function (e) {
				clearTimeout(timer);
				worker.terminate();
				resolve(e.data);
			};
			worker.onerror = function (e) {
				clearTimeout(timer);
				worker.terminate();
				reject(new Error(e.message || 'erro no worker'));
			};
			worker.postMessage(code);
		});
	}

	// ---------------- Python (Pyodide) ----------------

	function ensurePyWorker() {
		if (pyWorker) {
			return Promise.resolve(pyWorker);
		}
		if (pyReady) {
			return pyReady;
		}
		pyReady = new Promise(function (resolve, reject) {
			const worker = new Worker('js/py-worker.js');
			const timer = setTimeout(function () {
				reject(new Error('timeout carregando Pyodide'));
			}, 60000);
			worker.onmessage = function (e) {
				if (e.data.type === 'ready') {
					clearTimeout(timer);
					pyWorker = worker;
					resolve(worker);
				} else if (e.data.type === 'load-error') {
					clearTimeout(timer);
					pyReady = null;
					reject(new Error(e.data.error || 'falha ao carregar Pyodide'));
				}
			};
			worker.onerror = function (e) {
				clearTimeout(timer);
				pyReady = null;
				reject(new Error(e.message || 'falha ao carregar Pyodide'));
			};
			worker.postMessage({ type: 'init', indexURL: PYODIDE_CDN });
		});
		return pyReady;
	}

	async function runPython(code, panel) {
		const jupyter = await jupyterServer();
		if (jupyter) {
			try {
				return await runJupyter(code, jupyter, panel);
			} catch (err) {
				// Kernel indisponível: cai para Pyodide como antes.
				panel.textContent =
					'Jupyter indisponível (' + (err && err.message ? err.message : err) + ') — usando Pyodide…';
			}
		}
		const worker = await ensurePyWorker();
		panel.textContent = 'Executando…';
		return new Promise(function (resolve) {
			const timer = setTimeout(function () {
				// Reinicia o worker para abortar a execução travada.
				worker.terminate();
				pyWorker = null;
				pyReady = null;
				resolve({ stdout: '', stderr: 'Execução abortada: timeout de 30s' });
			}, 30000);
			const handler = function (e) {
				if (e.data.type === 'result') {
					clearTimeout(timer);
					worker.removeEventListener('message', handler);
					resolve(e.data);
				}
			};
			worker.addEventListener('message', handler);
			worker.postMessage({ type: 'run', code: code });
		});
	}

	// ---------------- Python (kernel Jupyter via proxy) ----------------
	// Admin habilita "jupyter" em code_execution e cadastra o servidor em
	// /api/v1/terminals/config; o proxy injeta a key do servidor no upstream.

	let _jupyter = undefined; // undefined = ainda não consultado

	function authToken() {
		try {
			return JSON.parse(localStorage.getItem('webui.token') || 'null');
		} catch (e) {
			return null;
		}
	}

	async function jupyterServer() {
		if (_jupyter !== undefined) {
			return _jupyter;
		}
		const token = authToken();
		if (!token) {
			return (_jupyter = null);
		}
		const headers = { Authorization: 'Bearer ' + token };
		try {
			const cfg = await fetch('/api/v1/configs/code_execution', { headers: headers })
				.then(function (r) { return r.ok ? r.json() : null; });
			if (!cfg || (cfg.engines || []).indexOf('jupyter') === -1) {
				return (_jupyter = null);
			}
			const servers = await fetch('/api/v1/terminals/', { headers: headers })
				.then(function (r) { return r.ok ? r.json() : []; });
			_jupyter = servers && servers.length ? servers[0] : null;
		} catch (e) {
			_jupyter = null;
		}
		return _jupyter;
	}

	async function runJupyter(code, server, panel) {
		const token = authToken();
		const base = '/api/v1/terminals/' + encodeURIComponent(server.id);
		const headers = {
			Authorization: 'Bearer ' + token,
			'Content-Type': 'application/json'
		};

		panel.textContent = 'Criando kernel (' + server.name + ')…';
		const kernel = await fetch(base + '/api/kernels', {
			method: 'POST', headers: headers, body: '{}'
		}).then(function (r) {
			if (!r.ok) throw new Error('kernel não criado (' + r.status + ')');
			return r.json();
		});

		try {
			panel.textContent = 'Executando…';
			const wsBase = base.replace(/^http/, location.protocol === 'https:' ? 'wss' : 'ws');
			const ws = new WebSocket(
				location.origin.replace(/^http/, location.protocol === 'https:' ? 'wss' : 'ws') +
				wsBase + '/api/kernels/' + kernel.id + '/channels?access_token=' +
				encodeURIComponent(token));
			return await executeOnKernel(ws, code);
		} finally {
			try {
				await fetch(base + '/api/kernels/' + kernel.id, { method: 'DELETE', headers: headers });
			} catch (e) { /* best-effort */ }
		}
	}

	function executeOnKernel(ws, code) {
		return new Promise(function (resolve, reject) {
			const msgId = 'exec-' + crypto.randomUUID();
			const session = 'webui-' + crypto.randomUUID();
			const out = { stdout: '', stderr: '' };
			const timer = setTimeout(function () {
				ws.close();
				resolve({ stdout: out.stdout, stderr: out.stderr + '\n(execução abortada: timeout)' });
			}, 30000);

			ws.onopen = function () {
				ws.send(JSON.stringify({
					header: {
						msg_id: msgId, username: 'webui', session: session,
						msg_type: 'execute_request', version: '5.3'
					},
					parent_header: {},
					metadata: {},
					channel: 'shell',
					content: {
						code: code, silent: false, store_history: true,
						user_expressions: {}, allow_stdin: false
					}
				}));
			};
			ws.onerror = function () {
				clearTimeout(timer);
				reject(new Error('falha no WebSocket do kernel'));
			};
			ws.onmessage = function (e) {
				let msg;
				try { msg = JSON.parse(e.data); } catch (err) { return; }
				const parent = (msg.parent_header || {}).msg_id;
				if (parent !== msgId) { return; }
				const type = (msg.header || {}).msg_type;
				const content = msg.content || {};
				if (type === 'stream') {
					if (content.name === 'stderr') { out.stderr += content.text; }
					else { out.stdout += content.text; }
				} else if (type === 'error') {
					out.stderr += (content.traceback || [content.evalue]).join('\n');
				} else if (type === 'execute_result' || type === 'display_data') {
					const text = (content.data || {})['text/plain'];
					if (text) { out.stdout += (Array.isArray(text) ? text.join('') : text) + '\n'; }
				} else if (type === 'status' && content.execution_state === 'idle') {
					clearTimeout(timer);
					ws.close();
					resolve(out);
				}
			};
		});
	}

	return { enhance: enhance };
})();
