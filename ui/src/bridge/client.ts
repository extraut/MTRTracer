import { L } from '../i18n';
import type { EventMap, RequestMap } from './protocol';

type Listener = (payload: unknown) => void;

interface HostTransport {
  post(message: unknown): void;
  subscribe(handler: (message: unknown) => void): void;
}

interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', handler: (e: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewHost };
  }
}

export class BridgeError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

const listeners = new Map<string, Set<Listener>>();
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
let nextId = 1;
let transport: HostTransport | null = null;

const wsParams = typeof window !== 'undefined' ? new URLSearchParams(window.location.search) : null;
const wsToken = wsParams?.get('bridge') === 'ws' ? wsParams.get('token') : null;

/** webview2: Windows app · ws: Linux app (Photino window or browser) · mock: plain browser demo */
export const hostKind: 'webview2' | 'ws' | 'mock' =
  typeof window !== 'undefined' && window.chrome?.webview ? 'webview2' : wsToken ? 'ws' : 'mock';
export const isNativeHost = hostKind !== 'mock';

/** WebSocket bridge to the Linux host on 127.0.0.1, with a per-launch token and automatic reconnect. */
function createWsTransport(token: string): HostTransport {
  const handlers: ((m: unknown) => void)[] = [];
  const queue: string[] = [];
  let ws: WebSocket | null = null;
  let delay = 250;
  const connect = () => {
    const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/bridge?token=${encodeURIComponent(token)}`;
    const sock = new WebSocket(url);
    ws = sock;
    sock.onopen = () => {
      delay = 250;
      while (queue.length) sock.send(queue.shift()!);
    };
    sock.onmessage = (e) => {
      try {
        const m = JSON.parse(String(e.data));
        handlers.forEach((h) => h(m));
      } catch {
        /* ignore malformed */
      }
    };
    sock.onclose = () => {
      ws = null;
      setTimeout(connect, delay);
      delay = Math.min(4000, delay * 2);
    };
  };
  connect();
  return {
    post: (msg) => {
      const text = JSON.stringify(msg);
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(text);
      else queue.push(text);
    },
    subscribe: (h) => handlers.push(h),
  };
}

function dispatch(message: unknown) {
  const m = message as { kind?: string; id?: number; ok?: boolean; result?: unknown; error?: { code: string; message: string }; type?: string; payload?: unknown };
  if (m.kind === 'res' && typeof m.id === 'number') {
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result);
    else p.reject(new BridgeError(m.error?.code ?? 'error', m.error?.message ?? L('Ошибка', 'Error')));
  } else if (m.kind === 'evt' && m.type) {
    listeners.get(m.type)?.forEach((l) => l(m.payload));
  }
}

async function getTransport(): Promise<HostTransport> {
  if (transport) return transport;
  const wv = window.chrome?.webview;
  if (wv) {
    transport = {
      post: (msg) => wv.postMessage(msg),
      subscribe: (h) => wv.addEventListener('message', (e) => h(e.data)),
    };
  } else if (wsToken) {
    transport = createWsTransport(wsToken);
  } else {
    // Running in a normal browser (vite dev / preview): simulated host with realistic data.
    const { createMockHost } = await import('./mock');
    transport = createMockHost();
  }
  transport.subscribe(dispatch);
  return transport;
}

export async function request<K extends keyof RequestMap>(
  type: K,
  ...args: RequestMap[K][0] extends undefined ? [payload?: RequestMap[K][0]] : [payload: RequestMap[K][0]]
): Promise<RequestMap[K][1]> {
  // On Linux the page itself can write to the clipboard (127.0.0.1 is a secure context).
  if (type === 'clipboard.copy' && hostKind !== 'webview2' && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText((args[0] as { text: string }).text);
      return null as RequestMap[K][1];
    } catch {
      /* fall back to the host (wl-copy / xclip) */
    }
  }
  const t = await getTransport();
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    t.post({ kind: 'req', id, type, payload: args[0] });
  });
}

export function on<K extends keyof EventMap>(type: K, handler: (payload: EventMap[K]) => void): () => void {
  let set = listeners.get(type);
  if (!set) listeners.set(type, (set = new Set()));
  set.add(handler as Listener);
  void getTransport();
  return () => set!.delete(handler as Listener);
}
