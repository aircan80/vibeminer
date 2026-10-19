@AGENTS.md

## Claude Code specifics
- The qa-testing, reviewer, wow-datamining-expert, ui-expert and usability-expert roles run as subagents defined in `.claude/agents/`. Each one reads its `.agents/rules/` file. Call them with the Agent tool; never act as them in the session that wrote the code.
- Area rules in `.claude/rules/` load automatically when you work in matching paths.
- QA worktrees go in `.claude/worktrees/` (gitignored).
