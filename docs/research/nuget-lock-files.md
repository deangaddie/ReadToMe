# NuGet lock files, source mapping and trusted signers against a hijacked release

Research for [#401](https://github.com/deangaddie/ReadToMe/issues/401), part of the map [#388](https://github.com/deangaddie/ReadToMe/issues/388). The question: if an attacker publishes a hijacked **new version** under a legitimate package name, what would NuGet lock files, package source mapping and trusted signers catch in this repo, and what would they cost?

Researched 2026-11-21. Sources are Microsoft Learn's NuGet docs, the `dependabot/dependabot-core` source at commit [`cbabeb7`](https://github.com/dependabot/dependabot-core/tree/cbabeb7b1671021a093ac3990c9e0ac17dc20fdd/nuget), and local measurements against this repo at `cd1ab0b1` (.NET SDK 10.0.404). No lock file or config was committed.

## Summary

| Mechanism | Catches in a hijacked-release scenario | Cost here |
|---|---|---|
| Lock file plus `--locked-mode` | Not the hijack itself. Once we bump to a bad version, its hash goes into the lock. What it adds is a **review surface**: every transitive version a bump moves shows up in the PR diff, which helps pre-merge review. Its hash check also catches a changed package in the local cache or on a second feed, but nuget.org can't change a published version anyway. | 8 lock files, about 2,900 lines. A typical patch PR touches most of them. Locked mode fails when the SDK changes the graph, and adding a package needs a `--force-evaluate` restore. |
| Package source mapping | Nothing. It stops dependency confusion between feeds. A hijacked release sits on the one feed we already trust. | Very little: one `nuget.config`. Its real value is `<clear/>`, which keeps machine-level feeds out of restore. |
| `trustedSigners` (`require` mode) | A hijacked release pushed with a **stolen API key**, for the 73 of 93 packages that are author-signed (Microsoft, .NET Foundation). It does **not** help for the 20 packages that are only repository-signed (Serilog, CliWrap, NSubstitute, VersOne.Epub, FractionalIndexing, SQLitePCLRaw and others): owner-based trust accepts anything the hijacked owner account pushes. | Every package must be covered by a trusted author certificate or a trusted owner. Restore breaks whenever a publisher rotates its certificate, until someone updates the fingerprint. Signature verification is off by default on macOS. |

Suggested outcome for the map:
- Lock files: optional. Adopt them only if pre-merge review of transitive moves is wanted. They don't stop a hijack.
- Source mapping: worth a small hygiene ticket (`nuget.config` with `<clear/>` and nuget.org only), but it doesn't defend against a hijack.
- `trustedSigners`: risk-accept. Most of the gain already comes from nuget.org's own signing rule, and the gap that's left is the 20 unsigned packages, which this mechanism can't close.

## The repo today

- Central Package Management (`src/Directory.Packages.props`) pins 16 packages at exact versions. Restore records each as a lower bound, such as `"requested": "[10.0.15, )"`, and resolves the lowest version that fits, which is the pinned one.
- There is no `nuget.config` in the repo, so the feeds come from the user and machine `nuget.config` files.
- There are no `packages.lock.json` files, and `global.json` doesn't pin an SDK version (it only sets the test runner).
- `src/Read2Me.slnx` holds 8 projects. Counted with `dotnet list src/Read2Me.slnx package --include-transitive`:

| Project | Top-level | Transitive |
|---|---|---|
| Read2Me.App | 5 | 41 |
| Read2Me.AppData | 4 | 38 |
| Read2Me.Core | 1 | 0 |
| Read2Me.Data | 4 | 38 |
| Read2Me.E2eTests | 5 | 66 |
| Read2Me.Services | 5 | 26 |
| Read2Me.Tests | 4 | 60 |
| Read2Me.TestUtils | 1 | 22 |

That is 88 unique transitive packages, or 93 packages in all when `project.assets.json` is read across every project.

- **No floating ranges anywhere in the graph.** I scanned every dependency range in every `project.assets.json` and found no `*`. Most ranges are lower bounds. A few are exact pins, mostly within families: Roslyn `[5.0.0]`, the xunit.v3 packages `[4.0.3]`, plus xunit's pins on `Microsoft.Win32.Registry` `[5.0.0]` and `System.Security.AccessControl` `[6.0.1]`.

## How NuGet resolves dependencies, and why that matters here

- Package dependencies can't float: "Floating versions can only be specified at the project level." ([dependency resolution](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution))
- For a range, NuGet picks the lowest version that fits: "The lowest applicable version rule restores the lowest possible version of a package as defined by its dependencies." (same page)
- A nuget.org version can't be replaced: "If the package with the provided ID and version already exists, nuget.org will reject the push." It returns `409`. ([package publish resource](https://learn.microsoft.com/en-us/nuget/api/package-publish-resource)) It can't be deleted either: "nuget.org does not support permanent deletion of packages". Unlisted versions "can still be downloaded and installed by using an exact version number". ([deleting packages](https://learn.microsoft.com/en-us/nuget/nuget-org/policies/deleting-packages))

So with no lock file at all, a hijacked **new** version of any package, direct or transitive, doesn't reach our build until we change `Directory.Packages.props`. Same inputs and same SDK give the same graph on every restore. A transitive package moves only when a direct bump raises a lower bound, and then it moves to that new lower bound, not to the newest release. This is NuGet's main difference from npm's caret ranges, and it's why the map rates NuGet's missing lock as gap 3, not higher.

## Lock files (`RestorePackagesWithLockFile`, `--locked-mode`)

What the docs say ([package references: locking dependencies](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies)):
- The lock file is "a full closure of all the package dependencies including transitive dependencies". It is written per project as `packages.lock.json`.
- In locked mode, restore "will either restore the exact packages as listed in the lock file or fail if you updated the defined package dependencies for the project after lock file was created". A mismatch raises [NU1004](https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1004).
- "using a different SDK version will change the package version, leading to locked mode restore failures". The docs suggest pinning the SDK in `global.json`.
- Each entry carries a `contentHash`. [NU1403](https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1403) fires when "The content of the package in the global packages folder is different from the value in the lock file". Its listed causes are that "The package on the source may have changed, or a multiple sources may have different instances of the same package id and version", or that "the package may have been tampered with before it made it to your machine."
- The [security best practices](https://learn.microsoft.com/en-us/nuget/concepts/security-best-practices) page says only that lock files "ensure package repeatability". It doesn't claim they defend against any attack.

The three cases the ticket asks about:
- **Transitive drift.** It can't happen silently here: the graph is deterministic, as shown above. A lock file doesn't prevent anything new. It **records** the full closure, so a bump's transitive moves show up in the PR diff instead of only in `obj/`. That serves the map's "pre-merge review" line of defence: a reviewer or agent can list every package and version the bump touches and diff each one.
- **A floating range in a transitive dependency.** This can't happen. Package dependencies can't float, and the scan found no floating range in our graph.
- **A re-signed or replaced package.** nuget.org refuses to overwrite a version, so it can't change the bytes behind a version we already restore. The hash check would catch a changed copy from somewhere else: a poisoned global packages folder, or a second feed serving the same ID and version. None of that is a hijacked release.

What it doesn't catch: the hijacked release itself. When we bump to the bad version, restore writes its hash into the lock, and locked mode passes from then on.

**Dependabot keeps lock files current.** Its NuGet updater lists `packages.lock.json` among the dependency files it tracks and commits ([ModifiedFilesTracker.cs#L42](https://github.com/dependabot/dependabot-core/blob/cbabeb7b1671021a093ac3990c9e0ac17dc20fdd/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Run/ModifiedFilesTracker.cs#L42)). Its file writer ends with a discovery pass that, per the source comment, "has the benefit of also updating the lock file if it exists" ([FileWriterWorker.cs#L278](https://github.com/dependabot/dependabot-core/blob/cbabeb7b1671021a093ac3990c9e0ac17dc20fdd/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Updater/FileWriters/FileWriterWorker.cs#L278)). The test `EndToEnd_ProjectReferenceWithPackageLockJson` checks that the lock shows the new resolved version ([FileWriterWorkerTests.cs#L185-L231](https://github.com/dependabot/dependabot-core/blob/cbabeb7b1671021a093ac3990c9e0ac17dc20fdd/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core.Test/Update/FileWriters/FileWriterWorkerTests.cs#L185-L231)). It handles Central Package Management as a separate `PackageManagementKind` ([PackageManagementKind.cs](https://github.com/dependabot/dependabot-core/blob/cbabeb7b1671021a093ac3990c9e0ac17dc20fdd/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Discover/PackageManagementKind.cs)). GitHub's docs pages don't mention NuGet lock files at all, so the source code is the only primary evidence.

**Churn, measured.** I generated lock files once with `dotnet restore src/Read2Me.slnx -p:RestorePackagesWithLockFile=true --force-evaluate`, then deleted them.
- That produced 8 files and 2,920 lines (Core 12, TestUtils 211, Services 295, AppData 368, Data 368, App 412, Tests 596, E2eTests 658).
- Each file repeats the closure of every project it references, as `Project` and `CentralTransitive` entries. So bumping one package rewrites every lock that sees it: the `Microsoft.*` 10.0.x family is in all of them except Core.
- The weekly grouped `dotnet` PR would usually touch 6 to 8 lock files, as hash and version swaps.
- Other costs:
  - `check.ps1` and the E2E host would need `--locked-mode` (or `RestoreLockedMode`) for the lock to mean anything.
  - An agent adding or bumping a package would have to run a `--force-evaluate` restore and commit the locks.
  - Because `global.json` doesn't pin the SDK, a new SDK that changes the graph could fail locked mode ([locking dependencies](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies)). Whether .NET 10's platform package pruning moves with SDK patches is untested.

## Package source mapping

What the docs say ([package source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping)):
- Its purpose: "improve your supply chain security, especially if you use a mix of public and private package sources".
- By default, NuGet searches every configured source, and when a package exists on more than one "it may not be deterministic which source the package will be downloaded from".
- Its scope: it "applies only when NuGet downloads packages during restore, install, and update operations". Also, "When the requested package already exists in the global packages folder, no source look-up will happen and the mappings will be ignored."
- [Security best practices](https://learn.microsoft.com/en-us/nuget/concepts/security-best-practices) ties it to "known attacks such as Dependency Confusion".
- The [CPM docs](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) warn with NU1507 when more than one source is configured. The fix is to "map your package sources with package source mapping ... or specify a single package source."

Against a hijacked release: **nothing**. The bad version is on nuget.org under the real ID, and that's exactly the source a mapping would allow.

What it does give here: the repo has no `nuget.config`, so restore uses whatever feeds the machine's user and machine configs list. A repo `nuget.config` with `<clear/>`, nuget.org as the only source, and a `*` mapping makes restore independent of the machine. It stops a stray local or offline feed from serving a package, which is the dependency-confusion shape. It costs one small file, and Dependabot reads the repo `nuget.config`. It's hygiene, not a defence against hijacks.

## Trusted signers (`signatureValidationMode=require`)

What the docs say:
- `signatureValidationMode` is `accept` by default. `require` "will verify that all packages are signed by any of the certificates trusted in the `nuget.config` file". The `trustedSigners` list "cannot be empty" in `require` mode. ([nuget.config reference](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file), [installing signed packages](https://learn.microsoft.com/en-us/nuget/consume-packages/installing-signed-packages))
- There are two kinds of trust:
  - An **author** entry pins a certificate fingerprint.
  - A **repository** entry trusts nuget.org's repository signature, optionally narrowed by `owners`: "If a package has multiple owners, and any one of those owners is in the trusted list, the package installation will succeed." ([installing signed packages](https://learn.microsoft.com/en-us/nuget/consume-packages/installing-signed-packages))
- "all packages uploaded to nuget.org are automatically repository signed". Author-signed packages "provide an extra authentication mechanism to the nuget.org publishing pipeline because the signing certificate must be registered ahead of time." ([signed packages reference](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference))
- nuget.org already enforces signing on the server side: "While a user has one or more certificates registered, all package submissions **must** be signed with one of the certificates." A co-owner with no registered certificate reopens the gap: "If you make an owner who does not have any certificate registered, then unsigned packages will be allowed." Certificates are registered through the website's account settings. ([sign a package](https://learn.microsoft.com/en-us/nuget/create-packages/sign-a-package), [publish a package](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package))
- Verification: "always enabled during package restore" on Windows, on by default from the .NET 8 SDK on Linux, and off by default on macOS ([signed package verification](https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification)).

Our packages, measured with `dotnet nuget verify --all` over the 93 restored packages:
- **73 are author-signed**: Microsoft Corporation 62, xUnit.net (.NET Foundation) 9, Humanizer (.NET Foundation) 1, Json.NET (.NET Foundation) 1.
- **20 are repository-signed only**: Castle.Core, CliWrap, FractionalIndexing, Mono.TextTemplating, NSubstitute, 9 Serilog packages, SQLite, 4 SQLitePCLRaw packages, and VersOne.Epub.

Does it stop a hijacked release from the legitimate owner's compromised account?
- **A stolen API key on an author-signed package** (the usual CI-secret theft): the attacker has no signing key, and nuget.org already rejects unsigned pushes from an account with a registered certificate. So the **server's own rule** blocks this before any client policy comes into play. Client-side `require` adds little, except in the co-owner-without-certificate case.
- **A full account takeover that registers a new certificate**, or a stolen signing key: an author fingerprint pin rejects a new certificate, which is the one case only the client policy catches. A stolen signing key passes.
- **Repository-signed-only packages, which are 20 of ours**: the only trust available is the repository signature plus `owners`. A hijacked owner account is still the owner, so the package passes. **Nothing caught.** These include packages with large blast radius at build and run time (Serilog in the host, NSubstitute and Castle.Core in the tests).

What breaks:
- Every one of the 93 packages has to match an entry. The 20 unsigned ones need a nuget.org repository entry with an `owners` list, and any owner change on nuget.org then breaks restore.
- Every publisher's certificate rotation (Microsoft rotates on a schedule) breaks restore until someone adds the new fingerprint. A grouped Dependabot PR would fail on that until an agent or human updates `nuget.config`. That's recurring maintenance with little security return.
- On macOS, verification is off by default, so the policy would need an opt-in there. The dev host is Windows, so this doesn't bite today.

## Related, for other tickets

[Security best practices](https://learn.microsoft.com/en-us/nuget/concepts/security-best-practices) also mentions a "minimum package publish age" and notes that it "doesn't guarantee package safety". That's the NuGet-side form of the Dependabot `cooldown` the map is weighing. I didn't research its client support here.
