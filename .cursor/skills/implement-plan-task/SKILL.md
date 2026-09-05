---
name: implement-plan-task
description: Implements one task from a Lagerkraft implementation plan end to end (read spec, test first, build, mark done, commit). Use when asked to "do task C2", "implement the next plan task", "start on A1", or to continue work from docs/superpowers/plans.
---

# Implement a plan task

## Workflow

Copy and track:

```
- [ ] 1. Locate the task in docs/superpowers/plans/<plan>.md; read its Files, Steps, Tests, Commit lines
- [ ] 2. Read every spec section the task names (docs/superpowers/specs/...); grep the spec for each mechanism the task uses
- [ ] 3. Read existing code the task touches; list the files you will create or change
- [ ] 4. Write the test(s) the task names first; run them; they must fail
- [ ] 5. Implement the minimum that makes them pass (ponytail ladder)
- [ ] 6. Run build, tests, lint for the trees you touched (commands in workflow.mdc)
- [ ] 7. Break the logic once on purpose, confirm the test catches it, restore
- [ ] 8. Mark the task done in the plan heading: (done <date>, <sha placeholder>)
- [ ] 9. Commit: sp<N>: <task id> - <what>; then fix the sha in the plan in the same commit via amend
- [ ] 10. Summary: what shipped, what was left out and why, any ponytail: ceilings, needs-human items
```

## Rules of engagement

- The plan's file list is the scope. A file outside it needs one sentence of justification in the summary.
- If the task depends on an unfinished task (see "Order and parallelism" in the plan), stop and say which one.
- If the spec is silent on something the task needs, do not invent. Write the question, propose the smallest answer, continue under that assumption, and flag it at the top of the summary.
- Never touch `Epixx/`.
- A task is not done without its test. "Manually verified" is not done.

## Step 4 detail: what a good first test looks like

Pick the test from the task's Tests line that would fail for the most likely bug, not the happy path. For a handler: the impossible-state rejection or the concurrency case. For a job: the idempotency case. For a client: the transaction atomicity case.

## Step 9 detail: commit message

```
sp0: C3 - Task module with claim-work and assignment sweep

ClaimTask uses FOR UPDATE SKIP LOCKED; AssignmentSweepJob releases expired
claims every 60 s and writes change_log entries. Warehouse stub table added.
```

Subject under 72 chars, body says what and why, not how.
