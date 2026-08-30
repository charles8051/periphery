#Requires -Version 7.0

<#
.SYNOPSIS
    Runs the release's Linux packaging inside the same container CI uses, so a
    packaging bug is found before a tag is pushed instead of after.

.DESCRIPTION
    Executes scripts/package-cli-linux.sh — the very file the `release-binaries-linux`
    job runs — inside mcr.microsoft.com/dotnet/sdk:10.0, the very image that job
    uses. There is no second implementation to drift, so a pass here means CI
    runs the same bytes in the same environment.

    This exists because three consecutive release bugs were all the same mistake:
    shell written against the author's environment rather than the one that
    executes it.

      * `tar --mode=...`   — GNU tar (Git Bash) accepts it; the Windows runner's
                             bundled bsdtar rejects it outright.
      * two globs under `fail_on_unmatched_files: true` — each matrix job makes
                             exactly one archive, so every job failed on the
                             pattern it did not produce.
      * `set -o pipefail`  — a bash builtin; the SDK container's /bin/sh is dash,
                             so the step died on line 1 before any assertion ran.

    None was catchable by reading the YAML, and all three were trivially
    catchable by running the real command in the real image.

    WHAT THIS DOES NOT CHECK: version stamping. `git archive` strips .git, so
    MinVer cannot see a tag and the build logs MINVER1001 and stamps
    0.0.0-alpha.0. That is expected and harmless here — this verifies PACKAGING
    (does it build, is the binary executable, does the bit survive the archive),
    not versioning. A wrong version in CI shows up as a wrong package version,
    loudly; a dropped execute bit shows up as a binary that will not run on
    someone else's box, silently. This targets the silent one.

    Sources come from `git archive HEAD`, NOT the working tree. Two reasons:
    a tag captures committed state, so that is what should be verified; and it
    structurally excludes Windows bin/ and obj/ directories, whose stale
    artifacts otherwise break a Linux container build in confusing ways.

.PARAMETER Label
    Archive label. Defaults to the current commit's short SHA. Pass the tag you
    intend to push for a full dress rehearsal.

.PARAMETER KeepArtifacts
    Copy the produced tarball out of the container into ./artifacts-verify/
    instead of discarding it.

.EXAMPLE
    ./scripts/Verify-ReleasePackaging.ps1 -Label v3.0.1-alpha.3
    Dress-rehearses the packaging for a tag before `git tag` is run.
#>

[CmdletBinding()]
param(
    [string] $Label,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "docker is required (this runs the CI container image locally)."
    }

    if (-not $Label) { $Label = (git rev-parse --short HEAD) }

    # Warn rather than fail: verifying committed state is the point, but an
    # operator mid-change should be told why their edit had no effect.
    if ((git status --porcelain).Count -gt 0) {
        Write-Warning "Working tree is dirty. This verifies COMMITTED state (git archive HEAD); uncommitted changes are NOT included."
    }

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("periphery-pkgverify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    try {
        Write-Host "==> exporting committed sources ($Label) to $stage" -ForegroundColor Cyan
        # git archive avoids bin/obj entirely — no --exclude list to keep in sync.
        $tarPath = Join-Path $stage 'src.tar'
        git archive --format=tar --output $tarPath HEAD
        if ($LASTEXITCODE -ne 0) { throw "git archive failed ($LASTEXITCODE)" }

        $work = Join-Path $stage 'work'
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        # .NET's tar reader, NOT the `tar` on PATH. On Windows that is usually
        # MSYS/Git tar, which parses a drive-qualified path as a remote
        # host:path spec and fails with "Cannot connect to C: resolve failed".
        # Depending on an external tool here would reintroduce the exact class of
        # bug this script exists to catch, so extraction uses the BCL instead.
        [System.Formats.Tar.TarFile]::ExtractToDirectory($tarPath, $work, $false)

        Write-Host "==> running scripts/package-cli-linux.sh in mcr.microsoft.com/dotnet/sdk:10.0" -ForegroundColor Cyan
        Write-Host "    (the same script and the same image the release-binaries-linux job uses)" -ForegroundColor DarkGray

        # Named so it can be reaped explicitly. --rm covers the happy path and
        # Ctrl-C, but it is the docker CLI that issues that cleanup, so a hostile
        # termination of THIS process (Stop-Process, host panic) can leave the
        # container running until the daemon reaps it on its own schedule. The
        # finally below closes that gap.
        $containerName = "periphery-pkgverify-$PID"
        $packagingExit = 1
        try {
            # --network is left at the default: the publish restores from the private
            # feed exactly as CI does, so a credential/feed problem also surfaces here.
            docker run --rm --name $containerName `
                -v "${work}:/src" `
                -w /src `
                mcr.microsoft.com/dotnet/sdk:10.0 `
                bash scripts/package-cli-linux.sh "$Label"
            $packagingExit = $LASTEXITCODE
        }
        finally {
            # No-op when --rm already did its job; the redirect keeps the
            # expected "No such container" quiet.
            docker rm -f $containerName 2>&1 | Out-Null
        }

        if ($packagingExit -ne 0) {
            throw "PACKAGING FAILED in the CI container image (exit $packagingExit). Fix this before tagging — CI will fail the same way."
        }

        if ($KeepArtifacts) {
            $dest = Join-Path $repoRoot 'artifacts-verify'
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Copy-Item (Join-Path $work 'artifacts/*.tar.gz') $dest -Force
            Write-Host "==> artifacts copied to $dest" -ForegroundColor Green
        }

        Write-Host ""
        Write-Host "PASS — packaging works in the image CI uses, and the execute bit survived into the archive." -ForegroundColor Green
    }
    finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Pop-Location
}
