# Bump diff review tooling

Research for issue #400 (map #388, supply-chain risk from a hijacked dependency release). Question: what can mechanically surface the risky part of a dependency bump before merge, for each ecosystem? "Risky" means new or changed code that runs without being imported on purpose: NuGet `build/`, `buildTransitive/`, `buildMultiTargeting/` MSBuild files, analyzers and source generators, `tools/init.ps1`, content files; npm and Bun install-time scripts, new `bin` entries, new network or `child_process` use; Python `.pth` files in wheels.

Constraints: GitHub Actions are disabled, so every check must run locally inside a merge procedure an agent can script. Dependabot opens patch-only PRs. Whoever merges runs the gate on the host with developer credentials, so the check has to happen before the new code is built, tested or installed with scripts enabled.

Versions on the research machine: Bun 1.4.2, npm 12.2.3 (newer than the 11.x the docs pages were written for), .NET SDK 10.0.404. Where the docs and the local CLI disagree, the text says so. Claims marked "observed" were run read-only against public registries on this machine.

## Recommendation

Cheapest mechanical check first, per ecosystem. None of these needs an account or API key.

### Bun (`src/Read2Me.Web`, `tools/browse`)

1. List the packages whose resolved version changed: `git diff` on `bun.lock` (the PR already contains it). This covers transitive changes too.
2. For each changed `name@old` to `name@new`, run `bun pm diff name@old..new` (add `--name-only`, `--stat` or `--json` as needed). It downloads both tarballs, checks them against the integrity hash, and prints a summary that flags new `install`/`postinstall` scripts, dependency and `bin` changes, new `child_process`/`fs`/`net` imports, and growth in `eval`, `fetch` and `process.env` use. It is built into Bun, free, and needs only registry access. Use explicit old and new versions: with only a package name it compares the version in `bun.lock` to `latest`, not old lock to new lock.
3. Keep Bun's default of not running dependency lifecycle scripts, and run `bun pm untrusted` after the install. A new entry there is a new install script in the tree. Do not add `trustedDependencies` just to make a bump pass.
4. Optional: add `install.minimumReleaseAge` or `--minimum-release-age` so a release younger than a set number of seconds is not picked up. It is a cooldown, not a detector.
5. Optional provenance signal: for packages that have provenance today, check that the new version still has it with `npm view name@new dist.attestations`. `npm audit signatures` does not work on a Bun install (see below).

### npm (`Infra/ContainerHealth`)

1. `npm diff --diff=name@old --diff=name@new` per changed package (list them from a `package-lock.json` diff). Add `--diff-name-only` first for a file list. It diffs raw tarballs and has no summary of scripts, so also check `hasInstallScript` in the lockfile diff: lockfile v2/v3 sets it on any package with `preinstall`, `install` or `postinstall`.
2. `npm ci --ignore-scripts` for the verification install, then `npm audit signatures` (needs an npm-installed `node_modules`). Run `npm rebuild` or normal scripts only if a reviewed script is genuinely needed.
3. Optional: `min-release-age` in `.npmrc` (exists in npm 12.2.3 here, default unset).
4. Do not rely on `npm query ':attr(scripts, [postinstall])'` against a lockfile-only tree. Observed: it returned `[]` for a lockfile that does contain a `hasInstallScript` package, because the lockfile does not carry the `scripts` object.

### NuGet (`src/Directory.Packages.props`)

No first-party diff tool exists, so script it. A `.nupkg` is a zip.

