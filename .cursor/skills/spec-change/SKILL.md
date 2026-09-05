---
name: spec-change
description: Changes the Lagerkraft architecture spec safely (locate, edit, sweep for contradictions, changelog, update affected plans). Use when the user wants to change a design decision, when code and spec disagree, or when implementation reveals a gap in docs/superpowers/specs.
---

# Change the spec

The spec is the truth other people build from. A change is done when no section disagrees with it.

## Workflow

```
- [ ] 1. State the change in one sentence and why (what broke, what was learned)
- [ ] 2. Grep the spec for every term the change touches; list all hits
- [ ] 3. Edit the primary section
- [ ] 4. Edit every other hit so the spec agrees with itself
- [ ] 5. Check the five principles in .cursor/rules/lagerkraft-project.mdc still hold; if the change alters one, update the rule too
- [ ] 6. Add a dated line to ## Changelog at the end of the spec
- [ ] 7. Grep docs/superpowers/plans for tasks affected; edit their Files/Steps/Tests lines
- [ ] 8. If contracts change shape, note the new version files needed
- [ ] 9. Commit: docs: spec - <change>
```

## Terms that spread across the spec (always grep these)

`held`, `rejected`, `unknown`, `426`, `410`, `423`, `402`, `503`, `feed_epoch`, `snapshot_schema`, `session_version` / `sv`, `occurred_at`, `recorded_at`, `processed_commands`, `processed_events`, `change_log`, `outbox`, `Deviation`, lifecycle state names (`Provisioning`, `Trialing`, `Active`, `PastDue`, `Restricted`, `Suspended`, `TrialExpired`, `Offboarding`, `Deleting`, `Deleted`), `maintenance`, `LocationReservation`, `HandlingUnit`, `qty_base`, `tenants/<tenant_id>/`.

## Grep command

```powershell
rg -n "held|feed_epoch|session_version" docs/superpowers/specs/2026-09-05-lagerkraft-architecture-design.md
```

## What is not a spec change

Wording, typos, a clearer example: edit and commit without a changelog line. A new mechanism, a changed rule, a moved responsibility, a new table or column: changelog line required.
