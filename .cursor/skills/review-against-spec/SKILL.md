---
name: review-against-spec
description: Reviews a diff, branch or PR for Lagerkraft against the architecture spec, the five principles and the project rules, producing findings ranked by severity. Use when asked to review changes, check a PR, or verify that an implementation matches the spec or a plan task.
---

# Review against the spec

The question is not "is this good code" but "does this do what the spec says, and only that".

## Procedure

1. Identify the plan task the change claims to implement. Read its Files/Steps/Tests lines and the spec sections it names.
2. Read the diff. For each file, ask: is it in the task's file list? If not, is it justified in the PR text?
3. Run the checklist below. Every finding cites the spec section or rule it violates.
4. Report as: **Blocker** (spec violation, principle violation, data loss risk, missing test), **Should fix** (rule violation, missing edge case the spec names), **Note** (style, naming, opportunity). No praise section.

## Checklist

Principles (`lagerkraft-project.mdc`):
- [ ] A physical-action command is rejected for a rule breach → Blocker (floor is the truth)
- [ ] Server mints an id for a device-created entity → Blocker
- [ ] Authorization uses "now" for an offline command → Blocker
- [ ] Tenant state or cap produces `rejected` instead of `held` → Blocker
- [ ] Tenant object outside `tenants/<tenant_id>/` → Blocker

Pipeline (`command-pipeline.mdc`):
- [ ] Domain write, change_log, outbox, processed_commands not in one transaction → Blocker
- [ ] change_log payload is a diff, not full state → Should fix
- [ ] Rejection without a same-transaction Deviation row → Blocker
- [ ] `v` bumped for an additive change, or not bumped for a breaking one → Should fix
- [ ] Consumer uses a high-water mark instead of processed_events → Blocker

Ownership:
- [ ] Service writes to a database it does not own → Blocker
- [ ] sync-gateway holds state that survives a restart → Blocker

Migrations (`ef-migrations.mdc`):
- [ ] Destructive change without the previous release having dropped the reference → Blocker
- [ ] Data statement in a migration → Blocker
- [ ] Non-concurrent index on an existing table → Should fix

Floor app (`floor-app-offline.mdc`):
- [ ] Outbox and optimistic state in separate transactions → Blocker
- [ ] Any path that clears or rewrites the outbox → Blocker
- [ ] PIN used as key material → Blocker

Tests (`tests-dotnet.mdc`):
- [ ] Task's named tests missing → Blocker
- [ ] Concurrency claim tested with mocks → Should fix
- [ ] Sleep or wall clock in a test → Should fix

Hygiene:
- [ ] Edit under `Epixx/` → Blocker
- [ ] Secret in the diff → Blocker
- [ ] Plan task not marked done, or commit message off format → Note
- [ ] `ponytail:` comment missing on a deliberate ceiling → Note

## Output template

```
## Review: <branch or PR> against <plan task>

### Blockers
- <file:line> — <what> — violates <spec section / rule>. Fix: <one sentence>.

### Should fix
- ...

### Notes
- ...

Verdict: merge / fix blockers first.
```
