# Developer

Purpose: deliver one issue as one tested commit.

Use when: an issue is assigned or picked up for implementation.
Skip when: the issue lacks testable acceptance criteria. Ask the product manager instead.

Read: [workflow](workflow.md), [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md), and the doc for the area you touch:
- Backend: [ASYNC-AND-JOBS](../../docs/ASYNC-AND-JOBS.md), [STORAGE](../../docs/STORAGE.md)
- Formats: [docs/formats/](../../docs/formats/)
- Desktop: [UI-GUIDELINES](../../docs/UI-GUIDELINES.md)

## Do
- Follow [workflow](workflow.md) steps 2-8: branch `issue-N`, one squashed commit ending `Refs #N`, call the reviewer and listed experts, merge `--ff-only` after QA passes.
- Write tests first where practical. Every new behaviour gets a meaningful test; every bug fix gets a regression test.
- Run the relevant checks before asking for review: `npm run contracts:check`, `npm run test:backend`, `npm run test:desktop`, `npm run test:contracts`, and `npm run test:e2e` for UI flows. Report the actual result.

## Area checklists
- **Backend (C#):**
  - async end to end
  - cancellation that really stops the producer
  - bounded workers, queues and allocations
  - staging rows never visible as ready
  - a DB commit and a file move are never one atomic step
  - migrations go through the catalog in order
- **Desktop (Electron/React):**
  - validate every bridge payload
  - the renderer gets no Node, SQL, HTTP or path access
  - custom protocols stay narrow
  - drop stale results after context changes
  - match the selected mockup
  - keyboard, focus and 200% text work
- **Formats (parsers):**
  - original code only (references read, never copied); record reference revisions in docs
  - pure functions over bounded input
  - unsigned/overflow-safe arithmetic
  - allocation capped before trusting header claims
  - exact end-of-file
  - explicit refusal of unsupported versions
  - tests for malformed input

## Don't
- Write handoffs, plans or notes as files.
- Paste hash lists or test-count dumps into comments. Give the command and pass/fail.
- Push to `main` without the owner's OK. Never force-push.

## Done when
The commit is on `origin/main` and the issue is `ready-for-qa` or closed by QA.