1. Download old and new from the flat container (`https://api.nuget.org/v3-flatcontainer/{id-lower}/{version}/{id-lower}.{version}.nupkg`), unzip each into its own empty folder, and compare the file lists. Observed with CliWrap 3.10.0 to 3.10.5: a one-line `diff` of the sorted file lists shows added or removed files at a glance.
2. Flag by path. Any added or changed file under `build/`, `buildTransitive/`, `buildMultiTargeting/`, `analyzers/`, `tools/`, `contentFiles/` or `content/`, or any new `.props`, `.targets`, `.ps1`, `.dll` outside `lib/`/`ref/`, is a stop-and-read item. Also diff the `.nuspec` dependency list for new dependencies.
3. Do the unzip before the first build. `dotnet restore` downloads and extracts the package into the global packages folder and writes `obj/project.assets.json`; package contents are not copied into the project (NuGet install-process doc). The package's MSBuild files and analyzers take effect when the project is built or opened in an IDE. So the safe order is: restore, inspect the extracted folder, then build. (That ordering is an inference from the docs plus how MSBuild imports `nuget.g.props`; it was not tested with a hostile package.)
4. For transitive changes, run `dotnet package list --include-transitive --format json` (needs a restore first; observed: `--no-restore` fails with "No assets file was found") before and after, and diff the two. Or turn on `RestorePackagesWithLockFile` and diff `packages.lock.json` in the PR (restore options `--use-lock-file`, `--locked-mode` exist in the local SDK).
5. `tools/init.ps1` is the one NuGet script left. NuGet's own warnings say `install.ps1` is not executed for PackageReference projects and that `init.ps1` must sit directly under `tools/`. I found no primary source that says whether the dotnet CLI ever runs `init.ps1`; the CLI docs say nothing about package scripts. Treat any `.ps1` in a package as a stop-and-read item regardless.
6. Optional: `minPublishAgeHours` on the nuget.org source (a cooldown) exists only from NuGet 8.0 and .NET SDK 11.0.100. This machine has SDK 10.0.404, so it is not available yet.
7. Optional: `signatureValidationMode=require` plus `trustedSigners` (see the provenance section). It proves nuget.org served the package unchanged, not that the publisher was honest.

### Python (`lock.ps1 -UpgradePackage`, rare)

Before installing a bumped wheel, download without installing and list the zip: any `*.pth` file, any file outside the package and `.dist-info` folders, and the `RECORD` file. A `.pth` line starting with `import` is executed at interpreter start-up from `site-packages`. The `uv pip compile` hash pins stop a swapped file, not a malicious new release.

### Shared, optional

OSV-Scanner reads `bun.lock`, `package-lock.json`, `packages.lock.json` and `uv.lock`, and can flag packages already reported malicious (OpenSSF feed). It will miss a release nobody has reported yet, so it is a backstop, not a diff tool. Socket has the richest behaviour analysis and lists NuGet and Bun (beta), but needs an account and token. See the scanner section.

## npm and Bun: `npm diff`, `bun pm diff`, lifecycle scripts

