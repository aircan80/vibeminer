# Workflow

How an issue moves from idea to closed. Read this before touching issues, branches or merges.

## Labels
- Status (at most one): `in-progress`, `ready-for-qa`, `blocked`. No status label = backlog.
- Always swap status in one command and remove both others:
  `gh issue edit N --add-label ready-for-qa --remove-label in-progress,blocked`
- Type (alongside status): `bug`, `enhancement`, `architecture`. Milestones = phases/releases.
- Never use `gh project`. Always pass `--title` and `--body` to `gh issue create` (without them gh opens an editor and hangs).

## Steps
1. **product-manager** writes the issue: 1-3 sentences of context, `- [ ]` acceptance criteria, `- [ ]` technical steps, and a review line for each trigger that applies: `- [ ] WoW review`, `- [ ] UI review`, `- [ ] Usability review`. One issue = one testable slice. Chores are checkboxes, not issues.
2. **developer** sets `in-progress` and works on a local branch `issue-N`.
3. When done, the developer squashes the branch to one commit on top of current `main`. The message ends with `Refs #N` (never `Closes`/`Fixes`).
   - The commit stays local. Nothing is pushed for CI. Never force-push anything.
4. The developer calls **reviewer** and each listed expert. Each one posts one verdict comment on the issue against `git diff main...issue-N`. Fix blockers on the same branch, re-squash, and re-request only the reviewer who raised them.
5. All verdicts pass → the developer rebases onto `main`, runs `npm run build` and the unit tests of the layer it changed (`npm run test:layers`, see [TESTING.md](../../docs/TESTING.md#layer-scoped-unit-tests)), and pushes: `git push origin <sha>:refs/heads/main`, then `git merge --ff-only`. No owner OK and no full gate needed. Flip the issue to `ready-for-qa`.
   - If conflict fixes changed code, the reviewer reviews again first.
   - Several passed issues can go in one push, stacked in merge order.
6. CI (`.github/workflows/ci.yml`) runs everything on Linux after each push. That's the full run per change. Windows and macOS are parked.
7. **qa-testing** checks the issue on `main`, in parallel with further work.
   - It tests the acceptance criteria and the applicable checks in [qa-testing](qa-testing.md), running only the tests and harnesses those need. No full `npm test`/e2e gate.
   - Findings go in a comment and get small follow-up fixes. Revert only if serious. A non-blocking finding can go in a follow-up issue.
   - When the criteria hold and CI is green, QA ticks the criteria and review lines, posts one line ("Passed: <sha>."), removes the status label and closes the issue. Only QA closes issues.
8. **Full gate** (`npm ci`, `npm run build`, `npm test`, `npm run test:e2e`) runs only at milestones (end of a phase or before a release) and when the owner asks. Heavy harnesses (scale, kill loops, bit flips) rerun only when their layer changes.

## Bugs
- A failing CI run on `main`: new `bug` issue right away, naming the run and sha.
- Found while the feature issue is open: comment on that issue and flip it to `in-progress`.
- Found in a closed feature or existing code: new issue with the `bug` label.

## Outside pull requests
The owner reviews and merges them. The same QA rule applies before merge.

## Writing
Issue comments are 1-3 factual sentences, e.g. "Fixed token validation bug in AuthService. Ready for QA." No filler, no restating the issue, no lists of what you inspected.
