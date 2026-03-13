---
name: github
description: |
  Interact with GitHub Issues and pull requests for Symphony workflows that use
  `tracker.kind: github`.
---

# GitHub Issues / PRs

Use this skill when the active workflow is configured with `tracker.kind: github`.

## Auth and Tooling

- Prefer GitHub MCP tools when they are available in-session.
- Otherwise use repo-local tooling or direct GitHub API calls backed by `GITHUB_TOKEN`.
- `tracker.repo` should be set to `owner/repo`.

## Default Posture

- Treat one persistent `## Codex Workpad` issue comment as the source of truth for progress.
- Reuse and update the existing workpad comment instead of creating duplicate progress comments.
- Keep issue state and labels aligned with the workflow's configured active and terminal states.
- Pull requests are separate GitHub objects; inspect them directly for review comments, checks, and
  merge status rather than inferring from the issue body alone.

## Common Workflows

- Read issue details, labels, assignees, comments, and linked PR context.
- Find or create the single `## Codex Workpad` issue comment, then update it in place.
- Add or edit issue comments for progress, blockers, and handoff.
- Inspect pull requests linked to the issue, including review comments, review state, checks, and
  mergeability.
- Attach or reference the PR on the issue once implementation is ready for review.

## Preferred Tools

- `mcp__github__issue_read` for issue details and issue comments.
- `mcp__github__add_issue_comment` for non-review issue comments.
- `mcp__github__pull_request_read` for PR details, files, comments, reviews, and check runs.
- `mcp__github__update_pull_request` / `mcp__github__create_pull_request` for PR lifecycle work.
- `mcp__github__search_pull_requests` when you need to find PRs by branch, issue reference, or
  author.

## Fallback CLI Examples

```bash
gh issue view 123 --repo owner/repo --json number,title,body,labels,assignees,comments
gh issue comment 123 --repo owner/repo --body "## Codex Workpad\n\n..."
gh pr view 456 --repo owner/repo --json number,state,reviewDecision,comments,reviews,checkSuites
```
