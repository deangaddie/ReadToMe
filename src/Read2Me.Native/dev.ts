// Dev server: Bun's HTML bundler + HMR under BASE, forwarding the host's routes to Kestrel on :5000.
import index from './index.html';
import { BASE } from './base';

const HOST = 'localhost:5000';
const FORWARDED = ['/api', '/hubs', '/workspace', '/openapi', '/audio-preview', '/preview-source'];
const ROOT = BASE.replace(/\/$/, '');

type Bridge = { path: string; upstream?: WebSocket; pending: (string | Uint8Array<ArrayBuffer>)[] };

const isForwarded = (pathname: string) =>
  FORWARDED.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));

const server = Bun.serve<Bridge>({
  port: 4300,
  development: { hmr: true, console: true },
  routes: { [ROOT]: index, [`${ROOT}/*`]: index },
  async fetch(req, srv) {
    const url = new URL(req.url);
    if (!isForwarded(url.pathname)) return new Response('Not found', { status: 404 });
    if (req.headers.get('upgrade')?.toLowerCase() === 'websocket') {
      return srv.upgrade(req, { data: { path: url.pathname + url.search, pending: [] } })
        ? undefined
        : new Response('Upgrade failed', { status: 400 });
    }
    const headers = new Headers(req.headers);
    headers.set('host', HOST);
    return fetch(`http://${HOST}${url.pathname}${url.search}`, {
      method: req.method,
      headers,
      body: req.body,
      redirect: 'manual',
      // Pass the host's compressed bytes through untouched, still labelled with their encoding.
      decompress: false,
    });
  },
  // The /hubs bridge: SignalR's WebSocket goes to the host, buffering until the upstream opens.
  websocket: {
    open(ws) {
      const upstream = new WebSocket(`ws://${HOST}${ws.data.path}`);
      upstream.binaryType = 'arraybuffer';
      ws.data.upstream = upstream;
      upstream.onopen = () => {
        for (const m of ws.data.pending.splice(0)) upstream.send(m);
      };
      upstream.onmessage = (e) => ws.send(e.data as string | ArrayBuffer);
      // 1005 and 1006 only report how the upstream closed; they cannot be sent.
      upstream.onclose = (e) =>
        e.code === 1005 || e.code === 1006 ? ws.close() : ws.close(e.code, e.reason);
      upstream.onerror = () => ws.close(1011, 'upstream error');
    },
    message(ws, msg) {
      const data = typeof msg === 'string' ? msg : new Uint8Array(msg);
      const up = ws.data.upstream;
      if (up?.readyState === WebSocket.OPEN) up.send(data);
      else ws.data.pending.push(data);
    },
    close(ws) {
      ws.data.upstream?.close();
    },
  },
});
console.log(`dev server on ${new URL(BASE, server.url).href}`);
