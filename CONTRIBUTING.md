# Contributing to Periphery

Thanks for looking. This is a small project maintained by one person, so the most
useful thing you can do is open an issue before writing much code — it is easier to
agree on shape than to unpick a finished branch.

## Before you start

**Licence.** Periphery is under [PolyForm Small Business 1.0.0](LICENSE.md), which is
source-available rather than open source. Read it before contributing; it is short,
and it restricts *use* in ways an OSI licence would not.

**Issues are the backlog.** There is no separate tracker and no roadmap file — open
issues are the whole list.

## Building

Requires the .NET 10 SDK. `Periphery.slnx` needs **SDK 9.0.200 or newer** to parse
at all; an older SDK fails before it reaches any project.

```bash
dotnet restore Periphery.slnx
dotnet build Periphery.slnx --configuration Release
```

Libraries multi-target `net8.0;net10.0`. Test projects target `net10.0` only.

By default a build also packs every `src/` project into a user-level local NuGet
feed, which is how projects developed alongside Periphery consume it. Set
`-p:PeripheryLocalFeedDisable=true` to skip that; CI does.

## Testing

```bash
dotnet test Periphery.slnx --configuration Release --filter "Category!=Integration"
```

That is what CI runs, and what your change needs to keep green.

`Category=Integration` tests need real hardware — a webcam, HID devices, a
Treehopper board, a Linux device rig — and are excluded from CI because hosted
runners have none. You are not expected to run them, and a PR will not be judged
on them.

## Formatting

CSharpier is pinned as a local tool:

```bash
dotnet tool restore
dotnet csharpier format <the files you changed>
```

**Do not reformat the repository.** The existing tree predates the tool and
CSharpier would rewrite most of it; a repo-wide pass is a deliberate change of its
own. Format the files you touch and leave the rest alone.

## Architecture decisions

Anything that changes a public contract, a platform behaviour, or a design
invariant gets an ADR in [`docs/adr/`](docs/adr/). Read a recent one for the shape —
frontmatter, a Context that says what was measured, numbered Decisions, and
Consequences that are honest about the cost.

Two conventions worth knowing:

- **Claims are measured, not assumed.** ADRs here cite what was observed on real
  hardware, and record when a hypothesis was falsified. "It should work like this"
  is not a finding.
- **Absence is its own state.** Where a platform cannot answer a question,
  Periphery says so rather than guessing a plausible default. See ADR-0073.

## Pull requests

- One concern per PR. A mechanical rename and a behaviour change do not belong
  together.
- Explain *why* in the description, not just what. The diff already says what.
- An automated reviewer comments on PRs. It is often right and sometimes wrong —
  push back in the thread when you disagree, with your reasoning.
- CI must be green. If a test fails in a way you cannot reproduce locally, say so
  rather than retrying silently; some are platform-specific.

## Reporting bugs

Include the platform and OS version, the .NET version, what hardware was attached,
and what you expected versus what happened. For enumeration problems the raw
`DeviceInfo` — `Id`, `LocationPath`, `ParentId` — is usually the thing that makes a
report actionable.

For anything security-related, do not open an issue. See [SECURITY.md](SECURITY.md).