| Option | Runs locally w/o Actions | Covers | Cost | Limits |
| --- | --- | --- | --- | --- |
| `bun pm diff <pkg>@<old>..<new>` | Yes. Observed working in Bun 1.4.2 (exit code 0). | Any package on the registry, direct or transitive, one package per call. Flags: `--name-only`, `--stat`, `--json`, `-w`, `--raw`, `--minify`, `--unminify`, `-U`, `--diff <spec>`. Summary flags new install scripts, dependency/`bin`/entry-point changes, new `child_process`/`fs`/`net` imports, growth in `eval`/`new Function`/`process.env`/`fetch`, newly executable files. | Free, built in, two tarball downloads. | Not listed in `bun pm --help` for 1.4.2, though it runs and is in the online docs. Exit codes are not documented. Output re-prints JS by default (use `--raw` for bytes as published). A pure `bun pm diff name` compares the locked version to `latest`. Observed: `lit-html` 3.3.0 to 3.3.1 printed a file/line summary and no script warnings; `esbuild` 0.21.0 to 0.21.1 printed `!` notes for optionalDependencies changes. |
| `npm diff --diff=a --diff=b` | Yes. | One package per call, any npm spec (registry, git, folder). `--diff-name-only`, `--diff-unified`, `--diff-ignore-all-space`, `--diff-text`, file-path filters. | Free, bundled with npm. | Plain text diff: no script or import summary, minified bundles are unreadable. The docs describe no lockfile input; you supply the old and new versions. Prints a diff of tarball contents, so a new `postinstall` shows up only as a `package.json` hunk. Works on any registry package, so it can also review Bun-managed packages. |
| Lockfile `hasInstallScript` (npm) / `bun pm untrusted` (Bun) | Yes. | npm: flag in `package-lock.json` for every package with `preinstall`, `install` or `postinstall`, direct and transitive. Bun: lists dependencies whose scripts were blocked. | Free. | The npm field does not list `prepare` (it is not an install-time script for dependencies). Bun's `bun pm untrusted` reported 0 for this repo today, which is the baseline: any new line is a change. |
| `npm query ':attr(scripts, [postinstall])'` | Yes, but see limits. | The `:attr()` selector reads package manifest fields. | Free. | The docs show only `[test~=tap]`-style examples; the `postinstall` form is an extrapolation. Observed `[]` against a lockfile-only tree. Likely only reliable with a populated `node_modules`. |
| Bun default script policy | Yes. | Bun does not run lifecycle scripts of dependencies unless the package is in `trustedDependencies` or the built-in default-trusted list (npm-registry packages only). `bun pm trust` runs scripts and adds the package to `trustedDependencies`. | Free. | Documentation and CLI help disagree: the lifecycle docs describe a default-trusted list, while `bun install --help` in 1.4.2 says dependency scripts are never run. Not tested which is right. Setting `trustedDependencies` replaces the default list. `--ignore-scripts` (or `install.ignoreScripts` in `bunfig.toml`) turns everything off. |

Notes:

- Scripts are only part of the risk. A hijacked package can also ship malicious code that runs when the app or test suite imports it. That is why the diff of the whole tarball matters, not just `scripts`.
- npm `--ignore-scripts` is a documented flag of `npm install`, `npm ci` and `npm audit` (the audit help lists it); it disables dependency scripts.

## NuGet: diffing the `.nupkg`

| Option | Runs locally w/o Actions | Covers | Cost | Limits |
| --- | --- | --- | --- | --- |
| Download from the flat container, unzip, diff file lists and contents (script) | Yes. Observed with CliWrap. | Any package and version, direct or transitive, including `build/`, `buildTransitive/`, `analyzers/`, `tools/`, `contentFiles/`. | Free. Two small HTTPS downloads per package. No key. | You write the script. Managed DLLs differ on every build, so a content diff of binaries is noise: review the file list and text files (`.props`, `.targets`, `.ps1`, `.nuspec`), and decompile only when a DLL appears in an unexpected folder. |
| `dotnet restore` then read the global packages folder | Yes. | Same files, already extracted and verified by NuGet. | Free. | Inference: restore does not run package code; build does (MSBuild imports, analyzers). Do the inspection between the two. |
| `dotnet package list --include-transitive` before and after (JSON) | Yes, after a restore. | Direct and transitive versions. | Free. | Versions only, not contents. `--no-restore` fails without an assets file (observed). |
| `packages.lock.json` (`--use-lock-file`, `--locked-mode`) | Yes. | Resolved direct and transitive versions plus content hashes, as a reviewable PR diff. | Free, but a one-time repo change (the repo has none today). | Versions and hashes only. |
| `dotnet nuget why` | Yes. | Explains why a transitive package is in the graph. | Free. | Not a diff. |
| Package Explorer, fuget.org, nuget.info | Not confirmed. | Interactive browsing of package contents. | Not confirmed. | I did not find a primary source describing a scriptable diff from any of these, so I did not rely on them. Marked unconfirmed. |

What runs, per primary sources:

