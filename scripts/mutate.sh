#!/usr/bin/env bash
# Mutation testing of what a change touched: would a test notice if the changed code were broken on purpose?
#
#   scripts/mutate.sh [<base>]            the src files changed since <base>; default: where the branch left origin/main
#   scripts/mutate.sh -- <src file>...    these files
#
# For each src project with such files it runs Stryker.NET (pinned in dotnet-tools.json) against the unit tests: the
# tool changes the code one small way at a time (a >= to >, a statement removed) and runs the tests that cover the
# line. It prints the mutants no test noticed. A survivor is one of two things: a behaviour no test asserts (write the
# test; the mutant says which behaviour), or a change that makes no difference to anyone (leave it, and do not write a
# test to its shape). Mutations of strings are left out: they are messages.
# Minutes per project (every test runs once alone, to learn what it covers), so this is not part of scripts/check.sh:
# run it after changing logic that is easy to get subtly wrong. Exit 0 whatever survives; 2 when the tool could not run.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"; cd "$root"
tests="tests/StrataRec.UnitTests"
cannot() { echo "mutate: $1" >&2; exit 2; }

files=()
if [[ "${1:-}" == "--" ]]; then
  shift; files=("$@")
else
  base="$(git merge-base "${1:-origin/main}" HEAD 2>/dev/null)" || cannot "'${1:-origin/main}' is not a commit that shares history with HEAD."
  while IFS= read -r -d '' file; do files+=("$file"); done < <(
    git diff --name-only -z --diff-filter=AM "$base" -- 'src/*.cs'; git ls-files --others --exclude-standard -z -- 'src/*.cs')
fi
[[ ${#files[@]} -gt 0 ]] || { echo "mutate: no src file changed${base:+ since ${base:0:9}}; nothing to mutate."; exit 0; }

projects=()
for file in "${files[@]}"; do
  [[ "$file" == src/*/*.cs && -f "$file" ]] || cannot "$file is not a C# file of a project under src/."
  project="${file#src/}"; project="${project%%/*}"
  [[ " ${projects[*]} " == *" $project "* ]] || projects+=("$project")
done

# The tool is a framework-dependent app: it finds the runtime through DOTNET_ROOT where dotnet is not in a default place.
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
dotnet tool restore >/dev/null || cannot "dotnet tool restore failed (dotnet-tools.json pins dotnet-stryker)."
out="$(mktemp -d "${TMPDIR:-/tmp}/mutate.XXXXXX")"
trap 'rm -rf "$out"' EXIT
printf '{ "stryker-config": { "ignore-mutations": ["string"] } }\n' > "$out/config.json"

for project in "${projects[@]}"; do
  grep -q "$project.csproj" "$tests/StrataRec.UnitTests.csproj" || { echo "mutate: $project: the unit tests do not reference it; skipped."; continue; }
  only=()
  for file in "${files[@]}"; do
    [[ "$file" == "src/$project/"* ]] && only+=(-m "${file#"src/$project/"}")
  done
  echo "mutate: $project: $(( ${#only[@]} / 2 )) file(s); a few minutes"
  (cd "$tests" && dotnet stryker -f "$out/config.json" -p "$project.csproj" "${only[@]}" -t mtp --configuration Release \
      -r json -O "$out/$project" --skip-version-check > "$out/$project.log" 2>&1) ||
    { tail -n 25 "$out/$project.log" >&2; cannot "Stryker did not finish for $project (its log is above)."; }
  python3 - "$out/$project/reports/mutation-report.json" "$root" <<'PY'
import json, os, sys
report, root = json.load(open(sys.argv[1], encoding="utf-8")), sys.argv[2]
counts, shown = {}, []
for path, file in sorted(report["files"].items()):
    source = file["source"].split("\n")
    for mutant in file["mutants"]:
        status = mutant["status"]
        counts[status] = counts.get(status, 0) + 1
        if status in ("Survived", "NoCoverage"):
            line = mutant["location"]["start"]["line"]
            what = "no test runs this line" if status == "NoCoverage" else "survived"
            was = " ".join(source[line - 1].split())[:90]
            now = " ".join((mutant.get("replacement") or "").split())[:60]
            shown.append(f"  {os.path.relpath(path, root)}:{line}: {what}: {mutant['mutatorName']}\n      {was}\n      => {now}")
tested = sum(n for s, n in counts.items() if s in ("Killed", "Timeout", "Survived", "NoCoverage"))
print(f"mutate: {tested} mutants: {counts.get('Killed', 0) + counts.get('Timeout', 0)} noticed by a test, "
      f"{counts.get('Survived', 0)} survived, {counts.get('NoCoverage', 0)} on lines no test runs"
      + (f" ({counts['CompileError']} did not compile and are not counted)" if counts.get("CompileError") else ""))
print("\n".join(shown))
PY
done
