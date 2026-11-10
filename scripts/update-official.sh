#!/usr/bin/env bash
# Move this app to a new official Jellyfin Android TV release, keeping our changes.
#
#   scripts/update-official.sh v0.19.11
#
# How: the code becomes exactly the official release, then our own changes (everything
# that differs from the official release we were on) are applied on top again.
# This is safer than a plain merge: upstream's release branches carry copies of
# the same fixes, which a merge can silently duplicate. The result is committed
# as a merge of the official tag, so `git tag --merged` keeps working.
#
# Exit codes: 0 = updated (or already up to date), 1 = our changes don't apply
# cleanly (conflict markers are left in the files; fix them, then `git add` and
# `git commit`), 2 = usage error.
set -euo pipefail

target=${1:?usage: scripts/update-official.sh <official tag, e.g. v0.19.11>}
upstream=https://github.com/jellyfin/jellyfin-androidtv.git

cd "$(git rev-parse --show-toplevel)"
if [ -n "$(git status --porcelain)" ]; then
	echo "Working tree not clean; commit or stash first." >&2
	exit 2
fi

git rev-parse -q --verify "refs/tags/$target" >/dev/null ||
	git fetch --quiet --no-tags "$upstream" "refs/tags/$target:refs/tags/$target"

# The official release we're currently based on
current=$(git tag --merged HEAD --list 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -1)
if git merge-base --is-ancestor "$target" HEAD; then
	echo "Already contains $target."
	exit 0
fi
echo "Updating from $current to $target"

# Our changes, relative to the official release we're on
patch=$(mktemp)
trap 'rm -f "$patch"' EXIT
git diff --binary "$current" HEAD > "$patch"

# Record the merge, then replace everything with the official release
git merge --quiet -s ours --no-commit "$target" >/dev/null
git read-tree -u --reset "$target"

# Re-apply our changes
if [ -s "$patch" ] && ! git apply --3way --whitespace=nowarn "$patch"; then
	echo
	echo "Our changes conflict with $target in:" >&2
	git diff --name-only --diff-filter=U >&2
	exit 1
fi

git add -A
git commit --quiet -m "Update to official Jellyfin Android TV $target"
echo "Done: official $target plus the Pickyfin changes."
