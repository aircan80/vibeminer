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
5. All verdicts pass → the developer flips the issue to `ready-for-qa`.
6. **qa-testing** tests that exact commit in its own clean worktree.
   - QA runs `npm ci`, `npm run build`, `npm test` and `npm run test:e2e` there. That run is the gate; any failure is a fail.
   - Fail: comment the bug on the issue and flip it to `in-progress`.
   - Pass: comment `QA pass at <sha>`.
   - An unticked review line with no passing verdict comment is a fail.
7. The developer runs `git merge --ff-only issue-N`.
   - If `main` moved, rebase the single commit and flip back to `ready-for-qa`. QA retests.
   - If conflict fixes changed code, the reviewer reviews again first.
   - Clean-rebase recheck: when the rebase had no conflicts, `git range-diff` shows the patch unchanged, and the commits `main` gained share no files with it, QA runs `npm ci`, `npm run build`, `npm test` and only the e2e specs the commit touches or that cover the areas both sides changed. A full gate is needed after any conflict or overlap. CI on `main` still runs everything.
   - Batch merges: when several issues have passed QA, stack them in merge order and run one recheck on the top commit instead of one per rebase. Then push the whole stack.
8. Push to `main` once QA passes; no owner OK needed. CI (`.github/workflows/ci.yml`) then runs on Linux as a clean-machine safety net. Windows and macOS are parked and get tested again when they're un-parked.
9. Once the commit is on `origin/main`, QA ticks the acceptance criteria and review lines, posts one line ("Passed: <sha>."), removes the status label and closes the issue. Only QA closes issues.

## Bugs
- A failing CI run on `main`: new `bug` issue right away, naming the run and sha.
- Found while the feature issue is open: comment on that issue and flip it to `in-progress`.
- Found in a closed feature or existing code: new issue with the `bug` label.

## Outside pull requests
The owner reviews and merges them. The same QA rule applies before merge.

## Writing
Issue comments are 1-3 factual sentences, e.g. "Fixed token validation bug in AuthService. Ready for QA." No filler, no restating the issue, no lists of what you inspected.
