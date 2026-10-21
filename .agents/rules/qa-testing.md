# QA testing

Purpose: independently confirm that a commit meets its issue, and close the issue. Only QA closes issues.

Use when: an issue is `ready-for-qa`, a passed commit has reached `origin/main` (close step), or a milestone needs release checks.
Skip when: the issue has no testable acceptance criteria. Send it back to the product manager.

Never QA code written in the same session. Work only from the issue text, the commit and the test commands.

Read: [workflow](workflow.md), [docs/TESTING.md](../../docs/TESTING.md).

## Setup (worktree, so development keeps going)
1. `git worktree add .claude/worktrees/qa-N <sha>`. Keep only one QA worktree at a time.
2. In the worktree:
   - `mkdir -p .local`
   - symlink the main checkout's `.local/toolchains` and `.local/electron-cache` into it
   - `npm ci`
   - `source tools/dev-env.sh`
3. Every app or Electron run uses `VIBEMINER_DATA_DIR=<worktree>/.local/qa-data`. Never touch the owner's app profile.
4. When done: `git worktree remove .claude/worktrees/qa-N`.

## Test
- Each acceptance criterion, plus the checks listed in [TESTING.md](../../docs/TESTING.md).
- The CI run for the exact sha: `gh run list --commit <sha> --workflow CI`, then `gh run view <id>`. Every job passes, or each failure matches an open bug issue you name in the verdict. An unexplained failure or a missing run is a fail.
- Check whichever of these apply:
  - cancel mid-download and mid-parse
  - reopen offline
  - kill the app during a commit, then restart
  - corrupt store object
  - encrypted sections show as Encrypted
  - truncated or bad-hash CDN response
  - interactive work wins over bulk work
  - no wrong-build or wrong-locale data
  - unsupported versions are refused
  - fixture data is labelled
- At each milestone: build a package (`npm run package:desktop`), run `npm run smoke:package`, and launch it on every claimed OS.

## Output
- Fail: one issue comment with steps, expected and actual. Flip the issue to `in-progress`.
- Pass: `QA pass at <sha>.`
- Close: after the commit is on `origin/main`, tick the criteria and review lines, comment `Passed: <sha>.`, remove the status label, close.
- After closing, delete the issue's `ci/issue-N-*` refs on origin (workflow step 10). Never push, rewrite or force-push any other ref.

## Don't
- Fix code.
- Write report files.
- Close an issue whose merged commit differs from the one you tested.
