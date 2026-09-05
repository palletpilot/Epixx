---
name: use-case-review
description: Scrutinises a design by tracing concrete end-to-end use cases through it, one at a time, and folding accepted fixes back into the spec. Use when the user asks to "run through the plan use case by use case", stress-test a spec, review a sub-project design before implementation, or check that a flow "holds up".
---

# Use-case review

This is how the Lagerkraft architecture spec was hardened. It finds the gaps a section-by-section read misses, because a use case crosses sections.

## Workflow

1. **Roster.** List 10 to 15 use cases in the order a real customer meets them, from first contact to offboarding. Include at least: one happy path per sub-project, one offline path, one failure or recovery path, one money path, one permissions path. Show the roster, then start with number 1 without waiting.
2. **Per use case, one turn:**
   - *Trace*: write the flow as the spec says it happens, naming the sections, tables and endpoints involved. Short paragraph.
   - *Where it doesn't hold*: list concrete gaps. Each gap is one bullet: what happens, why it is wrong (data loss, silent duplication, lockout, wrong balance, dead end for the user), and the **fix** in the same bullet. Prefer the fix that reuses an existing mechanism.
   - *What holds*: one line naming what was checked and survived, so the reader knows it was looked at.
   - Ask for a verdict with `AskQuestion`: accept all / accept but change one / another gap you missed. Product decisions (a v1 non-goal, a pricing rule) get their own question.
3. **Fold in** accepted fixes with the `spec-change` skill before moving to the next use case. Do not batch.
4. **Close**: list the cross-cutting principles that emerged (things three or more fixes had in common) and add them to the spec's opening section and to `.cursor/rules/lagerkraft-project.mdc`.

## What to look for in a trace

- An id, code or sequence minted in one place and needed in another before they can talk (offline).
- A physical fact the system would reject.
- Something evaluated "now" that should be evaluated at `occurred_at`.
- A long-lived connection or cache that misses a revocation.
- A state flip that can happen mid-shift.
- Two mechanisms described for the same thing (two passphrase stories, two id rules).
- A step that assumes a human is still there (contact deleted before the certificate is sent).
- A rule stated for the happy path with no word about the retry, the duplicate or the stale client.
- A provider capability assumed but never verified (needs a spike).

## Output discipline

Five to eight gaps per use case is normal. More than ten means the section needs a rewrite, say so. Fewer than three means look harder at offline, concurrency and revocation.
