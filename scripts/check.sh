#!/usr/bin/env bash
# Everything a change passes before it is handed over, cheapest first: the static gate (under a second), the build
# (warnings are errors), every test, the slow-test check. CI runs the same steps (.github/workflows/ci.yml).
#   scripts/check.sh [--base <commit>]     the base of the rule-file check and the size note; origin/main by default
set -euo pipefail
cd "$(dirname "$0")/.."

python3 scripts/gate.py selftest
gate="$(python3 scripts/gate.py "$@")" || { printf '%s\n' "$gate"; exit 1; }
printf '%s\n' "$gate"
dotnet build StrataRec.sln -c Release --nologo -v quiet
# The reports of this run only: one left by a project since renamed or removed would be judged as if it had just run.
find TestResults -name '*.trx' -delete 2>/dev/null || true
dotnet test StrataRec.sln -c Release --no-build -p:TestReports=true --results-directory TestResults
python3 scripts/gate.py slow TestResults
# Again at the end, where it is read: what the gate noted about the change (its size, baselines that can go down).
printf '%s\n' "$gate" | grep -E '^(size|sleeps|commits):' || true
echo "check: ok"
