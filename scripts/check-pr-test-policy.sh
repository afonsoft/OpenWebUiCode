#!/usr/bin/env bash
# PR test policy (padrão OmniRoute/OpenHandsNET, adaptado ao fork):
#   1. C# de produção mudou (src/**/*.cs) -> tests/ precisa mudar junto. BLOCKING.
#   2. Mudança só em .razor -> advisory (features visuais podem não ter teste).
#   3. Test-masking guard (advisory): arquivos de teste alterados não devem
#      remover linhas [Test]/[TestCase]/Assert. no saldo.
# Usage: check-pr-test-policy.sh <base-sha> <head-sha>
set -uo pipefail

BASE="${1:?base sha required}"
HEAD="${2:?head sha required}"

git diff --name-only "$BASE" "$HEAD" > /tmp/pr-files.txt

prod=$(grep -cE '^src/.*\.cs$' /tmp/pr-files.txt || true)
razor=$(grep -cE '^src/.*\.razor$' /tmp/pr-files.txt || true)
tests=$(grep -cE '^tests/.*\.cs$' /tmp/pr-files.txt || true)

{
  echo "## PR test policy"
  echo ""
  echo "- Production .cs files changed: $prod"
  echo "- Production .razor files changed: $razor"
  echo "- Test .cs files changed: $tests"
  echo ""
} >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"

if [ "$prod" -gt 0 ] && [ "$tests" -eq 0 ]; then
  {
    echo "**FAIL**: production code changed without any test change."
    echo ""
    echo '```'
    grep -E '^src/.*\.cs$' /tmp/pr-files.txt | head -20
    echo '```'
  } >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"
  echo "::error::PR changes $prod production .cs file(s) but no file under tests/."
  exit 1
fi

if [ "$prod" -eq 0 ] && [ "$razor" -gt 0 ] && [ "$tests" -eq 0 ]; then
  echo "::notice::PR changes $razor .razor file(s) without tests — ok for visual-only changes, consider a Client.Tests case if logic moved."
fi

# Test-masking guard (advisory — warning, not a gate yet)
mask=0
while IFS= read -r f; do
  [ -f "$f" ] || continue
  removed=$(git diff "$BASE" "$HEAD" -- "$f" | grep -cE '^-.*(\[Test|\[TestCase|\[Theory|Assert\.|\.Should[A-Z])' || true)
  added=$(git diff "$BASE" "$HEAD" -- "$f" | grep -cE '^\+.*(\[Test|\[TestCase|\[Theory|Assert\.|\.Should[A-Z])' || true)
  if [ "$removed" -gt "$((added + 2))" ]; then
    echo "::warning file=$f::possible test-masking — removed $removed assertion/test lines, added $added"
    mask=$((mask + 1))
  fi
done < <(grep -E '^tests/.*\.cs$' /tmp/pr-files.txt || true)

if [ "$mask" -gt 0 ]; then
  echo "::notice::$mask changed test file(s) net-removed assertions — review for test-masking."
fi

echo "PR test policy OK ($prod prod .cs, $razor .razor, $tests test files)"
