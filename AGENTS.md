# VibeMiner agent instructions

These rules apply to every AI coding agent working in this repo (Claude Code, Codex and others). `CLAUDE.md` imports this file. User instructions take precedence over this file.

VibeMiner explores Blizzard CDN data for WoW. It is an Electron + React desktop app with a C# .NET 10 engine; see [README.md](README.md) and [docs/](docs/README.md). It continues the Java app at github.com/aircan80/vibeminer_java. Port useful workflows deliberately rather than translating classes; see [docs/MIGRATION.md](docs/MIGRATION.md).

## Owner
The human owner (aircan80) has the final say on product, scope and merges. Ask when a choice changes product behaviour. Routine work inside an agreed issue needs no approval.

## Roles
Role definitions live in `.agents/rules/`. Before acting as a role, read its file.

| Role | File |
| --- | --- |
| Product manager | [.agents/rules/product-manager.md](.agents/rules/product-manager.md) |
| Developer | [.agents/rules/developer.md](.agents/rules/developer.md) |
| Reviewer | [.agents/rules/reviewer.md](.agents/rules/reviewer.md) |
| QA testing | [.agents/rules/qa-testing.md](.agents/rules/qa-testing.md) |
| WoW datamining expert | [.agents/rules/wow-datamining-expert.md](.agents/rules/wow-datamining-expert.md) |
| UI expert | [.agents/rules/ui-expert.md](.agents/rules/ui-expert.md) |
| Usability expert | [.agents/rules/usability-expert.md](.agents/rules/usability-expert.md) |

Before touching issues, branches or merges, read [.agents/rules/workflow.md](.agents/rules/workflow.md).

QA, the reviewer and the three experts must not review work written in the same session. Run them as fresh subagents or sessions.

## Two folders
- `docs/` holds permanent docs: architecture, decisions, formats, how things work.
- `.agents/rules/` holds role definitions.
- `.agents/workspace/` is gitignored throwaway scratch space.
- Never commit execution logs, session transcripts, progress notes, plans, handoffs or review files. Anything durable goes in `docs/`; anything still to do goes in a GitHub issue.
- Write nothing outside source, tests, `docs/` and `.agents/`.

## Write like a human
- Issues, PRs, comments and docs are short, blunt and pragmatic.
- Feature issues: 1-3 sentences of plain-English context, then short `- [ ]` bullets for acceptance criteria and technical steps.
- No corporate or AI filler ("comprehensive", "seamless", "robust", "Furthermore"). No "As a user, I want..." unless strictly necessary.
- Issue comments are 1-3 factual sentences, e.g. "Fixed token validation bug in AuthService. Ready for QA."
- One review pass per code change. Never review a plan or another review.

## GitHub and git
- Never use `gh project`. Status lives only in issue labels.
- Status labels `in-progress`, `ready-for-qa` and `blocked` are exclusive. Adding one removes the others in the same command:
  `gh issue edit N --add-label ready-for-qa --remove-label in-progress,blocked`
- Type labels `bug`, `enhancement` and `architecture` can sit alongside a status label. Milestones are phases or releases.
- One feature issue is one real, testable deliverable: a vertical slice, delivered as one squashed commit. Untestable chores are `- [ ]` checkboxes inside it.
- Commits reference their issue with `Refs #N`, never `Closes #N` or `Fixes #N`.
- Only the QA role closes issues.
- A bug found while its feature issue is open goes in a comment on that issue, which flips back to `in-progress`. Only bugs in closed features or existing code get their own `bug` issue.
- Always pass `--title` and `--body` to `gh issue create`.
- Commit identity: `aircan80 <329744803+aircan80@users.noreply.github.com>`. Don't change git config.
- Merging and pushing a QA-passed commit to `main` needs no further OK. Releases and repo settings need the owner's explicit OK. Never force-push.
- No candidate pushes. QA's local run on the exact sha is the gate; GitHub CI runs on `main` after each push, and a failing run there becomes a `bug` issue right away. See [workflow](.agents/rules/workflow.md).

## Engineering rules
Area details load automatically in Claude Code. Other tools should read them before editing:
- [.claude/rules/dotnet.md](.claude/rules/dotnet.md) for `src/` and backend tests
- [.claude/rules/electron.md](.claude/rules/electron.md) for `apps/desktop/`, desktop tests and contracts

The short version:
- Write our own acquisition, TACT/BLTE/DB2 parsing and asset decoding. Third-party game parsers are reference only.
- Async end to end, with every resource bounded and cancellation, progress, terminal outcome and stale results defined.
- C# owns CDN access, SQL, parsing, migrations and publication. The renderer only presents.
- Users can explore while builds download. Interactive data takes priority through the one shared acquisition pipeline; see [the priority scenarios](docs/ASYNC-AND-JOBS.md).
- Preserve exact build/locale/source identity. Missing, unknown, unsupported, encrypted, corrupt, partial and absent are different states.
- Verify before publishing or marking anything ready.
- New behaviour gets meaningful tests and every fix gets a regression test. See [docs/TESTING.md](docs/TESTING.md).
- Official CDN sources and app-owned storage only. Never read game installs, Battle.net directories or game caches. No game content or credentials in source, fixtures, logs or packages. The only other sources are WoWDBDefs table definitions ([D081](docs/DECISIONS.md)), the wow-listfile file names ([D105](docs/DECISIONS.md)) and the wago.tools build list ([D106](docs/DECISIONS.md)), each behind its own Settings switch and never game data.
- Record decisions in [docs/DECISIONS.md](docs/DECISIONS.md). Supersede old rows; don't silently rewrite them.
- Run the relevant checks before handoff and report actual results, including checks you didn't run. Never invent build or test commands; see [BUILDING.md](BUILDING.md) and `package.json`.

## Safety
- Never reset, delete, migrate or restart the owner's app profile or data without explicit instruction. A granted reset is one-time only. Test and QA runs set `VIBEMINER_DATA_DIR`.
- Never discuss an undisclosed vulnerability in public issues, comments or commit messages. The owner triages security reports; fixes go in the advisory's private fork. See [SECURITY.md](SECURITY.md).
