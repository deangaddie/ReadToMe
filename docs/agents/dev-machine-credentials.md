# Dev-machine credential policy

A hijacked dependency release runs as the developer during `dotnet build`, `bun run check`,
`npm run check`, `dotnet run` and `bun run dev`, and steals whatever credentials it can reach. These
rules keep that to as little as possible. They are generic on purpose: the repo is public, so no
machine-specific detail (which tokens exist, where, with which scopes) goes here or on the tracker.
The policy was agreed in #403.

## Rules

- **The build and the gate need no credentials.** NuGet, Bun, npm, Docker images and the Hugging
  Face model downloads are all public.
- **GitHub: one fine-grained PAT.** `gh` and git both use it (`gh auth setup-git`); it replaces the
  default `gh` OAuth token and the Git Credential Manager token.
  - Selected repositories only, named explicitly. A new repo means editing the token.
  - Repository permissions: Contents read/write, Pull requests read/write, Issues read/write,
    Metadata read. Nothing else: no Administration, Workflows, Secrets or Actions permission, and
    no account permissions.
  - 90-day expiry. On expiry, regenerate the token; don't extend it.
- **No secret in a plain-text file.** Tokens live in the OS credential store only, never in `.env`,
  `~/.netrc`, `~/.npmrc`, `nuget.config`, `~/.docker/config.json` (use Docker's credential store)
  or persistent environment variables. The AI tools' own login files are an accepted exception,
  since they can't be moved.
- **No credential the dev work doesn't need.** No Docker Hub login, no Hugging Face token, no npm
  or NuGet publish keys. A token kept for a tool is the narrowest kind it offers, and it expires.
- **SSH.** GitHub goes over HTTPS through the PAT. Any SSH key kept for other hosts has a
  passphrase.
- **`main` takes changes through PRs only.** The owner's ruleset bypass is `pull_request` mode, so
  a direct push to `main` is rejected for everyone, and nothing lands without a PR on record
  (#413). Merge with `gh pr merge --squash --admin`: the ruleset requires signed commits, which
  GitHub adds to a squash commit but can't add to rebased ones, and its review and update rules
  need the owner's bypass, which `--admin` uses inside the PR.
- **The gate strips credential-looking environment variables.** `scripts/check.ps1` removes them
  before its stages (see `Remove-CredentialVariables`) and puts them back at the end. It's a
  backstop for the plain-text rule, not a substitute: credential files and the OS credential store
  stay readable to anything the gate runs.

## Auditing the machine

The human keeps a local, git-ignored checklist for auditing what's actually on the machine against
these rules. Audit results never go on the tracker (no issue, PR or comment) or anywhere else in
the repo.
