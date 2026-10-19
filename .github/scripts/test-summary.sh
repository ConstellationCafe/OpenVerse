#!/usr/bin/env bash
# Turns the TRX results into workflow annotations (readable from the API without the raw log). Fails when nothing ran,
# or when a test fails that is not listed in the known-failures file (second argument, optional)
set -euo pipefail
dir="${1:-TestResults}"
known_file="${2:-}"
total=0; passed=0; failed=0; skipped=0; unexpected=0
known=""
[ -n "$known_file" ] && [ -f "$known_file" ] && known=$(grep -v '^#' "$known_file" | sed '/^\s*$/d')
while IFS= read -r f; do
  t=$(grep -o 'total="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  p=$(grep -o 'passed="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  x=$(grep -o 'failed="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  s=$(grep -o 'notExecuted="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*' || echo 0)
  total=$((total + t)); passed=$((passed + p)); failed=$((failed + x)); skipped=$((skipped + s))
  while read -r n; do
    [ -z "$n" ] && continue
    if printf '%s\n' "$known" | grep -qxF "$n"; then
      echo "::warning title=known failure::$n"
    else
      echo "::error title=failed test::$n"
      unexpected=$((unexpected + 1))
    fi
  done < <(grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' "$f" | grep -o 'testName="[^"]*"' | sed 's/^testName="//; s/"$//' || true)
done < <(find "$dir" -name '*.trx')
echo "::notice title=tests::total=$total passed=$passed failed=$failed (unexpected=$unexpected) skipped=$skipped"
if [ "$total" -eq 0 ]; then echo "::error title=tests::no tests ran"; exit 1; fi
if [ "$unexpected" -gt 0 ]; then exit 1; fi
