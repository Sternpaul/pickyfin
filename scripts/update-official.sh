#!/usr/bin/env bash
# Move this app to a new official Jellyfin Android TV release, keeping our changes.
#
#   scripts/update-official.sh v0.19.11
#
# .github/official-version holds the official release we're based on. This repo
# doesn't carry the official git history, so updating works on file contents:
# the code becomes exactly the new official release, then our own changes
# (everything that differs from the official release we were on) are applied on
# top again. That's also safer than a merge: upstream's release branches carry
# copies of the same fixes, which a merge can silently duplicate.
#
# Exit codes: 0 = updated (or nothing to do), 1 = our changes don't apply cleanly
# (conflict markers are left in the files; fix them, then `git add -A` and
# `git commit`), 2 = usage error.
set -euo pipefail

target=${1:?usage: scripts/update-official.sh <official tag, e.g. v0.19.11>}
upstream=https://github.com/jellyfin/jellyfin-androidtv.git
version_file=.github/official-version

cd "$(git rev-parse --show-toplevel)"
if [ -n "$(git status --porcelain)" ]; then
	echo "Working tree not clean; commit or stash first." >&2
	exit 2
fi

current=$(tr -d '[:space:]' < "$version_file")
if [ "$current" = "$target" ] || [ "$(printf '%s\n%s\n' "$current" "$target" | sort -V | tail -1)" = "$current" ]; then
	echo "Already on $current; nothing to do for $target."
	exit 0
fi
echo "Updating from $current to $target"

for tag in "$current" "$target"; do
	git rev-parse -q --verify "refs/tags/$tag" >/dev/null ||
		git fetch --quiet --no-tags --depth=1 "$upstream" "refs/tags/$tag:refs/tags/$tag"
done

# Our changes, relative to the official release we're on
patch=$(mktemp)
trap 'rm -f "$patch"' EXIT
git diff --binary "$current" HEAD > "$patch"

# Replace everything with the official release, then re-apply our changes
git read-tree -u --reset "$target"
status=0
if [ -s "$patch" ] && ! git apply --3way --whitespace=nowarn "$patch"; then
	status=1
fi
echo "$target" > "$version_file"

if [ $status -ne 0 ]; then
	echo
	echo "Our changes conflict with $target in:" >&2
	git diff --name-only --diff-filter=U >&2
	exit 1
fi

git add -A
git commit --quiet -m "Update to official Jellyfin Android TV $target"
echo "Done: official $target plus the Pickyfin changes."
