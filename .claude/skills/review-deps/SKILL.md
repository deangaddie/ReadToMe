---
name: review-deps
description: The pre-merge procedure for a dependency PR, run before anything builds it. Use when reviewing, gating or merging a PR that changes a dependency manifest, lockfile, registry config or pinned digest (Dependabot, a hand bump, or a feature PR that adds a package).
---

# Reviewing a dependency PR

A bump runs its new code on the host the first time anything restores, installs, builds or tests it, with the
developer's identity and credentials. `scripts/review-deps.ps1` reads the change without running it. This skill makes
every session run that review first, publish the report, and handle the result the same way.

## Trigger

Whoever opened the PR, including a hand bump on a `.scratch/dependency-upgrades` branch. Registry configs count:
`bunfig.toml`, `.npmrc`, `nuget.config`. The full file list is `scripts/dependency-files.ps1`, the same list
`check.ps1` warns on.

## Steps

1. **Fetch the head as a ref, without checking it out.** Keep the working tree on `main` and leave the head closed in
   any IDE that auto-restores (Rider, Visual Studio, VS Code with C# Dev Kit).

   ```bash
   git fetch origin main "+pull/<n>/head:refs/pr/<n>"
   ```

   Done when `git rev-parse refs/pr/<n>` prints the PR's head SHA (`gh pr view <n> --json headRefOid`).

2. **Run the review before restoring, installing, building or testing the PR head on the host.**

   ```bash
   pwsh scripts/review-deps.ps1 origin/main refs/pr/<n> > <tmp>/review-<n>.md
   ```

   Exit 0: clean or NOTEs only. Exit 1: at least one STOP. Exit 2: the review could not run and printed no report; fix
   the cause from its stderr log and run it again. `<tmp>` is any folder outside the repo (your scratchpad). Done
   when it exits 0 or 1 with a report.

3. **Read every finding.** Each one is a STOP or a NOTE. Done when you can say, for each NOTE, what it flagged and why
   it does or does not matter for this bump.

4. **Post the report as a PR comment, clean runs included.** Put the report first, then a short section of your own
   that names each NOTE and your reading of it, and says what happens next (gate, or handed to the human).

   ```bash
   gh pr comment <n> --body-file <tmp>/comment-<n>.md
   ```

   Done when the comment shows in `gh pr view <n> --comments`.

5. **On a STOP, stop.** Leave the PR open, post the report (step 4), and hand the decision to the human in your reply,
   naming each STOP. An agent never clears a STOP, however harmless it looks: only the human does, with a written reason
   in a PR comment by the repo owner (`deangaddie`). Done when you have handed over; carry on at step 6 only once that
   comment exists, and quote it in your reply.

6. **Run the gate on the head**, now that the review is clean or every STOP has the human's written clearance. The gate
   for each Dependabot entry is listed at the end of the `.github/dependabot.yml` header; a hand bump or feature PR
   runs `pwsh scripts/check.ps1`. Check the head out only now (`gh pr checkout <n>`). Done when the gate passes; a
   failure goes back on the PR as a comment, the PR stays open, and you switch back to `main`.

7. **Merge** with `gh pr merge <n> --squash --admin`, and switch back to `main`. Done when `gh pr view <n> --json
   state` says `MERGED`.

## Young security fixes

The 7-day age STOP holds a fresh release back until a hijack would have been spotted. When a security fix cannot wait,
only the human overrides it: the human runs the review with `-AllowYoung <package>`, or tells you to (in this
session, or in a PR comment by the repo owner), naming one package and its advisory. One package per use; the advisory goes in the PR comment. The report then shows
the age STOP as a NOTE, and every other STOP still applies.

## No scanner

The review adds no OSV-Scanner, Socket or similar: none catches a fresh hijack, and each would be one more tool
running on the host.
