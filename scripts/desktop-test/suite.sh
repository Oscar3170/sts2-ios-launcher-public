#!/usr/bin/env bash
# Run scenarios one after another with run.sh and write suite.tsv. Build first with build.sh. See docs/TESTING.md.
#
#   scripts/desktop-test/suite.sh [--window=WxH] [scenario...]
#
# Without --window the main portrait target runs: 540x1170 with the iPhone's top inset (STS2MOBILE_TEST_INSETS=0.069,0
# unless set). Without scenarios the full list below runs. Review suite.tsv and each scenario's RESULTS.json; a
# no-results row is a startup or fixture failure, not a pass.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/scripts/desktop-test/platform.sh"

WINDOW=540x1170
if [[ ${1:-} == --window=* ]]; then
  WINDOW="${1#--window=}"
  shift
fi
if [[ $WINDOW == 540x1170 ]]; then export STS2MOBILE_TEST_INSETS="${STS2MOBILE_TEST_INSETS:-0.069,0}"; fi

scenarios=(
  menus menu-hover menu-utilities settings menu-audit settings-deep pause-menu compendium capstone-over-hand
  potion-reward orbs-ovicopter enemy-order decimillipede hand-rows hand-selection regent-stars combat-piles
  status-scaling header-inventory header-indicators potions orbs touch-combat select-confirm reward-guard stage-osty
  portrait-leave shop shop-courier weirdshop shop-states blitz rewards rewards-variants screens treasure rest
  rest-options rest-tour deck-view upgrade enchant transform map map-history event event-popup events-tight ancient
  ancient-pael ancient-dialogue ancient-art-tour game-over game-over-win game-over-boss game-over-room
  game-over-boss-win stage-backdrops stage-encounters tour-overgrowth tour-underdocks tour-hive tour-glory
)
if (($# > 0)); then scenarios=("$@"); fi

PROJECT_ROOT="${STS2MOBILE_PROJECT_ROOT:-$ROOT}"
OUTPUT="$PROJECT_ROOT/.work/test-runs/suite-$WINDOW-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUTPUT"
printf 'scenario\tstatus\tpassed_steps\tsteps\tfailed_steps\n' >"$OUTPUT/suite.tsv"
echo "Suite: window=$WINDOW insets=${STS2MOBILE_TEST_INSETS:-none} output=$OUTPUT"

failed=0
for scenario in "${scenarios[@]}"; do
  echo "Running $scenario"
  run="$OUTPUT/$scenario"
  if STS2MOBILE_TEST_OUTPUT="$run" "$ROOT/scripts/desktop-test/run.sh" "$scenario" --touch --window="$WINDOW" \
    >"$OUTPUT/$scenario.runner.log" 2>&1; then
    status=passed
  else
    failed=1
    status=failed
    [[ -f $run/RESULTS.json ]] || status=no-results
  fi
  if [[ -f $run/RESULTS.json ]] && command -v jq >/dev/null 2>&1; then
    jq -r --arg s "$scenario" --arg st "$status" \
      '[$s, $st, ([.Steps[] | select(.Status != "failed")] | length), (.Steps | length),
        ([.Steps[] | select(.Status == "failed") | .Name] | join(","))] | @tsv' "$run/RESULTS.json" >>"$OUTPUT/suite.tsv"
  else
    printf '%s\t%s\t\t\t\n' "$scenario" "$status" >>"$OUTPUT/suite.tsv"
  fi
  # A window the game could not reach invalidates every later scenario at this size.
  if grep -Fq 'Requested test window' "$run/RESULTS.json" 2>/dev/null; then
    echo "Window size mismatch; see $run/RESULTS.json" >&2
    exit 1
  fi
done
echo "Suite report: $OUTPUT/suite.tsv"
exit "$failed"
