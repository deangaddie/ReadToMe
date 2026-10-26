# Issue tracker: GitHub

Issues and specs (you may know a spec as a PRD) for this repo live as GitHub issues on
`deangaddie/ReadToMe`. Use the `gh` CLI for all operations; it infers the repo from the clone.

**The repo is public, so every issue is public.** Don't paste secrets or non-Gutenberg book text
into an issue.
## Conventions

- **Create an issue**: `gh issue create --title "..." --body-file <file>`. Write the body to a file
  in the scratchpad first; it avoids shell quoting differences between Bash and PowerShell.
- **Body text**: don't hard-wrap paragraphs. GitHub renders a single newline in an issue or comment
  as a line break, so wrapped prose looks ragged.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments,assignees`.
- **List issues**: `gh issue list --state open --json number,title,labels,assignees` with
  `--label` and `--state` filters.
- **Sub-issues** (enabled on this repo): `gh issue create --parent <parent> ...`, or
  `gh issue edit <parent> --add-sub-issue <child>` afterwards. List a parent's children with
  `gh api repos/deangaddie/ReadToMe/issues/<parent>/sub_issues`.
- **Comment on an issue**: `gh issue comment <number> --body-file <file>`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`
- **Specs**: a spec is a parent issue; its implementation tickets are sub-issues of it, one issue per
  ticket. Reference other issues by `#<number>`, never a `.scratch` path.

## History

Before 2026-10-26 the tracker was local markdown under `.scratch/` (git-ignored, never committed).
`.scratch/completed/` and `.scratch/not-implemented/` stay on the owner's machine as read-only
history; older ADRs and docs still link paths in them. Don't add new work there. A dropped effort
from `not-implemented/` that comes back is recreated as issues.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature
requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr`
equivalents:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh api --paginate 'repos/{owner}/{repo}/pulls?state=open' --jq '.[] | select(.author_association | IN("OWNER","MEMBER","COLLABORATOR") | not) | {number, title, author: .user.login, author_association, labels: [.labels[].name]}'`.
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with
`gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Destination / Notes /
  Decisions-so-far / Not yet specified / Out of scope body. `gh issue create --label wayfinder:map`.
- **Child ticket**: a sub-issue of the map (`gh issue create --parent <map>`), labelled
  `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is
  assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**. Add an edge with
  `gh api --method POST repos/deangaddie/ReadToMe/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`,
  where `<blocker-db-id>` is the blocker's numeric **database id**
  (`gh api repos/deangaddie/ReadToMe/issues/<n> --jq .id`, _not_ the `#number` or `node_id`).
  `issue_dependencies_summary.blocked_by` counts open blockers only, the live gate. A ticket is
  unblocked when every blocker is closed.
- **Frontier query**: list the map's sub-issues, keep the open ones with no assignee and
  `issue_dependencies_summary.blocked_by == 0`; first in sub-issue order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body-file <answer>`, then `gh issue close <n>`, then append a
  context pointer (gist + link) to the map's Decisions-so-far (`gh issue edit <map> --body-file`).