- `.props` and `.targets` in `build/` etc. are MSBuild imports, so they run at build time. Analyzers and source generators run in the compiler. Neither is covered by a NuGet doc I could cite as a statement of "this executes arbitrary code"; this is the standard NuGet package layout (see nuspec and package-folder docs) and the issue text already takes it as given.
- `install.ps1` is not executed for PackageReference projects (NU5120). `init.ps1` is the only script NuGet still supports for PackageReference (NuGet/Home issue 4942), and it must sit directly under `tools/` (NU5107, NU5110). Which client runs it is not stated in the sources I could read.
- Content files: `contentFiles/` is the PackageReference mechanism (it can set a build action and copy-to-output); the older `content/` folder is ignored under PackageReference.
- Dependency `exclude` defaults to `build,analyzers` for packages that depend on other packages, meaning `build/` and `analyzers/` assets of a dependency are not passed through unless the package says so (nuspec reference). `buildTransitive/` is the mechanism that does flow through; this needs a manual look in each diff.

## Provenance, signatures and attestations

| Option | Runs locally w/o Actions | Covers | Cost | Limits |
| --- | --- | --- | --- | --- |
| `npm audit signatures` | Yes, but needs an npm-built `node_modules`. | Verifies registry signatures and provenance attestations of installed packages. | Free, network. | Observed on this Bun-installed tree: `found no dependencies to audit that were installed from a supported registry`, and `npm ls` reports every dependency as UNMET. Works for `Infra/ContainerHealth` only after `npm ci`. Docs do not say whether a lockfile alone is enough; observed that it is not. Verifies that a signature is genuine, not that the release is benign. |
| `npm view <name>@<ver> dist.attestations dist.signatures` | Yes, read-only registry call. | Per-version: does a provenance attestation exist (predicate `https://slsa.dev/provenance/v1`) and the registry signature. Observed: `lit-html@3.3.1`, `@preact/signals-core@1.14.5` and `oxlint@1.88.0` have provenance; `@microsoft/signalr@10.0.11` has none. | Free. One call per package version. | Works for Bun-managed projects because it queries the registry directly. Only useful as "provenance disappeared" for packages that had it before. |
| Bun | No built-in signature or provenance check found. | `bun pm diff` checks tarballs against the integrity hash, which detects damage, not tampering by the publisher. | Free. | The Bun docs I read do not mention provenance verification. |
| `dotnet nuget verify` | Yes. | Verifies signatures in a `.nupkg`. Observed on CliWrap 3.10.5: signature type Repository, signer `NuGet.org Repository by Microsoft`, no author signature. | Free. | Repository signatures are applied to every package nuget.org accepts. They prove the package came from nuget.org unchanged, not that the publisher account was honest. A hijacked account still gets a valid repository signature. An author signature would add a certificate registered by the owner, but most packages do not have one (CliWrap does not). |
| `signatureValidationMode=require` and `trustedSigners` in `nuget.config` | Yes. | Restore fails for packages not signed by a listed signer. Trusted `repository` can be limited to `owners`. | Free. | `trustedSigners` must not be empty with `require`. With only nuget.org trusted, this is no stronger than the repository signature above. Pinning an author certificate per package would block a hijack that lacks the author's key, but needs author-signed packages, which are rare. |
| PyPI attestations (PEP 740) | Via the Integrity API and the `pypi-attestations` tool. | Provenance for uploads made with Trusted Publishing. | Free. | The tool's own README calls its CLI experimental. `inspect` only prints structure, `verify` supports GitHub and GitLab publishers. Not researched further for this repo. |

Provenance points to keep in mind:

- npm provenance links a package to a source repo and build. It does not prove the code is benign (npm docs). The npm docs I read do not say that a token-based publish is blocked from omitting provenance, so "provenance vanished in the new version" is a reasonable warning signal but not something a primary source guarantees.
- Secondary reporting (vendor and press write-ups, not primary) says that in a recent npm worm incident the malicious versions did carry valid provenance, because malware running inside an authorised publish workflow requested its own short-lived token. So "has provenance" is necessary, not sufficient. This could not be confirmed from a primary source.

## Scanners and first-party options

