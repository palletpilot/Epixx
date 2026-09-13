import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createRealtime, type ChangeEntry, type EventSourceConstructor } from "./createRealtime";

type Listener = (ev: { data: string; lastEventId?: string }) => void;

class FakeEventSource {
  static instances: FakeEventSource[] = [];

  url: string;
  lastEventIdHeader: string | undefined;
  authorization: string | undefined;
  closed = false;
  private readonly listeners = new Map<string, Listener[]>();

  constructor(url: string, init?: { headers?: Record<string, string> }) {
    this.url = url;
    this.lastEventIdHeader = init?.headers?.["Last-Event-ID"];
    this.authorization = init?.headers?.Authorization;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: Listener): void {
    const list = this.listeners.get(type) ?? [];
    list.push(listener);
    this.listeners.set(type, list);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data = "", lastEventId?: string): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener({ data, lastEventId });
    }
  }

  emitChange(entry: ChangeEntry): void {
    this.emit("change", JSON.stringify(entry), String(entry.seq));
  }
}

function entry(seq: number): ChangeEntry {
  return {
    seq,
    entity: "Task",
    id: `00000000-0000-7000-8000-00000000000${seq}`,
    op: "update",
    payload: { seq },
    occurred_at: `2026-09-13T08:00:0${seq}.000Z`,
    actor: "anna",
  };
}

describe("createRealtime", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("delivers catch-up then live entries in seq order on one 100ms flush", () => {
    const batches: ChangeEntry[][] = [];
    const handle = createRealtime({
      url: "http://gateway/realtime",
      token: "tok",
      warehouse: "01900000-0000-7000-8000-000000000001",
      since: 0,
      EventSource: FakeEventSource as unknown as EventSourceConstructor,
      onEntries: (batch) => batches.push(batch),
    });

    const es = FakeEventSource.instances[0];
    expect(es).toBeDefined();
    es!.emitChange(entry(1));
    es!.emitChange(entry(2));
    es!.emitChange(entry(3));

    expect(batches).toEqual([]);
    vi.advanceTimersByTime(100);

    expect(batches).toHaveLength(1);
    expect(batches[0]!.map((e) => e.seq)).toEqual([1, 2, 3]);

    handle.close();
  });

  it("reconnects after 45s without a heartbeat and resumes with Last-Event-ID", () => {
    const handle = createRealtime({
      url: "http://gateway/realtime",
      token: "tok",
      warehouse: "01900000-0000-7000-8000-000000000001",
      since: 10,
      EventSource: FakeEventSource as unknown as EventSourceConstructor,
      onEntries: () => undefined,
    });

    const first = FakeEventSource.instances[0];
    expect(first).toBeDefined();
    expect(first!.lastEventIdHeader).toBe("10");
    first!.emitChange(entry(11));
    vi.advanceTimersByTime(40_000);
    first!.emit("heartbeat");

    vi.advanceTimersByTime(44_999);
    expect(FakeEventSource.instances).toHaveLength(1);
    expect(first!.closed).toBe(false);

    vi.advanceTimersByTime(1);
    expect(first!.closed).toBe(true);
    expect(FakeEventSource.instances).toHaveLength(2);
    const second = FakeEventSource.instances[1];
    expect(second!.lastEventIdHeader).toBe("11");
    expect(second!.url).toContain("since=11");
    expect(second!.authorization).toBe("Bearer tok");

    handle.close();
  });

  it("flushes the buffer once per 100ms tick, not per event", () => {
    const batches: ChangeEntry[][] = [];
    const handle = createRealtime({
      url: "http://gateway/realtime",
      token: "tok",
      EventSource: FakeEventSource as unknown as EventSourceConstructor,
      onEntries: (batch) => batches.push(batch),
    });

    const es = FakeEventSource.instances[0]!;
    es.emitChange(entry(1));
    vi.advanceTimersByTime(50);
    es.emitChange(entry(2));
    vi.advanceTimersByTime(49);
    expect(batches).toEqual([]);

    vi.advanceTimersByTime(1);
    expect(batches).toHaveLength(1);
    expect(batches[0]!.map((e) => e.seq)).toEqual([1, 2]);

    es.emitChange(entry(3));
    vi.advanceTimersByTime(100);
    expect(batches).toHaveLength(2);
    expect(batches[1]!.map((e) => e.seq)).toEqual([3]);

    handle.close();
  });
});
