#!/usr/bin/env bash
# Proves that tests fail without the change they come with. A test that also passes on the old code proves nothing.
#
#   scripts/red-on-base.sh <base> [--carry <path>]... -- --project <test project> [filters]
#   scripts/red-on-base.sh HEAD -- --project tests/StrataRec.UnitTests --filter-class StrataRec.UnitTests.Core.ListTakenTests
#
# <base> is the commit without the fix: HEAD while the fix is uncommitted, HEAD~1 once it is committed with its test.
# Checks <base> out in a temporary worktree, puts tests/ of the working tree on top (and each --carry path, for a test
# that needs a changed sample), and runs the given tests there. Exit 0: tests fail on <base>; it prints which, because
# one failing test of the selection is enough for it: select the new tests (--filter-method '*Class.Method*' takes
# one) and read the names. Exit 1: they all pass there. Exit 2: they could not be run, which is not a red test; a test
# of a defect is written against what existed, so it builds.
# A fix that is itself under tests/ (a helper the tests share) is carried along with them: undo it by hand to see the red.
# The worktree is registered in this repository while the script runs: not for a checkout that must stay untouched.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
usage() { sed -n '2,5p' "$0" >&2; exit 2; }
cannot() { echo "red-on-base: $1 Not a red test: exit 2." >&2; exit 2; }

base="${1:-}"; [[ -n "$base" ]] || usage; shift
carry=(tests)
while [[ $# -gt 0 && "$1" != "--" ]]; do
  [[ "$1" == "--carry" && $# -ge 2 ]] || usage
  carry+=("$2"); shift 2
done
[[ "${1:-}" == "--" ]] || usage; shift
# The project is built and run in the temporary tree, so it is named from the repository's root: an absolute path into
# this checkout would run the working tree's tests, which have the fix.
args=(); project=""
while [[ $# -gt 0 ]]; do
  if [[ "$1" == "--project" && $# -ge 2 ]]; then
    project="${2#"$root"/}"; project="${project#./}"
    [[ "$project" != /* && "$project" != ..* ]] || cannot "--project $2 is not inside the repository."
    args+=(--project "$project"); shift 2
  else
    args+=("$1"); shift
  fi
done
[[ -n "$project" ]] || usage
git -C "$root" rev-parse --verify -q "$base^{commit}" >/dev/null || cannot "'$base' is not a commit."

work="$(mktemp -d "${TMPDIR:-/tmp}/red-on-base.XXXXXX")"
trap 'git -C "$root" worktree remove --force "$work" >/dev/null 2>&1 || echo "red-on-base: left $work behind (git worktree remove --force \"$work\")" >&2' EXIT
git -C "$root" worktree add --detach -q "$work" "$base" || cannot "could not check out $base."

cd "$root"
# NUL-separated: a path git would print quoted (one with other than ASCII) is still the path. -P: a link stays a link.
put() { mkdir -p "$work/$(dirname "$1")" && cp -P "$1" "$work/$1" || cannot "could not carry $1."; }
while IFS= read -r -d '' status && IFS= read -r -d '' path; do
  if [[ "$status" == D ]]; then rm -f "$work/$path"; else put "$path"; fi
done < <(git diff --name-status -z --no-renames "$base" -- "${carry[@]}")
while IFS= read -r -d '' path; do put "$path"; done < <(git ls-files --others --exclude-standard -z -- "${carry[@]}")
[[ -e "$work/$project" ]] || cannot "$project is not in $base with the carried paths."

cd "$work"
echo "red-on-base: building $project at $(git rev-parse --short HEAD) with the working tree's ${carry[*]}"
dotnet build "$project" -c Release --nologo -v quiet || cannot "the tests do not build on $base (they use something the change adds)."
status=0
out="$(mktemp "${TMPDIR:-/tmp}/red-on-base.XXXXXX.out")"
dotnet test "${args[@]}" -c Release --no-build > "$out" 2>&1 || status=$?
tail -n 40 "$out"
failed="$(grep -E '^failed ' "$out" | cut -c1-200 || true)"; rm -f "$out"
case "$status" in
  2) echo "red-on-base: ok, on $base these fail (check that the tests of the change are among them):"; printf '%s\n' "$failed" | sed 's/^/  /'; exit 0 ;;
  0) echo "red-on-base: the tests PASS on $base: they do not test the change. Assert the behaviour that was wrong." >&2; exit 1 ;;
  8) cannot "no test matched the filter." ;;
  *) cannot "the run ended with exit code $status (https://aka.ms/testingplatform/exitcodes)." ;;
esac
