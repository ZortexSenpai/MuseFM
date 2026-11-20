#!/usr/bin/env bash
# Works out the next version from the Conventional Commits since the last vX.Y.Z tag reachable from HEAD.
#
#   breaking change ("feat!: …" or a "BREAKING CHANGE: …" footer)  → major
#   feat                                                           → minor
#   fix, perf, revert                                              → patch
#   anything else (docs, chore, refactor, test, ci, …)             → no release
#
# Without any tag yet, the first release is INITIAL_VERSION (default 0.1.0).
# Prints version, tag, bump and release as key=value lines, and appends them to $GITHUB_OUTPUT when set.
set -euo pipefail

initial_version="${INITIAL_VERSION:-0.1.0}"

last_tag="$(git tag --merged HEAD --list 'v*' --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -n 1 || true)"

if [[ -z "$last_tag" ]]; then
  bump=initial
  version="$initial_version"
else
  IFS=. read -r major minor patch <<<"${last_tag#v}"
  subjects="$(git log --format=%s "$last_tag..HEAD")"
  bodies="$(git log --format=%b "$last_tag..HEAD")"
  type='[a-zA-Z]+(\([^)]*\))?'

  if grep -qE "^$type!: " <<<"$subjects" || grep -qE '^BREAKING[ -]CHANGE: ' <<<"$bodies"; then
    bump=major
    major=$((major + 1)) minor=0 patch=0
  elif grep -qiE '^feat(\([^)]*\))?: ' <<<"$subjects"; then
    bump=minor
    minor=$((minor + 1)) patch=0
  elif grep -qiE '^(fix|perf|revert)(\([^)]*\))?: ' <<<"$subjects"; then
    bump=patch
    patch=$((patch + 1))
  else
    bump=none
  fi
  version="$major.$minor.$patch"
fi

release=true
if [[ "$bump" == none ]]; then
  release=false
elif git rev-parse -q --verify "refs/tags/v$version" >/dev/null; then
  # A re-run of an old commit: that version was already published from a later one.
  echo "v$version already exists; not releasing it again." >&2
  release=false
fi

result="version=$version
tag=v$version
bump=$bump
release=$release"

echo "$result"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "$result" >>"$GITHUB_OUTPUT"
fi
