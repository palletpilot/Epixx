# Agent entry point

For any AI agent working in this repository (Cursor loads `.cursor/rules` automatically; other tools start here).

1. Read `.cursor/rules/lagerkraft-project.mdc`, `.cursor/rules/workflow.mdc` and `.cursor/rules/ponytail.mdc`. They always apply.
2. The design is `docs/superpowers/specs/2026-09-05-lagerkraft-architecture-design.md`. The work list is `docs/superpowers/plans/`.
3. Path-scoped rules in `.cursor/rules/` cover backend, migrations, the command pipeline, tests, frontend, the floor app, contracts, infra and docs. Read the one matching the files you touch.
4. Repeatable procedures are skills in `.cursor/skills/<name>/SKILL.md`: `implement-plan-task`, `spec-change`, `use-case-review`, `new-command`, `new-slice`, `add-migration`, `review-against-spec`, `dev-loop`.
5. `Epixx/` is frozen. Never edit it.