| Option | Runs locally w/o Actions | Covers | Cost | Limits |
| --- | --- | --- | --- | --- |
| OSV-Scanner | Yes (CLI). | Lockfiles: `bun.lock`, `package-lock.json`, `packages.lock.json`, `deps.json`, `uv.lock`, `requirements.txt`. Matches against OSV, which also ingests OpenSSF malicious-package reports (`MAL-` ids on osv.dev). | Free, open source. Network for the database unless offline mode is set up. | Only knows what has been reported. A release published an hour ago will not be in it. The malicious-packages README gives no latency guarantee. |
| Socket (`socket` CLI, GitHub app) | CLI yes, but needs a token for the API-backed commands. | Behaviour-based alerts (install scripts, network, shell use). Docs list npm, PyPI and NuGet; Bun lockfiles in beta (text `bun.lock` only). Free tier and a free Team upgrade for qualifying open source projects. | Account, API token, Node 22+. Free tier limited to 1,000 scans a month. | Not run here, per the brief. I could not confirm from Socket's own docs which NuGet manifest formats are read, nor whether `socket scan create` works without a token. Uploads dependency manifests and lockfiles. |
| Bun security scanner API (`[install.security] scanner` in `bunfig.toml`) | Yes. | Runs during `bun install`, `add`, `update`, and also `bun pm scan`. A `fatal` advisory aborts the install. Scans transitive packages. | Free API, but the scanner is a third-party npm package you pick. Observed: `bun pm scan` says "no security scanner configured". | Only as good as the scanner chosen. Docs do not name a free one. |
| `bun audit`, `npm audit` | Yes. | Known-vulnerability advisories only (npm advisory endpoint; `bun audit` reads `bun.lock`, direct and transitive). | Free. | Not malware detection. A new hijack is not an advisory yet. |
| `dotnet list package --vulnerable`, NuGet Audit on restore | Yes. | Known vulnerabilities from the GitHub Advisory Database via nuget.org. `NuGetAuditMode` defaults to `all` (transitive too) for `net10.0` and higher, otherwise `direct`. Use `--include-transitive` for the CLI command. | Free. | Known vulnerabilities only. Not a hijack detector. |
| GitHub dependency review compare API (`GET /repos/{owner}/{repo}/dependency-graph/compare/{base}...{head}`) | Yes. It is a REST call, not an Action. Public repositories can be queried without authentication. Observed with `gh api`. | Changed dependencies between two commits, with vulnerability data. Observed on this repo: it listed the direct `package.json` change for an npm bump. | Free. | Observed `[]` for a commit that changed `Directory.Packages.props` (NuGet Central Package Management not reported), and no `bun.lock` rows, so no transitive Bun changes. It shows versions, not contents, so it does not surface install scripts or new code. |
| Dependabot PR body | Not researched. | Release notes and commit list written by the maintainer. | Free. | Maintainer-written text is exactly what a hijacker controls. I did not verify what Dependabot puts in a patch-only PR body. |

## Python: `.pth` files in a wheel

| Option | Runs locally w/o Actions | Covers | Cost | Limits |
| --- | --- | --- | --- | --- |
| Download the wheel without installing, list its zip entries | Yes (any zip tool or `pip download --no-deps`; `uv` and `pip` are not installed on this machine, so not run here). | Every file in the wheel, including `*.pth`. The wheel's `.dist-info/RECORD` lists every installed file with a hash. | Free. | Needs a manual compare of old and new lists. `RECORD` is written by the publisher, so it is not independent evidence. |
| Nothing built into `pip` or `uv` | n/a | I did not find a flag that refuses or reports `.pth` files. | n/a | Not confirmed from a primary source. Python's `site` docs say lines in a `.pth` file starting with `import` are executed at start-up (deprecated in 3.15 but still executed). Source distributions run a build backend on install and are a larger risk than wheels; `--only-binary` options exist in pip, and I did not check the uv equivalents. |

## What I could not confirm

