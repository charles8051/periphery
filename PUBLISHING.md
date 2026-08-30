# Publishing Guide

Pushing a `v*.*.*` tag publishes every packable project to
[nuget.org](https://www.nuget.org/profiles/charles8051). Nothing else publishes:
there is no manual path, and no API key stored anywhere.

## Quick Publish Workflow

```sh
# 1. Make your changes and commit
git add .
git commit -m "feat: add network adapter enumeration"

# 2. Tag the release (annotated, semver, `v` prefix — MinVer reads the tag)
git tag -a v3.2.0 -m "v3.2.0"

# 3. Push commits and tag
git push && git push --tags
```

GitHub Actions (`publish.yml`) will automatically:
1. Gate the release on Linux and Windows test jobs — Release build, **unit suites only**
   (`--filter "Category!=Integration"`). Device-backed tests never run here.
2. Rebuild non-incrementally so every assembly comes from the tagged commit, then pack
   those exact outputs. A completeness gate asserts every `IsPackable` project in
   `Periphery.slnx` produced a `.nupkg` — a partial family fails the release.
3. Exchange a GitHub OIDC token for a one-hour nuget.org key, then push with
   `--skip-duplicate`, versioned from the tag (`v4.1.0` → `Periphery.4.1.0.nupkg`).
4. Attach self-contained `Periphery.Cli` builds to the GitHub Release — `win-x64`
   and `win-arm64` zips and a `linux-x64` tar.gz — plus the dual-mode
   `treehopper-flash.exe`. A hyphen in the tag marks it a prerelease.

### The `net8.0` assets are published untested, on purpose

Libraries multi-target `net8.0;net10.0`, but the test projects are `net10.0`-only and
the release gate runs only that leg. **The `net8.0` assets we publish are compiled and
never executed by any test.**

This is a named, accepted decision, not an oversight — see
[ADR-0069](docs/adr/0069-restore-net8-tfm-untested.md), which restored the TFM
(D1), deliberately kept it out of CI (D2), and states the resulting risk (D3): runtime
behaviour differences the compiler cannot see, such as `TimeProvider`/`Task` scheduling,
`LibraryImport` marshalling shape, or BCL changes between .NET 8 and .NET 10.

`net8.0` is offered **best-effort**. Every known consumer is on `net10.0`.
A consumer adopting `net8.0` in earnest should run their own suite against it — and
that is D4's trigger to revisit D2 and add a `net8.0` test leg.

## Version Scheme

**Periphery is past 1.0 and follows standard Semantic Versioning.** The current
line is **4.x**; versions come from the git tag via
MinVer (`MinVerTagPrefix=v` in `Directory.Build.props`), so the tag *is* the version
and nothing in a `.csproj` needs editing.

| Change | Bump | Example |
| --- | --- | --- |
| Removed or changed a public/protected member, an interface member, or a record's positional shape | **MAJOR** | 4.0.0 → 5.0.0 |
| Added public surface, backwards-compatibly | **MINOR** | 4.0.0 → 4.1.0 |
| Fix, internal refactor, docs, dependency bump | **PATCH** | 4.0.0 → 4.0.1 |

Two things this repo has been bitten by, worth checking before you tag:

- **A behaviour change is breaking even when the signature is not.** If callers
  can observe a different result from the same call, that is a MAJOR bump
  regardless of what the compiler says.
- **Interface members carry no access modifier**, so a grep for removed
  `public`/`protected` declarations cannot see a changed interface member — the
  single most breaking thing a library can ship, since every implementer breaks.
  Diff the interface files by eye. (A sibling library shipped exactly this defect
  as a patch.)

Read `CHANGELOG.md`'s `[Unreleased]` section before choosing: **it accumulates
across changes**, so a MINOR addition released while an unreleased breaking
change is pending still ships as a MAJOR. The version reflects everything in the
release, not the last thing merged into it.

## Consuming the Package

```sh
dotnet add package Periphery
```

That is the whole procedure. The packages are public on nuget.org, so there is no
token, no `nuget.config`, and nothing to configure in CI.

## Testing a Package Locally (Before Publishing)

```sh
# Create a test package
dotnet pack src/Periphery/Periphery.csproj --configuration Release --output test-nupkgs -p:PeripheryLocalFeedDisable=true

# Add as a local source in the consuming project (absolute path to test-nupkgs)
dotnet nuget add source /path/to/periphery/test-nupkgs --name periphery-local

# Install it (use whatever version the pack produced)
dotnet add package Periphery --version 4.0.1-alpha.0.1
```

## Dry run

`workflow_dispatch` on the Release workflow is a real dry run. Everything runs --
restore, build, pack, the completeness gate, and the nuget.org OIDC exchange --
and only the push is skipped, because that step alone is gated on
`github.event_name == 'push'`.

```bash
gh workflow run publish.yml -R charles8051/periphery --ref main
```

Look for this line in the `NuGet login` step:

```
Successfully exchanged OIDC token for NuGet API key.
```

If the Trusted Publishing policy is wrong, that is where you find out, having
published nothing.

**The version a dry run produces is not the version a release would produce.**
MinVer reads the nearest tag, so a dispatch from `main` yields the last tag plus
a height (`4.0.0-alpha.2.33`), not the next release number. To check what a
specific tag would produce, tag locally and pack -- without pushing, which is
what would trigger a real release:

```bash
git tag -a v4.1.0 -m v4.1.0
dotnet pack src/Periphery -c Release -o /tmp/pk -p:PeripheryLocalFeedDisable=true
git tag -d v4.1.0
```

## Troubleshooting

### `NuGet/login` fails, or "Unable to get an access token"

The Trusted Publishing policy does not match the run. A policy binds to four
things and **all four** must agree with what the workflow actually presents:
owner, repository, workflow **filename** (`publish.yml`, no path), and
environment (`nuget.org`). Renaming this workflow file breaks publishing.

Two behaviours from nuget.org's docs that look like bugs and are not:

- **The key lasts one hour.** `NuGet/login` therefore runs immediately before the
  push rather than early in the job. Moving it earlier is how a long release run
  starts failing intermittently.
- **A policy created against a private repository is "temporarily active" for 7
  days** and goes permanently inactive if no publish happens in that window.
  nuget.org needs the repository and owner IDs from a real publish to pin the
  policy against resurrection attacks. Public repositories are unaffected.

### "Version already exists"

nuget.org does not allow deleting or replacing a published version. `--skip-duplicate`
means a re-run of the same tag is a no-op rather than a failure. Bump and tag again.

### The version came out `0.0.0-alpha.0`

MinVer saw no tag. Either the checkout was shallow (`fetch-depth: 0` is required
and is set) or the job is running on a ref with no `v*` tag in its history.
`-p:PackageVersion` does not help — MinVer overrides it.

## One-time setup

Already done, recorded so it can be redone on a fresh repository:

```bash
gh api -X PUT repos/charles8051/periphery/environments/nuget.org
gh variable set NUGET_USER --body "<nuget.org profile name>" -R charles8051/periphery
```

`NUGET_USER` is the nuget.org **profile name, not an email address**. It is a
repo *variable* rather than a secret: the value is public, and a secret would be
masked to `***` in the log exactly when a failed exchange makes you want to read
it back. Then, on
nuget.org: account menu → Trusted Publishing → add a policy with the four fields
above, and **scope the glob to `Periphery*`**. A policy binds to a package *owner*,
not to a repository, so an unscoped one would let this repository publish any
package the account owns.

Neither the environment nor the variable survives a repository rename or reseed.
The policy does — it names an owner and repository by name, and a recreated
repository at the same name satisfies it.

## Viewing Published Packages

https://www.nuget.org/profiles/charles8051

---

**Registry:** https://api.nuget.org/v3/index.json (public)  
**Authentication:** nuget.org Trusted Publishing (OIDC). No stored key.
