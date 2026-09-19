import { can as domainCan, parseRoleAssignments, type RoleAssignment } from "@lagerkraft/domain";
import { defineStore } from "pinia";
import { createDeviceKey, decryptToken, encryptToken, isStandalone, requestPersistentStorage } from "../auth/crypto";
import { parseJwt } from "../auth/jwt";
import {
  hashPin,
  IDLE_LOCK_MS,
  OFFLINE_WARN_MS,
  PIN_FULL_LOGIN_AFTER,
  PIN_LOCK_AFTER,
  PIN_LOCK_MS,
  verifyPin,
} from "../auth/pin";
import {
  getDb,
  type ArticleRow,
  type DeviceRow,
  type LocationRow,
  type SessionRow,
  type TaskRow,
  type UnitRow,
  type WarehouseRow,
} from "../db";
import { APP_VERSION, platformUrl, syncUrl } from "../env";
import { applyFeedEntries, flushOutbox, flushState, webLock, type FlushHttpResult } from "../sync/flush";
import { enqueueAisleMap } from "../map/enqueueAisle";
import { DEFAULT_PATTERN, generateAisle, type Dims } from "../map/generateAisle";
import { enqueueReceive, enqueueWithTask, logoutLeavesOutbox, oldestPendingAt, pendingCount } from "../sync/outbox";
import { uuidv7 } from "../uuid";

export type LoginResult = "authenticated" | "chooser" | "totp" | "error" | "set-pin";

async function readErrorCode(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { code?: string; error?: string };
    return body.code ?? body.error ?? `http_${res.status}`;
  } catch {
    return `http_${res.status}`;
  }
}

