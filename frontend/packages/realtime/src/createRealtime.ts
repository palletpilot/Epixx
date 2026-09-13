export type ChangeEntry = {
  seq: number;
  entity: string;
  id: string;
  op: string;
  payload: unknown;
  occurred_at: string;
  actor?: string | null;
  command_id?: string | null;
};

export type SseListener = (ev: { data: string; lastEventId?: string }) => void;

export type EventSourceLike = {
  url: string;
  close(): void;
  addEventListener(type: string, listener: SseListener): void;
};

export type EventSourceConstructor = new (
  url: string,
  init?: { headers?: Record<string, string> },
) => EventSourceLike;

export type RealtimeHandle = {
  close: () => void;
};

export type RealtimeOptions = {
  url: string;
  token: string;
  warehouse?: string;
  since?: number;
  onEntries: (entries: ChangeEntry[]) => void;
  onResync?: (payload: { reason?: string }) => void;
  EventSource?: EventSourceConstructor;
  fetch?: typeof globalThis.fetch;
};

const HEARTBEAT_TIMEOUT_MS = 45_000;
const POLL_INTERVAL_MS = 30_000;
const BATCH_MS = 100;
const DEFAULT_RETRY_MS = 3_000;

export class FetchEventSource implements EventSourceLike {
  url: string;
  readonly #abort = new AbortController();
  readonly #listeners = new Map<string, SseListener[]>();
  #closed = false;
  readonly #fetch: typeof globalThis.fetch;

  constructor(url: string, init?: { headers?: Record<string, string>; fetch?: typeof globalThis.fetch }) {
    this.url = url;
    this.#fetch = init?.fetch ?? globalThis.fetch.bind(globalThis);
    void this.#run(init?.headers ?? {});
  }

  addEventListener(type: string, listener: SseListener): void {
    const list = this.#listeners.get(type) ?? [];
    list.push(listener);
    this.#listeners.set(type, list);
  }

  close(): void {
    this.#closed = true;
    this.#abort.abort();
  }

  #emit(type: string, data = "", lastEventId?: string): void {
    for (const listener of this.#listeners.get(type) ?? []) {
      listener({ data, lastEventId });
    }
  }

  async #run(headers: Record<string, string>): Promise<void> {
    try {
      const res = await this.#fetch(this.url, {
        headers: { Accept: "text/event-stream", ...headers },
        signal: this.#abort.signal,
      });
      if (!res.ok || !res.body) {
        this.#emit("error");
        return;
      }
      this.#emit("open");
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buf = "";
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        buf += decoder.decode(value, { stream: true });
        buf = this.#consume(buf);
      }
      if (!this.#closed) {
        this.#emit("error");
      }
    } catch {
      if (!this.#closed) {
        this.#emit("error");
      }
    }
  }

  #consume(buf: string): string {
    const parts = buf.split("\n\n");
    const rest = parts.pop() ?? "";
    for (const block of parts) {
      this.#dispatchBlock(block.replace(/\r/g, ""));
    }
    return rest;
  }

  #dispatchBlock(block: string): void {
    let id: string | undefined;
    let event = "message";
    const data: string[] = [];
    let retry: string | undefined;
    let hadComment = false;
    for (const line of block.split("\n")) {
      if (line.startsWith(":")) {
        hadComment = true;
        continue;
      }
      const colon = line.indexOf(":");
      const field = colon === -1 ? line : line.slice(0, colon);
      let value = colon === -1 ? "" : line.slice(colon + 1);
      if (value.startsWith(" ")) {
        value = value.slice(1);
      }
      if (field === "id") {
        id = value;
      } else if (field === "event") {
        event = value;
      } else if (field === "data") {
        data.push(value);
      } else if (field === "retry") {
        retry = value;
      }
    }
    if (retry !== undefined) {
      this.#emit("retry", retry);
    }
    if (hadComment) {
      this.#emit("heartbeat");
    }
    if (data.length > 0) {
      this.#emit(event, data.join("\n"), id);
    }
  }
}

