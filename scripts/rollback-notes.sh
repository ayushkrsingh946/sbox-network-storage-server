#!/usr/bin/env bash
# Prints the release-notes line "Rolls back without data loss to: 0.x.y, ..." for a release.
#
# A binary can run on a newer database when its supported schema is at least the database's
# min_binary_schema (the highest `breaking` migration; docs/database.md "Rollback-safe migrations").
# So the older releases that `sbox-ns rollback --keep-data` can return to from release R are the
# tagged releases that (a) know the floor rule at all (their migration list carries SchemaChange tags)
# and (b) support a schema >= the floor of R. Both facts come from the migration list at each tag.
#
# Usage: scripts/rollback-notes.sh [REF] [--tags "v0.5.0 v0.5.1 ..."]
#   REF   the release commit or tag (default HEAD). Tags newer than or equal to REF are ignored.
#   --tags  override the candidate tags (default: every v*.*.* tag without a suffix, reachable from REF).
# Output: one line on stdout, suitable for pasting under the release notes. Exit 0 always except on bad input.
set -euo pipefail

file="src/SboxNetworkStorage.Storage.Sqlite/SqliteMigrations.cs"
ref="HEAD"
tags=""
while [ $# -gt 0 ]; do
  case "$1" in
    --tags) tags="${2:?--tags needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) ref="$1"; shift ;;
  esac
done

git rev-parse --verify --quiet "$ref^{commit}" >/dev/null || { echo "error: unknown ref '$ref'" >&2; exit 2; }

# "<version> <additive|breaking>" per migration at a ref; empty when that ref has no tagged migrations.
migrations_at() {
  git show "$1:$file" 2>/dev/null \
    | sed -nE 's/^[[:space:]]*new\(([0-9]+), ".*", SchemaChange\.(Additive|Breaking), [A-Za-z0-9_]+\),?[[:space:]]*$/\1 \2/p' \
    | tr 'A-Z' 'a-z'
}

# Highest schema version and highest breaking version, "<supported> <floor>".
summarise() { awk 'BEGIN{s=0;f=0} {if($1>s)s=$1; if($2=="breaking"&&$1>f)f=$1} END{print s, f}'; }

release="$(migrations_at "$ref" || true)"
if [ -z "$release" ]; then
  echo "error: $ref has no tagged migrations ($file)" >&2
  exit 2
fi
read -r _ floor <<<"$(printf '%s\n' "$release" | summarise)"

if [ -z "$tags" ]; then
  tags="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --merged "$ref" | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tr '\n' ' ')"
fi

this="$(git describe --tags --exact-match "$ref" 2>/dev/null || true)"
targets=()
for tag in $tags; do
  [ "$tag" = "$this" ] && continue
  rows="$(migrations_at "$tag" || true)"
  [ -z "$rows" ] && continue                      # released before rollback-safe migrations: needs the backup restore
  read -r supported _ <<<"$(printf '%s\n' "$rows" | summarise)"
  [ "$supported" -ge "$floor" ] && targets+=("${tag#v}")
done

if [ "${#targets[@]}" -eq 0 ]; then
  echo "Rolls back without data loss to: none (a breaking migration or an older release; \`sbox-ns rollback\` restores the pre-update backup)"
else
  printf -v joined '%s, ' "${targets[@]}"
  echo "Rolls back without data loss to: ${joined%, }"
fi