export const useFloorStore = defineStore("floor", {
  state: () => ({
    loaded: false,
    device: null as DeviceRow | null,
    sessions: [] as SessionRow[],
    unlockedUserId: null as string | null,
    accessToken: null as string | null,
    displayName: "",
    warehouseId: "",
    warehouses: [] as WarehouseRow[],
    lastError: null as string | null,
    lastActivityAt: Date.now(),
    lastSyncAt: null as string | null,
    sseConnected: false,
    formDirty: false,
    printInFlight: false,
    updateReady: false,
    pending: 0,
    oldestPending: null as string | null,
    online: typeof navigator === "undefined" ? true : navigator.onLine,
    upgradeBlocked: false,
  }),
  getters: {
    enrolled: (s) => Boolean(s.device),
    unlocked: (s) => Boolean(s.accessToken && s.unlockedUserId),
    revoked: (s) => Boolean(s.device?.revoked),
    claims: (s) => (s.accessToken ? parseJwt(s.accessToken) : null),
    userId(): string | undefined {
      return this.claims?.sub;
    },
    assignments(): RoleAssignment[] {
      return parseRoleAssignments(this.claims?.ra);
    },
    idleLocked(): boolean {
      return this.unlocked && Date.now() - this.lastActivityAt >= IDLE_LOCK_MS;
    },
    expiryWarning(s): boolean {
      const exp = s.accessToken ? parseJwt(s.accessToken).exp : undefined;
      if (!exp) {
        return false;
      }
      const left = exp * 1000 - Date.now();
      return left > 0 && left <= 60 * 60 * 1000;
    },
    offlineWarning(s): boolean {
      if (!s.oldestPending || s.pending === 0) {
        return false;
      }
      return Date.now() - Date.parse(s.oldestPending) >= OFFLINE_WARN_MS;
    },
    upgradeRequired: (s) => s.upgradeBlocked,
  },
  actions: {
    touch(): void {
      this.lastActivityAt = Date.now();
    },
    can(permission: string, warehouseId?: string): boolean {
      return domainCan(this.assignments, permission, warehouseId);
    },
    async hydrate(): Promise<void> {
      const db = getDb();
      this.device = (await db.device.toArray())[0] ?? null;
      this.sessions = await db.sessions.toArray();
      this.warehouses = await db.warehouses.toArray();
      await this.refreshBadge();
      this.loaded = true;
    },
    async refreshBadge(): Promise<void> {
      const db = getDb();
      this.pending = await pendingCount(db);
      this.oldestPending = await oldestPendingAt(db);
    },
    async enroll(code: string, name: string): Promise<boolean> {
      this.lastError = null;
      if (!isStandalone()) {
        this.lastError = "not_standalone";
        return false;
      }
      const deviceId = uuidv7();
      const res = await fetch(`${platformUrl()}/devices/enroll`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code, name, device_id: deviceId }),
      });
      if (!res.ok) {
        this.lastError = await readErrorCode(res);
        return false;
      }
      const body = (await res.json()) as {
        device_id: string;
        device_secret: string;
        warehouse_ids: string[];
        tenant_id: string;
      };
      await requestPersistentStorage();
      const key = await createDeviceKey();
      const row: DeviceRow = {
        id: body.device_id,
        secret: body.device_secret,
        warehouse_ids: body.warehouse_ids ?? [],
        tenant_id: body.tenant_id,
        crypto_key_handle: key,
      };
      await getDb().device.put(row);
      this.device = row;
      return true;
    },
    async login(email: string, password: string, totp?: string): Promise<LoginResult> {
      this.lastError = null;
      const res = await fetch(`${platformUrl()}/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password, totp: totp || null }),
      });
      if (res.status === 401) {
        const code = await readErrorCode(res);
        this.lastError = code;
        if (code === "totp_invalid") {
          return "totp";
        }
        return "error";
      }
      if (!res.ok) {
        this.lastError = await readErrorCode(res);
        return "error";
      }
      const body = (await res.json()) as {
        access_token?: string;
        refresh_token?: string;
        chooser_token?: string;
      };
      if (body.chooser_token) {
        this.lastError = "chooser";
        return "chooser";
      }
      if (!body.access_token || !body.refresh_token) {
        return "error";
      }
      await this.acceptTokens(body.access_token, body.refresh_token, email);
      return "set-pin";
    },
    async acceptTokens(access: string, refresh: string, displayName: string): Promise<void> {
      const claims = parseJwt(access);
      this.accessToken = access;
      this.unlockedUserId = claims.sub;
      this.displayName = displayName;
      this.touch();
      const db = getDb();
      const device = this.device;
      if (!device?.crypto_key_handle || !claims.sub) {
        return;
      }
      const encrypted = await encryptToken(device.crypto_key_handle, refresh);
      const existing = await db.sessions.get(claims.sub);
      const session: SessionRow = {
        user_id: claims.sub,
        display_name: displayName,
        encrypted_refresh_token: encrypted,
        pin_hash: existing?.pin_hash ?? "",
        failed_attempts: 0,
        token_expires_at: claims.exp ? new Date(claims.exp * 1000).toISOString() : null,
      };
      await db.sessions.put({ ...existing, ...session });
      this.sessions = await db.sessions.toArray();
      device.access_token = access;
      await db.device.put(device);
    },
    async setPin(pin: string): Promise<void> {
      if (!this.unlockedUserId) {
        return;
      }
      const db = getDb();
      const session = await db.sessions.get(this.unlockedUserId);
      if (!session) {
        return;
      }
      session.pin_hash = await hashPin(pin);
      session.failed_attempts = 0;
      session.locked_until = null;
      session.full_login_required = false;
      await db.sessions.put(session);
      this.sessions = await db.sessions.toArray();
    },
    async unlock(userId: string, pin: string): Promise<"ok" | "locked" | "full" | "invalid"> {
      const db = getDb();
      const session = await db.sessions.get(userId);
      const device = this.device;
      if (!session || !device) {
        return "invalid";
      }
      if (session.full_login_required) {
        return "full";
      }
      if (session.locked_until && Date.parse(session.locked_until) > Date.now()) {
        return "locked";
      }
      const ok = session.pin_hash ? await verifyPin(pin, session.pin_hash) : false;
      if (!ok) {
        session.failed_attempts += 1;
        if (session.failed_attempts >= PIN_FULL_LOGIN_AFTER) {
          session.full_login_required = true;
        } else if (session.failed_attempts >= PIN_LOCK_AFTER) {
          session.locked_until = new Date(Date.now() + PIN_LOCK_MS).toISOString();
        }
        await db.sessions.put(session);
        this.sessions = await db.sessions.toArray();
        return session.full_login_required ? "full" : session.locked_until ? "locked" : "invalid";
      }
      session.failed_attempts = 0;
      session.locked_until = null;
      await db.sessions.put(session);
      if (navigator.onLine) {
        const res = await fetch(`${platformUrl()}/auth/pin-unlock`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ device_id: device.id, user_id: userId, pin }),
        });
        if (res.ok) {
          const body = (await res.json()) as { access_token: string; refresh_token: string };
          await this.acceptTokens(body.access_token, body.refresh_token, session.display_name);
          return "ok";
        }
        if (res.status === 401) {
          const code = await readErrorCode(res);
          if (code === "pin_full_login_required") {
            return "full";
          }
        }
      }
      if (!device.crypto_key_handle) {
        return "invalid";
      }
      try {
        await decryptToken(device.crypto_key_handle, session.encrypted_refresh_token);
      } catch {
        return "invalid";
      }
      this.accessToken = device.access_token ?? this.accessToken;
      this.unlockedUserId = userId;
      this.displayName = session.display_name;
      this.touch();
      return "ok";
    },
    async logout(): Promise<void> {
      await logoutLeavesOutbox(getDb());
      this.unlockedUserId = null;
      this.accessToken = null;
      this.displayName = "";
      this.sessions = await getDb().sessions.toArray();
    },
    lock(): void {
      this.unlockedUserId = null;
      this.accessToken = null;
    },
    async markRevoked(): Promise<void> {
      if (!this.device) {
        return;
      }
      this.device.revoked = true;
      await getDb().device.put(this.device);
    },
    async authedFetch(input: string, init: RequestInit = {}): Promise<Response> {
      const headers = new Headers(init.headers);
      if (this.accessToken) {
        headers.set("Authorization", `Bearer ${this.accessToken}`);
      }
      headers.set("X-Lagerkraft-App-Version", APP_VERSION);
      return fetch(input, { ...init, headers });
    },
    async loadWarehouses(): Promise<void> {
      const res = await this.authedFetch(`${syncUrl()}/sync/snapshot?entity=Warehouse`);
      if (res.status === 410) {
        await this.markRevoked();
        return;
      }
      if (!res.ok) {
        return;
      }
      const body = (await res.json()) as { items?: WarehouseRow[] };
      const db = getDb();
      await db.transaction("rw", db.warehouses, async () => {
        await db.warehouses.clear();
        for (const item of body.items ?? []) {
          if (item.id) {
            await db.warehouses.put(item);
          }
        }
      });
      this.warehouses = await db.warehouses.toArray();
    },
    async loadSnapshot(warehouseId: string): Promise<void> {
      const [taskRes, locRes, articleRes, unitRes] = await Promise.all([
        this.authedFetch(`${syncUrl()}/sync/snapshot?warehouse=${warehouseId}&entity=Task`),
        this.authedFetch(`${syncUrl()}/sync/snapshot?warehouse=${warehouseId}&entity=Location`),
        this.authedFetch(`${syncUrl()}/sync/snapshot?warehouse=${warehouseId}&entity=Article`),
        this.authedFetch(`${syncUrl()}/sync/snapshot?warehouse=${warehouseId}&entity=UnitOfMeasure`),
      ]);
      if (taskRes.status === 410 || locRes.status === 410 || articleRes.status === 410 || unitRes.status === 410) {
        await this.markRevoked();
        return;
      }
      if (!taskRes.ok) {
        return;
      }
      const epoch = taskRes.headers.get("Lagerkraft-Feed-Epoch") ?? "";
      const taskBody = (await taskRes.json()) as {
        items?: TaskRow[];
        feed_epoch?: string;
        snapshot_schema?: string;
      };
      const locBody = locRes.ok
        ? ((await locRes.json()) as { items?: LocationRow[] })
        : { items: [] };
      const articleBody = articleRes.ok
        ? ((await articleRes.json()) as { items?: ArticleRow[] })
        : { items: [] };
      const unitBody = unitRes.ok
        ? ((await unitRes.json()) as { items?: UnitRow[] })
        : { items: [] };
      const db = getDb();
      await db.transaction("rw", db.tasks, db.locations, db.articles, db.units, db.cursor, async () => {
        const qtyById = new Map(
          (await db.tasks.where("warehouse_id").equals(warehouseId).toArray()).map((row) => [
            row.id,
            row.requested_qty_base,
          ]),
        );
        await db.tasks.where("warehouse_id").equals(warehouseId).delete();
        await db.locations.where("warehouse_id").equals(warehouseId).delete();
        await db.articles.clear();
        await db.units.clear();
        for (const item of taskBody.items ?? []) {
          await db.tasks.put({
            ...item,
            requested_qty_base: item.requested_qty_base ?? qtyById.get(item.id),
          });
        }
        for (const item of locBody.items ?? []) {
          if (item.id) {
            await db.locations.put(item);
          }
        }
        for (const item of articleBody.items ?? []) {
          if (item.id) {
            await db.articles.put(item);
          }
        }
        for (const item of unitBody.items ?? []) {
          if (item.id) {
            await db.units.put(item);
          }
        }
        await db.cursor.put({
          warehouse_id: warehouseId,
          seq: 0,
          feed_epoch: taskBody.feed_epoch ?? epoch,
          snapshot_schema: taskBody.snapshot_schema ?? "",
        });
      });
      this.warehouseId = warehouseId;
    },
    async applyEntries(
      entries: Array<{
        seq: number;
        entity: string;
        id: string;
        op: string;
        payload: unknown;
        command_id?: string | null;
      }>,
      feedEpoch: string,
    ): Promise<void> {
      if (!this.warehouseId) {
        return;
      }
      const result = await applyFeedEntries(
        getDb(),
        this.warehouseId,
        entries,
        feedEpoch,
        "",
        new Date(),
      );
      if (result === "epoch") {
        await this.loadSnapshot(this.warehouseId);
        await flushOutbox(this.flushDeps());
      }
      if (result === "regression") {
        await flushOutbox(this.flushDeps());
      }
      await this.refreshBadge();
    },
    flushDeps() {
      return {
        db: getDb(),
        now: () => new Date(),
        online: this.online,
        lock: webLock,
        onRevoked: () => {
          void this.markRevoked();
        },
        onUpgradeRequired: () => {
          this.upgradeBlocked = true;
          flushState.upgradeRequired = true;
        },
        postCommands: (batch: Parameters<typeof this.postCommands>[0]) => this.postCommands(batch),
      };
    },
    async postCommands(batch: {
      now: string;
      commands: Array<{
        id: string;
        type: string;
        v: number;
        payload: unknown;
        occurred_at: string;
        device_id: string;
        user_id: string;
      }>;
    }): Promise<FlushHttpResult> {
      const pendingAfter = Math.max(0, this.pending - batch.commands.length);
      const res = await this.authedFetch(`${syncUrl()}/sync/commands`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          now: batch.now,
          commands: batch.commands,
          pending_count_after: pendingAfter,
          oldest_pending_occurred_at: this.oldestPending,
          sse_connected: this.sseConnected,
        }),
      });
      if (res.status === 410) {
        await this.markRevoked();
        return { status: 410 };
      }
      if (res.status === 426) {
        this.upgradeBlocked = true;
        flushState.upgradeRequired = true;
        return { status: 426 };
      }
      let results: FlushHttpResult["results"] = [];
      if (res.headers.get("content-type")?.includes("json")) {
        const body = (await res.json()) as {
          results?: FlushHttpResult["results"];
          min_app_version?: number;
          feed_epoch?: string;
        };
        results = body.results ?? [];
        this.lastSyncAt = new Date().toISOString();
        return { status: res.status, results, min_app_version: body.min_app_version, feed_epoch: body.feed_epoch };
      }
      return { status: res.status, results };
    },
    async flush(): Promise<void> {
      await flushOutbox(this.flushDeps());
      await this.refreshBadge();
    },
    async mapAisle(input: {
      aisle: string;
      racks: number;
      levels: number;
      bins: number;
      dimsByLevel: Dims[];
    }): Promise<boolean> {
      if (!this.device || !this.unlockedUserId || !this.warehouseId) {
        return false;
      }
      const warehouse = this.warehouses.find((w) => w.id === this.warehouseId);
      const built = generateAisle({
        pattern: warehouse?.code_pattern || DEFAULT_PATTERN,
        aisle: input.aisle,
        racks: input.racks,
        levels: input.levels,
        bins: input.bins,
      });
      await enqueueAisleMap(getDb(), {
        warehouseId: this.warehouseId,
        deviceId: this.device.id,
        userId: this.unlockedUserId,
        built,
        dimsByLevel: input.dimsByLevel,
      });
      await this.refreshBadge();
      if (this.online) {
        await this.flush();
      }
      return true;
    },
    async claimTask(task: TaskRow): Promise<void> {
      if (!this.device || !this.unlockedUserId) {
        return;
      }
      await enqueueWithTask(
        getDb(),
        {
          type: "ClaimTask",
          v: 1,
          payload: { task_id: task.id },
          device_id: this.device.id,
          user_id: this.unlockedUserId,
        },
        task.id,
        { status: "claimed", assignee_user_id: this.unlockedUserId },
      );
      await this.refreshBadge();
      if (this.online) {
        await this.flush();
      }
    },
    async completeTask(task: TaskRow): Promise<void> {
      if (!this.device || !this.unlockedUserId) {
        return;
      }
      await enqueueWithTask(
        getDb(),
        {
          type: "CompleteTask",
          v: 1,
          payload: { task_id: task.id },
          device_id: this.device.id,
          user_id: this.unlockedUserId,
        },
        task.id,
        { status: "done" },
      );
      await this.refreshBadge();
      if (this.online) {
        await this.flush();
      }
    },
    async receiveHandlingUnit(input: { articleId: string; qty: string; packagingLevelId?: string }): Promise<string | null> {
      if (!this.device || !this.unlockedUserId || !this.warehouseId) {
        return null;
      }
      const handlingUnitId = uuidv7();
      const taskId = uuidv7();
      const lpn = `LPN-${handlingUnitId.replaceAll("-", "").slice(0, 8).toUpperCase()}`;
      await enqueueReceive(
        getDb(),
        {
          type: "ReceiveHandlingUnit",
          v: 1,
          payload: {
            warehouse_id: this.warehouseId,
            handling_unit_id: handlingUnitId,
            task_id: taskId,
            lpn,
            article_id: input.articleId,
            qty_base: input.qty,
            packaging_level_id: input.packagingLevelId,
          },
          device_id: this.device.id,
          user_id: this.unlockedUserId,
        },
        {
          id: taskId,
          warehouse_id: this.warehouseId,
          type: "putaway",
          status: "open",
          created_at: new Date().toISOString(),
          requested_qty_base: input.qty,
        },
      );
      await this.refreshBadge();
      if (this.online) {
        await this.flush();
      }
      return taskId;
    },
    async confirmPutaway(task: TaskRow, locationId: string): Promise<void> {
      if (!this.device || !this.unlockedUserId) {
        return;
      }
      await enqueueWithTask(
        getDb(),
        {
          type: "ConfirmPutaway",
          v: 1,
          payload: {
            task_id: task.id,
            location_id: locationId,
            qty: task.requested_qty_base ?? "1",
          },
          device_id: this.device.id,
          user_id: this.unlockedUserId,
        },
        task.id,
        { status: "done" },
      );
      await this.refreshBadge();
      if (this.online) {
        await this.flush();
      }
    },
    async sendBeacon(): Promise<void> {
      if (!this.online || !this.accessToken) {
        return;
      }
      await this.authedFetch(`${syncUrl()}/sync/beacon`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          pending_count: this.pending,
          oldest_pending_occurred_at: this.oldestPending,
          last_sync_at: this.lastSyncAt,
          sse_connected: this.sseConnected,
        }),
      });
    },
  },
});