export function createRealtime(opts: RealtimeOptions): RealtimeHandle {
  const EventSourceImpl = opts.EventSource ?? FetchEventSource;
  const fetchFn = opts.fetch ?? globalThis.fetch.bind(globalThis);
  let closed = false;
  let es: EventSourceLike | null = null;
  let lastEventId = opts.since != null ? String(opts.since) : undefined;
  let retryMs = DEFAULT_RETRY_MS;
  let heartbeatTimer: ReturnType<typeof setTimeout> | undefined;
  let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let sseOpen = false;
  const buffer: ChangeEntry[] = [];

  function flush(): void {
    if (buffer.length === 0) {
      return;
    }
    opts.onEntries(buffer.splice(0, buffer.length));
  }

  function noteActivity(): void {
    if (heartbeatTimer !== undefined) {
      clearTimeout(heartbeatTimer);
    }
    heartbeatTimer = setTimeout(onDead, HEARTBEAT_TIMEOUT_MS);
  }

  function onDead(): void {
    if (closed) {
      return;
    }
    es?.close();
    es = null;
    sseOpen = false;
    connect();
  }

  function scheduleReconnect(): void {
    if (closed || reconnectTimer !== undefined) {
      return;
    }
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined;
      connect();
    }, retryMs);
  }

  function startPolling(): void {
    if (pollTimer !== undefined || closed) {
      return;
    }
    void pollOnce();
    pollTimer = setInterval(() => {
      void pollOnce();
    }, POLL_INTERVAL_MS);
  }

  function stopPolling(): void {
    if (pollTimer === undefined) {
      return;
    }
    clearInterval(pollTimer);
    pollTimer = undefined;
  }

  async function pollOnce(): Promise<void> {
    if (closed || sseOpen) {
      return;
    }
    try {
      const res = await fetchFn(changesUrl(opts.url, opts.warehouse, lastEventId), {
        headers: { Authorization: `Bearer ${opts.token}` },
      });
      if (!res.ok) {
        return;
      }
      const body = (await res.json()) as { entries?: ChangeEntry[] };
      for (const entry of body.entries ?? []) {
        buffer.push(entry);
        lastEventId = String(entry.seq);
      }
    } catch {
      // stay on the poll interval
    }
  }

  function connect(): void {
    if (closed) {
      return;
    }
    es?.close();
    const headers: Record<string, string> = {
      Authorization: `Bearer ${opts.token}`,
    };
    if (lastEventId !== undefined) {
      headers["Last-Event-ID"] = lastEventId;
    }
    const source = new EventSourceImpl(realtimeUrl(opts.url, opts.warehouse, lastEventId), { headers });
    es = source;

    source.addEventListener("open", () => {
      sseOpen = true;
      stopPolling();
      noteActivity();
    });
    source.addEventListener("heartbeat", () => {
      noteActivity();
    });
    source.addEventListener("retry", (ev) => {
      const n = Number(ev.data);
      if (Number.isFinite(n) && n >= 0) {
        retryMs = n;
      }
    });
    source.addEventListener("change", (ev) => {
      noteActivity();
      pushChange(ev.data, ev.lastEventId);
    });
    source.addEventListener("resync", (ev) => {
      noteActivity();
      let reason: string | undefined;
      try {
        reason = (JSON.parse(ev.data) as { reason?: string }).reason;
      } catch {
        reason = undefined;
      }
      opts.onResync?.({ reason });
    });
    source.addEventListener("error", () => {
      sseOpen = false;
      source.close();
      if (closed) {
        return;
      }
      startPolling();
      scheduleReconnect();
    });

    noteActivity();
  }

  function pushChange(data: string, eventId?: string): void {
    try {
      const parsed = JSON.parse(data) as ChangeEntry;
      buffer.push(parsed);
      if (eventId !== undefined && eventId !== "") {
        lastEventId = eventId;
      } else if (parsed.seq != null) {
        lastEventId = String(parsed.seq);
      }
    } catch {
      // ignore malformed payloads
    }
  }

  const batchTimer = setInterval(flush, BATCH_MS);
  connect();

  return {
    close() {
      closed = true;
      sseOpen = false;
      es?.close();
      es = null;
      stopPolling();
      if (heartbeatTimer !== undefined) {
        clearTimeout(heartbeatTimer);
      }
      if (reconnectTimer !== undefined) {
        clearTimeout(reconnectTimer);
      }
      clearInterval(batchTimer);
      flush();
    },
  };
}

function realtimeUrl(base: string, warehouse: string | undefined, since: string | undefined): string {
  return withQuery(base, { warehouse, since });
}

function changesUrl(realtime: string, warehouse: string | undefined, since: string | undefined): string {
  return withQuery(realtime.replace(/\/realtime(?=$|\?)/, "/sync/changes"), { warehouse, since });
}

function withQuery(base: string, params: Record<string, string | undefined>): string {
  const absolute = /^https?:/i.test(base);
  const url = new URL(base, "http://pn.invalid");
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") {
      url.searchParams.set(key, value);
    }
  }
  return absolute ? url.toString() : `${url.pathname}${url.search}`;
}
