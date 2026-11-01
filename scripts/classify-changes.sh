#!/usr/bin/env bash
# Classifies a changed-file list into lanes consumed by job `if:` guards.
# Input: newline-separated file list on stdin (or $1 as file path).
# Output: key=value lines for $GITHUB_OUTPUT (or stdout when unset).
set -uo pipefail

if [[ -n "${1:-}" ]]; then
  files=$(cat "$1")
else
  files=$(cat)
fi

has() { local pattern="$1"; printf '%s\n' "$files" | grep -qE "$pattern" && echo true || echo false; }

code=$(has '^(src|tests|tools|scripts|\.ci)/|\.(slnx?|csproj|props|targets|razor|cs)$|^(nuget\.config|Directory\.)')
docs=$(has '^docs/|\.md$|^\.specs/')
workflow=$(has '^\.github/')
docker=$(has '(^|/)Dockerfile|docker-compose.*\.(ya?ml)|^\.dockerignore')
tests_changed=$(has '^tests/.*\.(cs|csproj)$')

# testsOnly: every changed path lives under tests/ (nothing else touched)
if [[ -n "$files" ]] && ! printf '%s\n' "$files" | grep -vqE '^tests/'; then
  tests_only=true
else
  tests_only=false
fi

out="${GITHUB_OUTPUT:-/dev/stdout}"
{
  echo "code=$code"
  echo "docs=$docs"
  echo "workflow=$workflow"
  echo "docker=$docker"
  echo "tests_changed=$tests_changed"
  echo "testsOnly=$tests_only"
} >> "$out"
