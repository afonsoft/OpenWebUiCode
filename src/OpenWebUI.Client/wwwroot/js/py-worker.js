// Worker que hospeda o Pyodide (Python WASM) e executa blocos de código.
let pyodide = null;
let runQueue = Promise.resolve();

onmessage = async function (e) {
	const msg = e.data;
	if (msg.type === 'init') {
		try {
			importScripts(msg.indexURL + 'pyodide.js');
			pyodide = await loadPyodide({ indexURL: msg.indexURL });
			postMessage({ type: 'ready' });
		} catch (err) {
			postMessage({ type: 'load-error', error: String(err?.message ? err.message : err) });
		}
		return;
	}

	if (msg.type === 'run' && pyodide) {
		runQueue = runQueue.then(async function () {
			const stdout = [];
			const stderr = [];
			pyodide.setStdout({ batched: function (line) { stdout.push(line); } });
			pyodide.setStderr({ batched: function (line) { stderr.push(line); } });
			try {
				const result = await pyodide.runPythonAsync(msg.code);
				if (result !== undefined) {
					stdout.push(String(result));
				}
			} catch (err) {
				stderr.push(String(err?.message ? err.message : err));
			}
			postMessage({
				type: 'result',
				stdout: stdout.join('\n'),
				stderr: stderr.join('\n')
			});
		});
	}
};
