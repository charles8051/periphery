#!/usr/bin/env bash
# Moves every recorded public API change from PublicAPI.Unshipped.txt into
# PublicAPI.Shipped.txt, for each project and each PublicAPI/<tfm>/ pair.
#
#   scripts/ship-public-api.sh           # promote
#   scripts/ship-public-api.sh --check   # exit 1 if anything is unshipped
#
# Part of cutting a release: run it in the commit that retitles the changelog,
# so the tag's Shipped files are the surface that tag publishes. An added line
# joins Shipped; a `*REMOVED*<line>` deletes <line> from Shipped. Shipped files
# are written in byte order.
set -euo pipefail

check=0
[ "${1:-}" = "--check" ] && check=1

cd "$(git rev-parse --show-toplevel)"
pending=0

while IFS= read -r u; do
  body="$(grep -v '^#nullable' "$u" | grep -v '^[[:space:]]*$' || true)"
  [ -n "$body" ] || continue
  n="$(printf '%s\n' "$body" | wc -l | tr -d ' ')"
  pending=$((pending + n))
  if [ "$check" = 1 ]; then
    echo "$u: $n unshipped line(s)"
    continue
  fi

  s="$(dirname "$u")/PublicAPI.Shipped.txt"
  [ -f "$s" ] || printf '#nullable enable\n' > "$s"
  removed="$(printf '%s\n' "$body" | sed -n 's/^\*REMOVED\*//p')"
  added="$(printf '%s\n' "$body" | grep -v '^\*REMOVED\*' || true)"

  missing="$(printf '%s\n' "$removed" | grep -v '^$' | grep -vxFf "$s" || true)"
  if [ -n "$missing" ]; then
    echo "$u: *REMOVED* names a line that is not in $s:" >&2
    printf '  %s\n' "$missing" >&2
    exit 1
  fi

  tmp="$(mktemp)"
  { echo '#nullable enable'
    { grep -v '^#nullable' "$s" | grep -vxFf <(printf '%s\n' "$removed" | grep -v '^$' || printf '\n') || true
      printf '%s\n' "$added"
    } | grep -v '^[[:space:]]*$' | LC_ALL=C sort -u
  } > "$tmp"
  mv "$tmp" "$s"
  printf '#nullable enable\n' > "$u"
  echo "$u: $n line(s) shipped"
done < <(find src -name PublicAPI.Unshipped.txt -not -path '*/obj/*' -not -path '*/bin/*' | LC_ALL=C sort)

if [ "$check" = 1 ] && [ "$pending" -gt 0 ]; then
  echo "Run scripts/ship-public-api.sh and commit the result before tagging." >&2
  exit 1
fi
[ "$pending" -gt 0 ] || echo "Nothing unshipped."
