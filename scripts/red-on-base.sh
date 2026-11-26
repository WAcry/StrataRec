#!/usr/bin/env bash
# Proves that tests fail without the change they come with. A test that also passes on the old code proves nothing.
#
#   scripts/red-on-base.sh <base> [--carry <path>]... -- --project <test project> [filters]
#   scripts/red-on-base.sh HEAD -- --project tests/StrataRec.UnitTests --filter-class StrataRec.UnitTests.Core.ListTakenTests
#
# <base> is the commit without the fix: HEAD while the fix is uncommitted, HEAD~1 once it is committed with its test.
# Checks <base> out in a temporary worktree, puts tests/ of the working tree on top (and each --carry path, for a test
# that needs a changed sample), and runs the given tests there. Exit 0: they fail on <base>. Exit 1: they pass there.
# Exit 2: they could not be run, which is not a red test; a test of a defect is written against what existed, so it builds.
# A fix that is itself under tests/ (a helper the tests share) is carried along with them: undo it by hand to see the red.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
usage() { sed -n '2,5p' "$0" >&2; exit 2; }

base="${1:-}"; [[ -n "$base" ]] || usage; shift
carry=(tests)
while [[ $# -gt 0 && "$1" != "--" ]]; do
  [[ "$1" == "--carry" && $# -ge 2 ]] || usage
  carry+=("$2"); shift 2
done
[[ "${1:-}" == "--" ]] || usage; shift
project=""
for ((i = 1; i <= $#; i++)); do
  [[ "${!i}" == "--project" ]] && { j=$((i + 1)); project="${!j:-}"; }
done
[[ -n "$project" ]] || usage

work="$(mktemp -d "${TMPDIR:-/tmp}/red-on-base.XXXXXX")"
trap 'git -C "$root" worktree remove --force "$work" >/dev/null 2>&1 || true' EXIT
git -C "$root" worktree add --detach -q "$work" "$base"

cd "$root"
{ git diff --name-status --no-renames "$base" -- "${carry[@]}"; git ls-files --others --exclude-standard -- "${carry[@]}" | sed 's/^/A\t/'; } |
  while IFS=$'\t' read -r status path; do
    if [[ "$status" == D ]]; then
      rm -f "$work/$path"
    else
      mkdir -p "$work/$(dirname "$path")"
      cp "$path" "$work/$path"
    fi
  done

cd "$work"
echo "red-on-base: building $project at $(git rev-parse --short HEAD) with the working tree's ${carry[*]}"
if ! dotnet build "$project" -c Release --nologo -v quiet; then
  echo "red-on-base: the tests do not build on $base (they use something the change adds). Not a red test: exit 2." >&2
  exit 2
fi
status=0
dotnet test "$@" -c Release --no-build | tail -n 40 || status=$?
case "$status" in
  2) echo "red-on-base: ok, the tests fail on $base."; exit 0 ;;
  0) echo "red-on-base: the tests PASS on $base: they do not test the change. Assert the behaviour that was wrong." >&2; exit 1 ;;
  8) echo "red-on-base: no test matched the filter. Not a red test: exit 2." >&2; exit 2 ;;
  *) echo "red-on-base: the run ended with exit code $status (https://aka.ms/testingplatform/exitcodes). Not a red test: exit 2." >&2; exit 2 ;;
esac
