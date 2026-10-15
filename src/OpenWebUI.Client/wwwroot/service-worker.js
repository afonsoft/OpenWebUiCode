// Service worker do shell PWA.
// Dois caches com estratégias distintas:
// - openwebui-fw-* (cache-first): assets de _framework fingerprinted pelo
//   MapStaticAssets (name.{hash}.ext) e o espelho /framework-assets/{stem}/{ext}
//   — a URL carrega o hash do build, então conteúdo velho nunca é servido
//   para um nome novo. Evicção FIFO limita o crescimento entre deploys.
// - openwebui-shell-* (network-first): documento/navegações e toda a cadeia
//   mutável em URLs estáveis (index.html, js/*, css/*, i18n/*, assets/*,
//   manifest, os 3 loaders literais de _framework). Cache é só fallback
//   offline — um bundle velho nunca é servido depois de um deploy.
// - API / realtime: nunca cacheia — erros de rede propagam reais.
const CACHE_SHELL = 'openwebui-shell-v2';
const CACHE_FW = 'openwebui-fw-v2';
const KNOWN_CACHES = [CACHE_SHELL, CACHE_FW];

// Cap FIFO de entradas no cache de framework: cada deploy adiciona um build
// inteiro de nomes fingerprinted; sem limite o cache cresceria sem parar.
const MAX_FW_ENTRIES = 240;

// Aliases literais (não-fingerprinted) de _framework servidos como documento —
// mutáveis entre builds, ficam no caminho network-first junto com o restante.
const MUTABLE_FRAMEWORK_BOOT = new Set([
  '/_framework/blazor.webassembly.js',
  '/_framework/dotnet.js',
  '/_framework/dotnet.boot.js',
]);

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(keys.filter((k) => !KNOWN_CACHES.includes(k)).map((k) => caches.delete(k)))
      )
      .then(() => self.clients.claim())
  );
});

function isImmutableFrameworkAsset(pathname) {
  if (pathname.startsWith('/framework-assets/')) return true;
  return pathname.startsWith('/_framework/') && !MUTABLE_FRAMEWORK_BOOT.has(pathname);
}

function cacheFirst(request) {
  return caches.open(CACHE_FW).then((cache) =>
    cache.match(request).then((hit) => {
      if (hit) return hit;
      return fetch(request).then((response) => {
        if (response.ok) {
          const copy = response.clone();
          cache.put(request, copy).then(() => evictOldEntries(cache)).catch(() => { /* quota cheia */ });
        }
        return response;
      });
    })
  );
}

function evictOldEntries(cache) {
  return cache.keys().then((keys) => {
    const excess = keys.length - MAX_FW_ENTRIES;
    if (excess <= 0) return;
    // keys() vem em ordem de inserção — os mais antigos são de builds velhos.
    return Promise.all(keys.slice(0, excess).map((k) => cache.delete(k)));
  });
}

function networkFirst(request) {
  return fetch(request)
    .then((response) => {
      if (response.ok) {
        // Os clones têm que ser criados ANTES de resolver o response —
        // dentro do .then assíncrono do caches.open o browser já começou a
        // ler o body e clone() lança "Response body is already used".
        const copy = response.clone();
        const shellCopy = request.mode === 'navigate' ? response.clone() : null;
        caches.open(CACHE_SHELL).then((cache) => {
          // Fallback offline da navegação: sempre aponta para o shell atual.
          const puts = [cache.put(request, copy)];
          if (shellCopy) puts.push(cache.put('/', shellCopy));
          return Promise.all(puts);
        }).catch(() => { /* quota cheia / cache fechado */ });
      }
      return response;
    })
    .catch(() =>
      caches.open(CACHE_SHELL).then((cache) =>
        cache.match(request).then((hit) => {
          if (hit) return hit;
          return request.mode === 'navigate'
            ? cache.match('/')
            // Falha de rede num asset: resolve com erro em vez de rejeitar —
            // evita "Uncaught (in promise) TypeError: Failed to fetch" no console.
            : new Response(null, { status: 504, statusText: 'Gateway Timeout' });
        })
      )
    );
}

self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);

  if (request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/ws')) return;

  if (isImmutableFrameworkAsset(url.pathname)) {
    event.respondWith(
      cacheFirst(request).catch(
        () => new Response(null, { status: 504, statusText: 'Gateway Timeout' })
      )
    );
    return;
  }

  event.respondWith(networkFirst(request));
});

// SPEC-20261007-chat-notifications RF-004: Web Push — entrega run.completed
// mesmo com todas as abas fechadas. O clique foca uma aba existente ou abre
// /c/{chatId}.
self.addEventListener('push', (event) => {
  if (!event.data) return;

  let data;
  try {
    data = event.data.json();
  } catch (e) {
    data = { title: '', status: 'completed' };
  }

  const title = data.title && data.title.length > 0 ? data.title : 'Conversa';
  const body =
    data.status === 'completed' ? (data.snippet || 'Resposta concluída')
    : data.status === 'stopped' ? 'Resposta interrompida'
    : data.status === 'interrupted' ? 'Interrompida — abra para retomar'
    : 'Falhou';

  event.waitUntil(
    self.registration.showNotification('Open WebUI — ' + title, {
      body,
      tag: 'openwebui-chat-' + (data.runId || ''),
      data: { url: data.url || '/' },
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/';
  event.waitUntil(
    self.clients
      .matchAll({ type: 'window', includeUncontrolled: true })
      .then((clientList) => {
        // Reutiliza uma aba aberta quando existe — senão abre uma nova.
        for (const client of clientList) {
          if ('focus' in client) {
            client.focus();
            client.navigate(url);
            return;
          }
        }
        return self.clients.openWindow(url);
      })
  );
});
