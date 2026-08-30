#!/usr/bin/env bash
#
# Validates the ADR corpus in docs/adr/ (periphery#297).
#
# The corpus drifted into two mutually exclusive header formats, so any grep,
# script, or agent that checked one silently saw a subset — and `status` could
# not be trusted to say whether a decision was live. This keeps the corpus in
# one shape:
#
#   * every ADR opens with YAML frontmatter, at byte 0 (a UTF-8 BOM in front of
#     the `---` is what hid six files from `startswith("---")` parsers before);
#   * `status` is one of a fixed vocabulary, with any qualifier in `status_note`
#     rather than free text inside the value;
#   * cross-references resolve to files that exist, and ADR numbers are unique.
#
# Usage: scripts/validate-adrs.sh [adr-dir]
# Exits non-zero, listing every problem, if the corpus does not conform.

set -uo pipefail

ADR_DIR="${1:-docs/adr}"
ALLOWED=(Proposed Accepted Rejected Superseded Informational)
REQUIRED=(title status date authors tags supersedes superseded_by)

problems=0
note() { printf '  %s\n' "$*"; problems=$((problems + 1)); }

if [[ ! -d $ADR_DIR ]]; then
  echo "no such directory: $ADR_DIR" >&2
  exit 2
fi

shopt -s nullglob
files=("$ADR_DIR"/*.md)
if ((${#files[@]} == 0)); then
  echo "no ADRs found in $ADR_DIR" >&2
  exit 2
fi

echo "Validating ${#files[@]} ADRs in $ADR_DIR"

# ── Corpus-level: ADR numbers must be unique ────────────────────────────────
dupes=$(for f in "${files[@]}"; do basename "$f" | cut -c1-4; done | sort | uniq -d)
if [[ -n $dupes ]]; then
  while read -r n; do
    [[ -z $n ]] && continue
    matches=""
    for m in "$ADR_DIR/$n"-*.md; do matches+="$(basename "$m") "; done
    note "duplicate ADR number $n: ${matches% }"
  done <<<"$dupes"
fi

# ── Per file ────────────────────────────────────────────────────────────────
for f in "${files[@]}"; do
  name=$(basename "$f")

  if [[ ! -s $f ]]; then
    note "$name: file is empty"
    continue
  fi

  # Byte 0 must be '-'. A UTF-8 BOM here parses as frontmatter to a human and
  # as body text to every YAML reader, which is exactly how six files drifted.
  if [[ $(head -c 3 "$f") != "---" ]]; then
    if [[ $(head -c 3 "$f" | od -An -tx1 | tr -d ' ') == "efbbbf" ]]; then
      note "$name: starts with a UTF-8 BOM before the frontmatter"
    else
      note "$name: does not open with YAML frontmatter"
    fi
    continue
  fi

  # `closed` distinguishes a terminated block from one that ran to EOF — without
  # it an unterminated frontmatter swallows the whole document and still looks
  # like a valid block, so every key check would pass against the body text.
  block=$(awk 'NR==1 && /^---$/ {inb=1; next}
               inb && /^---$/ {closed=1; exit}
               inb {print}
               END {if (!closed) exit 3}' "$f")
  case $? in
    3) note "$name: frontmatter is never closed by a second '---'"; continue ;;
  esac
  if [[ -z $block ]]; then
    note "$name: frontmatter block is empty"
    continue
  fi

  # Every line in the block must look like YAML. This is what actually catches an
  # unterminated frontmatter: ADR bodies contain `---` horizontal rules, so an
  # unclosed block simply ends at the first one and looks terminated — with the
  # H1 and prose swallowed into it. Checking for a closing delimiter cannot see
  # that; checking the block's contents can.
  while IFS= read -r l; do
    [[ -z ${l//[[:space:]]/} ]] && continue
    if ! [[ $l =~ ^[a-z_]+:  || $l =~ ^[[:space:]]+-[[:space:]] || $l =~ ^[[:space:]]+[^[:space:]] ]]; then
      note "$name: frontmatter contains a non-YAML line (unterminated block?): ${l:0:60}"
      break
    fi
  done <<<"$block"

  # Two tiers. Every required key must at least carry something after the colon,
  # so a bare `title:` fails while `supersedes: ""` passes — an empty string is a
  # legitimate "no such relationship", not a missing value.
  #
  # `title` and `status` are held to more: they may not be the empty string
  # either, since both are always knowable for any ADR worth having. `date` and
  # `authors` are deliberately NOT in that tier — a few older ADRs record neither,
  # and the honest representation of "nobody wrote it down" is `""` rather than a
  # fabricated value invented to satisfy a linter.
  for key in "${REQUIRED[@]}"; do
    if ! grep -qE "^${key}:" <<<"$block"; then
      note "$name: missing required key '$key'"
      continue
    fi
    if ! grep -qE "^${key}:[[:space:]]*[^[:space:]]" <<<"$block"; then
      note "$name: required key '$key' has no value"
      continue
    fi
    case $key in
      title | status)
        val=$(grep -E "^${key}:" <<<"$block" | head -1 |
          sed -E "s/^${key}:[[:space:]]*//; s/[[:space:]]*$//; s/^[\"']//; s/[\"']$//")
        [[ -n $val ]] || note "$name: required key '$key' is empty"
        ;;
    esac
  done

  # Strip the key, then the surrounding quotes separately — POSIX ERE has no
  # lazy quantifier, so a single `"?(.*?)"?` pattern leaves the closing quote on.
  # Both quote styles: YAML accepts either, and a single-quoted status would
  # otherwise fail the vocabulary check with a baffling message about `'Accepted'`.
  status=$(grep -E '^status:' <<<"$block" | head -1 |
    sed -E "s/^status:[[:space:]]*//; s/[[:space:]]*$//; s/^[\"']//; s/[\"']$//")
  if [[ -n $status ]]; then
    ok=0
    for a in "${ALLOWED[@]}"; do [[ $status == "$a" ]] && ok=1; done
    ((ok)) || note "$name: status '$status' is not one of: ${ALLOWED[*]}"
  fi

  # Cross-references must resolve. Values are quoted filenames, possibly a list.
  for key in supersedes superseded_by depends_on amended; do
    line=$(grep -E "^${key}:" <<<"$block" | head -1) || true
    [[ -z $line ]] && continue
    for ref in $(grep -oE '"[^"]+\.md"' <<<"$line" | tr -d '"'); do
      [[ -f "$ADR_DIR/$ref" ]] || note "$name: $key -> '$ref' does not exist"
    done
  done

  # The body must not contradict the frontmatter status. Bodies used to open with
  # a `Proposed | Accepted | Rejected | Superseded | Deprecated` picker line copied
  # from the template, and in five files that copy had drifted out of agreement
  # with the frontmatter (ADR-0032 and ADR-0034 read `Proposed` above and
  # `Accepted` below; ADR-0075 and ADR-0076 said `**Proposed** (accepted on merge)`
  # under an `Accepted` header). Frontmatter `status:` is the one source of truth.
  #
  # Two shapes are rejected, both only on the FIRST declaring line of the `## Status`
  # section: the pipe-separated picker, and a leading status word that names a
  # different status than the frontmatter. Prose in a `## Status` section is fine —
  # this checks the line that *declares* a status, not one that mentions another
  # ADR's (ADR-0078's rejection cites ADR-0079 being Accepted, and must pass).
  words='(Proposed|Accepted|Rejected|Superseded|Deprecated)'
  in_status=0
  while IFS= read -r l; do
    if [[ $l =~ ^##[[:space:]]+Status[[:space:]]*$ ]]; then in_status=1; continue; fi
    ((in_status)) || continue
    [[ $l =~ ^## ]] && break                       # next section; nothing declared
    [[ -z ${l//[[:space:]]/} || $l == '---' ]] && continue
    if [[ $l =~ ^(\*\*)?$words(\*\*)?[[:space:]]*\|[[:space:]]*(\*\*)?$words ]]; then
      note "$name: body carries a status picker line (frontmatter is the source of truth): ${l:0:60}"
    elif [[ $l =~ ^(\*\*)?$words ]] && [[ ${BASH_REMATCH[2]} != "$status" ]]; then
      note "$name: body declares '${BASH_REMATCH[2]}' but frontmatter says '$status': ${l:0:60}"
    fi
    break                                          # only the first declaring line
  done <"$f"

  # The title should carry the same number as the filename.
  num=${name:0:4}
  title=$(grep -E '^title:' <<<"$block" | head -1)
  grep -qE "ADR-${num}" <<<"$title" || note "$name: title does not name ADR-${num}: $title"
done

echo
if ((problems)); then
  echo "FAILED: $problems problem(s)"
  exit 1
fi
echo "OK: corpus is consistent"