- Whether the dotnet CLI ever runs a package's `tools/init.ps1` (only that PackageReference supports `init.ps1` and not `install.ps1`).
- Whether any NuGet browsing site (nuget.info, fuget.org, Package Explorer) can be driven from a script.
- Whether Bun runs scripts of default-trusted packages today (docs) or never (CLI help text).
- That a token-published npm release lacks provenance by default; and the claim that a recent npm incident carried valid provenance (secondary sources only).
- Socket details: NuGet manifest coverage, whether the CLI works without a token, and the status of Bun support past the announced beta. Socket was not run.
- What Dependabot writes in its PR body.
- Python tooling (`uv`, `pip`) behaviour, since neither is installed here.

## Sources

- npm diff: https://docs.npmjs.com/cli/v11/commands/npm-diff
- npm audit (signatures): https://docs.npmjs.com/cli/v11/commands/npm-audit
- npm provenance: https://docs.npmjs.com/generating-provenance-statements
- npm dependency selectors (`:attr`): https://docs.npmjs.com/cli/v11/using-npm/dependency-selectors
- package-lock.json `hasInstallScript`: https://docs.npmjs.com/cli/v11/configuring-npm/package-lock-json
- Bun lifecycle scripts and `trustedDependencies`: https://bun.com/docs/install/lifecycle
- Bun `bun pm` commands including `diff`, `untrusted`, `trust`: https://bun.com/docs/cli/pm
- Bun audit: https://bun.com/docs/install/audit
- Bun security scanner API: https://bun.com/docs/install/security-scanner-api
- CLI help and observed output, `bun` 1.4.2: `bun pm`, `bun pm diff`, `bun pm untrusted`, `bun pm scan`, `bun install --help`, `bun audit --help`
- CLI help and observed output, `npm` 12.2.3: `npm diff --help`, `npm audit signatures`, `npm query`, `npm view`, `npm config list -l`
- CLI help and observed output, .NET SDK 10.0.404: `dotnet nuget verify`, `dotnet restore --help`, `dotnet package list`
- NuGet flat container (download URL scheme, used as `https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg`): https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource
- NuGet install process (restore extracts to global packages folder, contents not copied into the project): https://learn.microsoft.com/en-us/nuget/concepts/package-installation-process
- NuGet nuspec (content files, dependency `exclude` default `build,analyzers`): https://learn.microsoft.com/en-us/nuget/reference/nuspec
- NuGet signed packages (author vs repository signature; all nuget.org packages are repository signed): https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference
- NuGet config (`signatureValidationMode`, `trustedSigners`, `minPublishAgeHours`, `auditSources`): https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file
- NuGet minimum package publish age (NuGet 8.0, .NET SDK 11.0.100): https://learn.microsoft.com/en-us/nuget/consume-packages/minimum-package-publish-age
- NuGet Audit: https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- NU5120 (`install.ps1` ignored for PackageReference): https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu5120
- NuGet/Home issue 4942 (`init.ps1` only): https://github.com/NuGet/Home/issues/4942
- NU5107 and NU5110 (`init.ps1` must be directly under `tools/`): https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu5107 and https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu5110
- OSV-Scanner supported lockfiles: https://google.github.io/osv-scanner/supported-languages-and-lockfiles/
- OpenSSF malicious-packages: https://github.com/ossf/malicious-packages
- OpenSSF note on detecting malicious packages via OSV: https://openssf.org/?p=11003
- Socket CLI docs: https://docs.socket.dev/docs/socket-cli
- Socket ecosystem support: https://docs.socket.dev/docs/language-support
- Socket Bun support announcement: https://socket.dev/blog/announcing-bun-and-vlt-support
- Socket pricing: https://www.socket.dev/pricing
- Socket on npm trusted publishing (secondary, provenance caveat): https://socket.dev/glossary/npm-trusted-publishing
- GitHub dependency review REST API: https://docs.github.com/en/rest/dependency-graph/dependency-review
- PEP 740: https://peps.python.org/pep-0740
- PyPI attestations: https://docs.pypi.org/attestations/
- pypi-attestations tool: https://github.com/pypi/pypi-attestations
- Python `site` module (`.pth` handling): https://docs.python.org/3/library/site.html
- Python wheel `RECORD` file: https://packaging.python.org/en/latest/specifications/recording-installed-packages/
