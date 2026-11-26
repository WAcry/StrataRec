#!/usr/bin/env bash
# Everything a change passes before it is handed over, cheapest first: the static gate (under a second), the build
# (warnings are errors), every test, the slow-test check. CI runs the same steps (.github/workflows/ci.yml).
#   scripts/check.sh [--base <commit>]     the base of the rule-file check and the size note; origin/main by default
set -euo pipefail
cd "$(dirname "$0")/.."

python3 scripts/gate.py selftest
python3 scripts/gate.py "$@"
dotnet build StrataRec.sln -c Release --nologo -v quiet
dotnet test StrataRec.sln -c Release --no-build -p:TestReports=true --results-directory TestResults
python3 scripts/gate.py slow TestResults
# Again at the end, where it is read: what the gate noted about the change (its size, baselines that can go down).
python3 scripts/gate.py "$@" | grep -E '^(size|sleeps|commits):' || true
echo "check: ok"
