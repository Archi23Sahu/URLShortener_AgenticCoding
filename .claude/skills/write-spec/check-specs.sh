#!/usr/bin/env bash
# Verifies spec <-> test traceability (rubric A4 in docs/PLAN.md).
#   - every `Class.Method` cited in docs/specs/*.md exists in tests/
#   - prints AC and GAP counts per spec
#   - with --unmapped, lists [Fact]/[Theory] tests no spec cites
# Usage (from repo root, Git Bash):  bash .claude/skills/write-spec/check-specs.sh [--unmapped]
# Exit code 1 if any cited test is missing.

set -u
cd "$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

specs=(docs/specs/[0-9][0-9][0-9]-*.md)
missing=0

echo "== AC / GAP counts =="
for f in "${specs[@]}"; do
  acs=$(grep -cE '^\| [A-Z]{3}-AC-[0-9]+' "$f")
  gaps=$(grep -E '^\| [A-Z]{3}-AC-[0-9]+' "$f" | grep -c 'GAP')
  printf '%-40s ACs=%-3s GAP=%s\n' "$(basename "$f")" "$acs" "$gaps"
done

echo "== Cited tests =="
cited=$(grep -ohE '`[A-Z][A-Za-z0-9]+Tests\.[A-Za-z0-9_]+`' "${specs[@]}" | tr -d '`' | sort -u)
count=0
while read -r ref; do
  [ -z "$ref" ] && continue
  count=$((count + 1))
  cls="${ref%%.*}"; method="${ref#*.}"
  file=$(grep -rlE "class $cls\b" tests --include=*.cs | grep -v '/obj/' | head -1)
  if [ -z "$file" ] || ! grep -qE "\b$method\(" "$file"; then
    echo "MISSING: $ref"
    missing=$((missing + 1))
  fi
done <<< "$cited"
echo "checked=$count missing=$missing"

if [ "${1:-}" = "--unmapped" ]; then
  echo "== Tests not cited by any spec =="
  grep -rhE -A4 '\[(Fact|Theory)' tests --include=*.cs \
    | grep -oE '(Task|void) [A-Za-z0-9_]+\(' | sed -E 's/(Task|void) //; s/\(//' | sort -u \
    | while read -r m; do grep -q "\.$m\`" "${specs[@]}" || echo "UNMAPPED: $m"; done
fi

[ "$missing" -eq 0 ]
