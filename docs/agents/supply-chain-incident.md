# Supply-chain incident runbook

Use this when a hijacked dependency version may have run on the dev machine or in a container. It is
written ahead of time so nothing gets improvised under stress, and so the GitHub PAT is revoked
before anyone starts investigating. It is generic on purpose: the repo is public, so host-specific
findings go in the owner's private notes, never here or on the tracker. The credentials it rotates
are the ones in [dev-machine-credentials.md](dev-machine-credentials.md).

## Triggers

Any one of these starts the runbook:

- a version we installed is later reported malicious, yanked for malware, or covered by an advisory
- a STOP from `scripts/review-deps.ps1` turns out to have been cleared wrongly after the code ran
- `Infra/scripts/egress-check.ps1`, or anything else, shows unexpected outbound traffic
- a commit, PR, branch or tag nobody recognises appears on a repo the PAT covers

First question: **did it run on the host, or only in a container?** Code ran on the host if anything
on the host restored, installed, built, tested or ran it (`dotnet`, `bun`, `npm`, an IDE that
auto-restores). A container-only incident follows [Container-only incidents](#container-only-incidents);
everything else follows the steps from [Contain](#1-contain) on.

## Who runs it

The human. An agent that notices a trigger **stops**: it tells the human which trigger it saw and
runs nothing more from the repo (no build, test, script or dev server).

Once the PAT is rotated, agents may help with the read-only parts: history and log searches, the
cleanup list, the persistence checklist. Rotation and the final call on the diff review stay with the
human.

## Host incidents

### 1. Contain

Contain first. Nothing here waits on the investigation.

- Stop running anything from the affected repos: no build, test or dev server. Kill running
  `dotnet`, `bun` and `node` processes.
- Run `docker compose down` on any affected service.
- Revoke the GitHub PAT right away.

### 2. Rotate

Rotate everything the payload could reach, whether or not there's evidence it was read, in this
order:

1. GitHub PAT (a new fine-grained token per the credential policy).
2. AI-tool logins and API keys.
3. SSH keys for other hosts.
4. Anything else in the OS credential store, or in a plain-text file that broke the policy.

### 3. Find the exposure window

The window runs from the first host checkout and build of the commit or branch carrying the bump,
until cleanup (step 4) is done. Find it with:

- `git log -p` on the dependency files: `src/Directory.Packages.props`, every `bun.lock`
  (`src/Read2Me.Web`, `tools/browse`), `Infra/ContainerHealth/package-lock.json` and the Infra locks
  (`Infra/*/requirements.txt`, and the `@sha256:` pins in the Dockerfiles and
  `Infra/docker-compose.yml`); `scripts/dependency-files.ps1` names every file kind. Reading history
  catches hand bumps as well as Dependabot ones.
- The Dependabot PR and its `review-deps` report comment.
- Every local clone, worktree and branch that contains the commit
  (`git branch -a --contains <sha>`, `git worktree list`).

Also name the other repos on the machine that share the package caches (NuGet, Bun, npm): they may
have restored the same bad version, and the window covers them too.

### 4. Clean up

- Revert the pin through a PR.
- Purge the caches: `dotnet nuget locals all --clear`, `bun pm cache rm` and
  `npm cache clean --force`.
- Delete `node_modules`, `obj` and `bin` in every clone and worktree that built the bad version.
- Rebuild.

### 5. Look for poisoned commits

For the whole window, across every repo the PAT covers, check:

- the account's security log
- pushes, merged PRs, new branches, tags and releases
- changes under `.github/workflows/`

Every PR merged in the window gets a human diff review. Signatures don't settle it, because most
commits are unsigned. Anything found is reverted through a PR.

Webhooks, deploy keys and repo settings are skipped: the PAT has no Administration permission, so a
stolen copy could not change them.

### 6. Sweep for persistence

- [ ] `.git/hooks` and `core.hooksPath` across local repos
- [ ] the global git config
- [ ] shell profiles, scheduled tasks and startup items
- [ ] `.pth` files in any host Python

## Container-only incidents

The similarity services, `audiocpp`, or a Docker build stage: the services sit on internal networks
with no egress, so a payload there has no route to send a credential out, and rotation is skipped.

1. Revert the pin through a PR.
2. Rebuild the service (`docker compose up -d --build <service>` from `Infra/`).
3. Run `Infra/scripts/egress-check.ps1`.
4. Check that every service network in `Infra/docker-compose.yml` is still `internal: true` (all but the proxy's `edge` bridge).

If anything from that build ran on the host (a copied artifact, a script, a mounted folder the host
then executed), it's a host incident: start again at [Contain](#1-contain).

## Reporting

The human reports the package through the registry's abuse channel. An agent only drafts that text
for the human to post; it never posts on a project the owner doesn't own.

Host-specific findings stay in the owner's private notes. The public tracker gets at most a generic
"reverted dependency X after a malicious release" issue.

## No script

A `find-exposure.ps1` that automates the window search and cleanup is risk-accepted as missing:
incidents are rare, and a script nobody exercises rots.
