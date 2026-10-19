#!/usr/bin/env bash
# Turns the TRX results into a workflow annotation (readable from the API without the raw log) and fails the job when
# nothing ran, so a test project that silently discovers zero tests cannot pass as green
set -euo pipefail
dir="${1:-TestResults}"
total=0; passed=0; failed=0; skipped=0
while IFS= read -r f; do
  t=$(grep -o 'total="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  p=$(grep -o 'passed="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  x=$(grep -o 'failed="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  s=$(grep -o 'notExecuted="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  total=$((total + t)); passed=$((passed + p)); failed=$((failed + x)); skipped=$((skipped + s))
  grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' "$f" | grep -o 'testName="[^"]*"' | while read -r n; do
    echo "::error title=failed test::${n#testName=}"
  done || true
done < <(find "$dir" -name '*.trx')
echo "::notice title=tests::total=$total passed=$passed failed=$failed skipped=$skipped"
if [ "$total" -eq 0 ]; then echo "::error title=tests::no tests ran"; exit 1; fi
if [ "$failed" -gt 0 ]; then exit 1; fi
