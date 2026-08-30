#!/usr/bin/env bash
#
# Publishes and packages the self-contained linux-x64 Periphery.Cli, and asserts
# the result is actually runnable.
#
# THIS SCRIPT IS THE SINGLE SOURCE OF TRUTH, AND THAT IS THE ENTIRE POINT.
# The release workflow calls it, and scripts/Verify-ReleasePackaging.ps1 runs
# this same file inside the same container image locally. Verifying locally
# therefore exercises the bytes CI will execute, not a re-implementation that
# can drift from them.
#
# It exists because three consecutive release bugs came from writing shell
# against the environment in front of the author rather than the one that runs
# it: `tar --mode` (GNU tar in Git Bash accepts it, the Windows runner's bsdtar
# rejects it), a two-glob `fail_on_unmatched_files` (each job produces one
# archive), and `set -o pipefail` (a bash builtin; the SDK container's /bin/sh
# is dash). Each was only discoverable by running the real commands in the real
# image — so that is now a thing you can do before tagging.
#
# Usage:  package-cli-linux.sh <label> [output-dir]
#   label       goes in the archive name; a tag like v3.0.1-alpha.2, or a branch
#               (slashes are normalised, since '/' is not a legal filename char)
#   output-dir  defaults to ./artifacts
#
# bash, not sh: `pipefail` is a bash builtin. Declared in the shebang AND in the
# workflow's `shell:` key, because the container default is dash.

set -euo pipefail

label_raw="${1:?usage: package-cli-linux.sh <label> [output-dir]}"
outdir="${2:-artifacts}"

# github.ref_name is the BRANCH on a workflow_dispatch, and a branch such as
# fix/release-linux-shell carries a '/' that cannot appear in a filename.
# Whitespace is collapsed too: quoting makes it survive correctly, but a
# tarball whose filename contains spaces is a trap for every consumer that
# later globs for it.
label="$(printf '%s' "$label_raw" | tr '/\\' '--' | tr -s '[:space:]' '-')"

publish_dir="${outdir}/Periphery.Cli-linux-x64"
tarball="${outdir}/Periphery.Cli-${label}-linux-x64.tar.gz"

echo "==> publishing linux-x64 (self-contained, single-file)"
dotnet publish src/Periphery.Cli/Periphery.Cli.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PeripheryLocalFeedDisable=true \
    --output "$publish_dir"

# Assertion 1 — the bit exists on disk. On Linux the SDK sets it on the apphost;
# this is what silently does NOT happen when the publish runs on Windows, where
# NTFS has no execute bit to set.
echo "==> asserting the published binary is executable"
if [ ! -x "${publish_dir}/Periphery.Cli" ]; then
    echo "::error::Periphery.Cli is not executable before packaging"
    ls -l "${publish_dir}/Periphery.Cli" || true
    exit 1
fi

echo "==> packaging ${tarball}"
tar -czf "$tarball" -C "$publish_dir" Periphery.Cli

# Assertion 2 — and it survived INTO the archive. Distinct from assertion 1:
# a packer that drops permissions (Compress-Archive, or bsdtar given the wrong
# flags) passes the first check and fails this one. This is the check that
# would have caught the original bug before a user did.
echo "==> asserting the execute bit survived into the archive"
if ! tar -tvzf "$tarball" | grep -qE '^-rwx'; then
    echo "::error::execute bit not preserved in ${tarball}"
    tar -tvzf "$tarball"
    exit 1
fi

echo "==> OK"
tar -tvzf "$tarball"
ls -l "$tarball"
