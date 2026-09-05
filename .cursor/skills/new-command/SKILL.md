---
name: new-command
description: Adds a new wms-core command type or bumps an existing command's version across contracts, backend handler, upcaster, fixtures, TypeScript types and tests. Use when adding a floor command (ConfirmPutaway, ReportDeviation), changing a command payload, or when asked to "add a command" or "bump v on a command".
---

# New command or command version

A command is a contract first, code second. Both sides generate from `contracts/commands/`.

## New command type

```
- [ ] 1. contracts/commands/<Type>/v1.json  (JSON Schema 2020-12, snake_case, additionalProperties true, quantities as decimal strings)
- [ ] 2. contracts/commands/<Type>/fixtures/v1.json  (valid instance)
- [ ] 3. backend/src/WmsCore/<Module>/Commands/<Type>/Command.cs, Validator.cs, Handler.cs  (slice shape in dotnet-backend.mdc)
- [ ] 4. Register in CommandRegistry with current version 1
- [ ] 5. Handler writes change_log entries as full entity state; rule breaches -> Deviation(policy_override); impossible states -> Result.Reject(code)
- [ ] 6. Tests: happy path, impossible-state rejection (asserts the deviation row), occurred_at authorization boundary, idempotent retry
- [ ] 7. pnpm -C frontend gen  (regenerates packages/domain types); commit the generated file
- [ ] 8. Floor app: outbox writer for the command + optimistic update in ONE Dexie transaction
- [ ] 9. Permission: which resource.action gates it; add to Lagerkraft.Shared.Permissions if new, regenerate the frontend map
```

## Version bump (only when a field becomes required, changes meaning or type, or is removed)

```
- [ ] 1. contracts/commands/<Type>/v<N>.json  (do not edit v<N-1>)
- [ ] 2. fixtures/v<N>.json and fixtures/v<N-1>-upcast.json  (what v<N-1> must become)
- [ ] 3. Upcaster v<N-1> -> v<N>: pure function, no I/O, defaults for new required fields
- [ ] 4. Handler now takes v<N>; registry current version = N
- [ ] 5. Golden test replays every fixture through the chain; it must pass without edits to the test
- [ ] 6. Client emits v<N>; CI check confirms the server lists it
- [ ] 7. Note the bump in the spec changelog only if the semantics changed for users
```

Adding an optional field is **not** a bump: edit the current schema, add the field to the fixture, done.

## Schema template

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "lagerkraft:commands/ClaimTask/v1",
  "type": "object",
  "additionalProperties": true,
  "required": ["task_id"],
  "properties": {
    "task_id": { "type": "string", "format": "uuid" }
  }
}
```

## Reminders

- Client-generated ids for anything the command creates: the payload carries them, the handler accepts them.
- Never return `unknown` for validation; never return `rejected` for tenant state.
- The device never migrates queued payloads; the server upcasts.
