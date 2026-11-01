// Áudio do Open WebUI — STT (Web Speech API ou /transcriptions server-side)
// e TTS (speechSynthesis ou /speech server-side). Os controles só aparecem
// quando configurados: engine "web-speech" usa as APIs do browser; "auto" e
// "server" exigem que o servidor reporte a capability em /audio/capabilities.
(function () {
	const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
	let recog = null;
	let sttRef = null;
	let accFinal = '';
	let ttsRef = null;
	let mediaRec = null;
	let mediaChunks = [];
	let ttsAudio = null;
	let caps = null; // {stt, tts} do servidor — cacheado

	const VOICE_KEY = 'webui.audio.voice';
	const AUTOSEND_KEY = 'webui.audio.autosend';
	const ENGINE_KEY = 'webui.audio.engine';

	function cleanForSpeech(text) {
		return (text || '')
			.replace(/```[\s\S]*?```/g, ' ')
			.replace(/`([^`]*)`/g, '$1')
			.replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
			.replace(/\[([^\]]{0,1000})\]\([^)]{0,8000}\)/g, '$1')
			.replace(/[#>*_~|]/g, ' ')
			.replace(/\s+/g, ' ')
			.trim();
	}

	function pickVoice() {
		if (!('speechSynthesis' in window)) return null;
		const voices = speechSynthesis.getVoices() || [];
		const saved = localStorage.getItem(VOICE_KEY);
		if (saved) {
			const v = voices.find((x) => x.name === saved);
			if (v) return v;
		}
		const lang = document.documentElement.lang || 'pt-BR';
		return (
			voices.find((v) => v.lang === lang) ||
			voices.find((v) => v.lang.startsWith(lang.slice(0, 2))) ||
			null
		);
	}

	function engine() {
		return localStorage.getItem(ENGINE_KEY) || 'auto';
	}

	function authHeaders() {
		const token = localStorage.getItem('webui.token');
		return token ? { Authorization: 'Bearer ' + token } : {};
	}

	async function serverCaps() {
		if (caps) return caps;
		try {
			const r = await fetch('/api/v1/audio/capabilities', { headers: authHeaders() });
			caps = r.ok ? await r.json() : { stt: false, tts: false };
		} catch {
			caps = { stt: false, tts: false };
		}
		return caps;
	}

	function useServerStt() {
		return engine() !== 'web-speech';
	}

	function useServerTts() {
		return engine() !== 'web-speech';
	}

	async function startServerStt(ref) {
		if (!('MediaRecorder' in window) || !navigator.mediaDevices?.getUserMedia) {
			return false;
		}
		try {
			const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
			mediaChunks = [];
			mediaRec = new MediaRecorder(stream);
			sttRef = ref;
			mediaRec.ondataavailable = (e) => {
				if (e.data.size > 0) mediaChunks.push(e.data);
			};
			mediaRec.onstop = async () => {
				stream.getTracks().forEach((t) => t.stop());
				const d = sttRef;
				sttRef = null;
				const blob = new Blob(mediaChunks, { type: mediaRec?.mimeType || 'audio/webm' });
				mediaRec = null;
				try {
					const form = new FormData();
					const ext = blob.type.includes('ogg') ? 'ogg' : 'webm';
					form.append('file', blob, 'gravacao.' + ext);
					const r = await fetch('/api/v1/audio/transcriptions', {
						method: 'POST',
						headers: authHeaders(),
						body: form,
					});
					if (!r.ok) {
						await d?.invokeMethodAsync('OnSttError', 'server-' + r.status);
						await d?.invokeMethodAsync('OnSttEnded');
						return;
					}
					const data = await r.json();
					await d?.invokeMethodAsync('OnSttResult', data.text || '');
					await d?.invokeMethodAsync('OnSttEnded');
				} catch {
					await d?.invokeMethodAsync('OnSttError', 'server-error');
					await d?.invokeMethodAsync('OnSttEnded');
				}
			};
			mediaRec.start();
			return true;
		} catch {
			sttRef = null;
			return false;
		}
	}

	async function speakServer(text, ref) {
		ttsRef = ref || null;
		try {
			const r = await fetch('/api/v1/audio/speech', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json', ...authHeaders() },
				body: JSON.stringify({ input: cleanForSpeech(text) }),
			});
			if (!r.ok) {
				const d = ttsRef;
				ttsRef = null;
				d?.invokeMethodAsync('OnTtsEnded');
				return false;
			}
			const blob = await r.blob();
			ttsAudio = new Audio(URL.createObjectURL(blob));
			ttsAudio.onended = ttsAudio.onerror = () => {
				const d = ttsRef;
				ttsRef = null;
				ttsAudio = null;
				d?.invokeMethodAsync('OnTtsEnded');
			};
			await ttsAudio.play();
			return true;
		} catch {
			const d = ttsRef;
			ttsRef = null;
			d?.invokeMethodAsync('OnTtsEnded');
			return false;
		}
	}

	window.openwebui = window.openwebui || {};
	window.openwebui.audio = {
		sttSupported: async () =>
			engine() === 'web-speech'
				? !!SR
				: ('MediaRecorder' in window) && (await serverCaps()).stt,
		ttsSupported: async () =>
			engine() === 'web-speech'
				? 'speechSynthesis' in window
				: (await serverCaps()).tts,

		voices: () =>
			('speechSynthesis' in window ? speechSynthesis.getVoices() : []).map((v) => ({
				name: v.name,
				lang: v.lang,
			})),
		getVoice: () => localStorage.getItem(VOICE_KEY) || '',
		setVoice: (name) =>
			name ? localStorage.setItem(VOICE_KEY, name) : localStorage.removeItem(VOICE_KEY),
		getAutoSend: () => localStorage.getItem(AUTOSEND_KEY) === 'true',
		setAutoSend: (on) => localStorage.setItem(AUTOSEND_KEY, on ? 'true' : 'false'),
		getEngine: () => engine(),
		setEngine: (e) => localStorage.setItem(ENGINE_KEY, e || 'auto'),
		// Invalida o cache de capabilities (config admin mudou).
		refreshCaps: () => { caps = null; },

		// Inicia o ditado; callbacks no ref: OnSttResult(text), OnSttEnded(), OnSttError(name).
		startStt: async (ref, lang) => {
			if (useServerStt()) return await startServerStt(ref);
			if (!SR) return false;
			window.openwebui.audio.stopStt();
			sttRef = ref;
			accFinal = '';
			recog = new SR();
			recog.lang = lang || 'pt-BR';
			recog.interimResults = true;
			recog.continuous = false;
			recog.onresult = (e) => {
				let interim = '';
				for (let i = e.resultIndex; i < e.results.length; i++) {
					const r = e.results[i];
					if (r.isFinal) {
						accFinal += (accFinal ? ' ' : '') + r[0].transcript;
					} else {
						interim += r[0].transcript;
					}
				}
				let full = accFinal;
				if (interim) {
					full += (full ? ' ' : '') + interim;
				}
				sttRef?.invokeMethodAsync('OnSttResult', full);
			};
			recog.onerror = (e) => sttRef?.invokeMethodAsync('OnSttError', e.error || 'error');
			recog.onend = () => {
				const d = sttRef;
				sttRef = null;
				d?.invokeMethodAsync('OnSttEnded');
			};
			try {
				recog.start();
				return true;
			} catch {
				sttRef = null;
				return false;
			}
		},
		stopStt: () => {
			try {
				recog?.stop();
			} catch {
				/* noop */
			}
			try {
				if (mediaRec && mediaRec.state !== 'inactive') mediaRec.stop();
			} catch {
				/* noop */
			}
		},

		// Fala o texto; ref opcional recebe OnTtsEnded() ao terminar.
		speak: async (text, ref) => {
			if (useServerTts()) return await speakServer(text, ref);
			if (!('speechSynthesis' in window)) return false;
			speechSynthesis.cancel();
			ttsRef = ref || null;
			const u = new SpeechSynthesisUtterance(cleanForSpeech(text));
			const v = pickVoice();
			if (v) {
				u.voice = v;
				u.lang = v.lang;
			}
			u.onend = u.onerror = () => {
				const d = ttsRef;
				ttsRef = null;
				d?.invokeMethodAsync('OnTtsEnded');
			};
			speechSynthesis.speak(u);
			return true;
		},
		stopSpeak: () => {
			try {
				speechSynthesis.cancel();
			} catch {
				/* noop */
			}
			try {
				ttsAudio?.pause();
				ttsAudio = null;
			} catch {
				/* noop */
			}
		},
		isSpeaking: () =>
			('speechSynthesis' in window && speechSynthesis.speaking) ||
			(ttsAudio !== null && !ttsAudio.paused),
	};
})();
