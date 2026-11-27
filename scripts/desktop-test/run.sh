#!/usr/bin/env bash
# Launch the installed game with the desktop test host on one scenario and wait for its RESULTS.json. Build and install
# first with build.sh. See docs/TESTING.md.
#
#   scripts/desktop-test/run.sh <scenario> [--touch] [--window=WxH] [extra game arguments]
#
# The main portrait target is --window=540x1170 with STS2MOBILE_TEST_INSETS=0.069,0 (the iPhone's top inset); 720x1280 is
# 9:16 and 1280x720 landscape. The game window is resized; the display never is.
#
# Environment:
#   STS2MOBILE_TEST_INSETS=top,side   safe-area insets as fractions of the window (default none)
#   STS2MOBILE_TEST_TIMEOUT=seconds   how long to wait for RESULTS.json (default per scenario)
#   STS2MOBILE_TEST_OUTPUT=dir        the run's folder (default .work/test-runs/<scenario>-<timestamp>)
#   STS2MOBILE_PROJECT_ROOT=dir       the checkout results belong to (default this one)
#   STS2MOBILE_TEST_VIA_STEAM=1       launch through `steam -applaunch` instead of directly; Steam does not forward this
#                                     shell's environment, so insets and proofs need the direct launch
#   STS2MOBILE_BUILD_STARTED_MS / STS2MOBILE_BUILD_FINISHED_MS   a wrapper's build phase, for host-timings.json
#   STS2_PORTRAIT_PROOF, STS2_PORTRAIT_NOFIT, STS2_STAGE_ENCOUNTERS   read by the game process
#   STS2_DATA_DIR, STS2_GAME_EXE      see platform.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/scripts/desktop-test/platform.sh"

START_MS=$(dt_now_ms)
START_ISO=$(dt_now_iso)
# RESULTS.json appears within a few hundred ms of the write; five wake-ups a second is still idle.
POLL_INTERVAL=0.2
POLL_INTERVAL_MS=200

SCENARIO="${1:?usage: run.sh <scenario> [--touch] [--window=WxH] [game arguments]}"
shift
TOUCH=0
WINDOW=""
EXTRA=()
for argument in "$@"; do
  case "$argument" in
    --touch) TOUCH=1 ;;
    --window=*) WINDOW="${argument#--window=}" ;;
    *) EXTRA+=("$argument") ;;
  esac
done
if [[ -n $WINDOW && ! $WINDOW =~ ^[0-9]+x[0-9]+$ ]]; then
  echo "Expected --window=WIDTHxHEIGHT, got $WINDOW" >&2
  exit 1
fi

DATA_DIR="$(dt_find_data_dir)"
GAME_EXE="$(dt_find_game_exe "$DATA_DIR")"
MODS_DIR="$(dt_mods_dir "$GAME_EXE")"

if dt_game_running "$GAME_EXE"; then
  echo "Slay the Spire 2 is already running; close it first (a user's session is never touched)." >&2
  exit 1
fi
if [[ ! -f $MODS_DIR/STS2Mobile.DesktopHost/STS2MobileDesktopHost.dll ]]; then
  echo "The test host is not installed in $MODS_DIR/STS2Mobile.DesktopHost. Run scripts/desktop-test/build.sh." >&2
  exit 1
fi
retired="$(dt_retired_mods "$MODS_DIR")"
if [[ -n $retired ]]; then
  echo "The retired TouchSpireUI mods would load with this host and change what the checks measure. Remove:" >&2
  printf '  %s\n' "$retired" >&2
  exit 1
fi
if [[ ${STS2MOBILE_TEST_VIA_STEAM:-0} == 1 ]] && \
  [[ -n ${STS2_PORTRAIT_PROOF:-}${STS2_PORTRAIT_NOFIT:-}${STS2MOBILE_TEST_INSETS:-}${STS2_STAGE_ENCOUNTERS:-} ]]; then
  echo "Steam does not forward this shell's environment: proofs, insets and stage subsets need the direct launch." >&2
  exit 1
fi

PROJECT_ROOT="${STS2MOBILE_PROJECT_ROOT:-$ROOT}"
export STS2MOBILE_PROJECT_ROOT="$PROJECT_ROOT"
OUTPUT="${STS2MOBILE_TEST_OUTPUT:-$PROJECT_ROOT/.work/test-runs/${SCENARIO}-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"

DEFAULT_TIMEOUT=240
[[ $SCENARIO == tour-* ]] && DEFAULT_TIMEOUT=1200
# Every rest option, the synthetic eleven-option row and their describe/confirm taps: about 205 s at 9:16 and over 240 s
# at 540x1170.
[[ $SCENARIO == rest-options ]] && DEFAULT_TIMEOUT=420
# Every encounter of every act staged and measured: about 430 s at 540x1170.
[[ $SCENARIO == stage-encounters ]] && DEFAULT_TIMEOUT=720
# One Fabricator fight through up to six summon turns, two deaths and a re-summon: about 150 s at 540x1170.
[[ $SCENARIO == stage-fabricator ]] && DEFAULT_TIMEOUT=420
TEST_TIMEOUT="${STS2MOBILE_TEST_TIMEOUT:-$DEFAULT_TIMEOUT}"
# The host finishes a little before this (it reads the same variable).
export STS2MOBILE_TEST_TIMEOUT="$TEST_TIMEOUT"
DEADLINE_MS=$((START_MS + TEST_TIMEOUT * 1000))

# Wall clock of this launch next to RESULTS.json, so a suite can attribute build, launch, results and exit.
LAUNCH_MS=""
RESULTS_MS=""
EXIT_MS=""
write_timings() {
  local total build_ms=null
  total=$(( $(dt_now_ms) - START_MS ))
  if [[ ${STS2MOBILE_BUILD_STARTED_MS:-} =~ ^[0-9]+$ && ${STS2MOBILE_BUILD_FINISHED_MS:-} =~ ^[0-9]+$ ]]; then
    build_ms=$((STS2MOBILE_BUILD_FINISHED_MS - STS2MOBILE_BUILD_STARTED_MS))
  fi
  {
    printf '{\n'
    printf '  "Scenario": "%s",\n' "$SCENARIO"
    printf '  "Platform": "%s",\n' "$DT_PLATFORM"
    printf '  "Window": "%s",\n' "${WINDOW:-default}"
    printf '  "StartedAt": "%s",\n' "$START_ISO"
    printf '  "PollIntervalMs": %s,\n' "$POLL_INTERVAL_MS"
    printf '  "LaunchMs": %s,\n' "${LAUNCH_MS:-null}"
    printf '  "ResultsMs": %s,\n' "${RESULTS_MS:-null}"
    printf '  "ExitedMs": %s,\n' "${EXIT_MS:-null}"
    printf '  "BuildMs": %s,\n' "$build_ms"
    printf '  "TotalMs": %s\n' "$total"
    printf '}\n'
  } >"$OUTPUT/host-timings.json" 2>/dev/null || true
}
trap write_timings EXIT

# A native crash leaves no Godot backtrace; let the .NET runtime write a crash report with every thread's managed and
# native frames next to the run's results.
export DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1
export DOTNET_DbgMiniDumpName="$OUTPUT/dotnet-crash.%p.dmp"

ARGS=(--sts2mobile-test="$SCENARIO" --sts2mobile-test-skip-logo --sts2mobile-test-output="$OUTPUT")
[[ $TOUCH == 1 ]] && ARGS+=(--sts2mobile-test-touch)
if [[ -n $WINDOW ]]; then
  # The game reads this to start windowed instead of in its saved fullscreen mode.
  export STS2_DEV_WINDOWED=1
  while IFS= read -r window_argument; do ARGS+=("$window_argument"); done < <(dt_window_args "$WINDOW")
  ARGS+=(--sts2mobile-test-window="$WINDOW")
fi
if ((${#EXTRA[@]} > 0)); then ARGS+=("${EXTRA[@]}"); fi

echo "Launching desktop test: scenario=$SCENARIO touch=$TOUCH window=${WINDOW:-default} insets=${STS2MOBILE_TEST_INSETS:-none}"
echo "Output: $OUTPUT"
LAUNCH_MS=$(( $(dt_now_ms) - START_MS ))
GAME_PID=""
if [[ ${STS2MOBILE_TEST_VIA_STEAM:-0} == 1 ]]; then
  steam -applaunch 2868840 "${ARGS[@]}" >"$OUTPUT/steam-launch.out" 2>&1
else
  # Direct launch avoids the Steam overlay's Vulkan present deadlock on repeated fixture launches. Steam must still be
  # running for the game's Steam API.
  if [[ $DT_PLATFORM == linux ]] && command -v systemctl >/dev/null 2>&1; then
    # Agent or non-GUI shells may lack the desktop display environment; Godot then falls back to Wayland/libdecor,
    # which can hang in its first roundtrip. Inherit only missing display keys from the user session.
    while IFS='=' read -r key value; do
      case "$key" in
        DISPLAY|WAYLAND_DISPLAY|XAUTHORITY) [[ -n ${!key:-} ]] || export "$key=$value" ;;
      esac
    done < <(systemctl --user show-environment 2>/dev/null || true)
  fi
  # The caller's environment (insets, proofs) reaches the game; only LD_PRELOAD is dropped.
  (cd "$(dirname "$GAME_EXE")" && env -u LD_PRELOAD \
    DISABLE_VK_LAYER_VALVE_steam_overlay_1=1 SteamAppId=2868840 SteamGameId=2868840 \
    "$GAME_EXE" "${ARGS[@]}") >"$OUTPUT/process.log" 2>&1 &
  GAME_PID=$!
fi

# A run that died from a signal: keep the host's core dump stacks with the run (systemd-coredump may still be writing).
save_crash_backtrace() {
  command -v coredumpctl >/dev/null 2>&1 || { echo "coredumpctl is not available" >"$OUTPUT/backtrace.txt"; return; }
  local since="@$((START_MS / 1000))" tries
  for tries in 1 2 3 4 5 6 7 8 9 10; do
    coredumpctl --no-pager --since="$since" -1 info "$GAME_EXE" >"$OUTPUT/coredump-info.txt" 2>&1 \
      && grep -q 'Storage: .*present' "$OUTPUT/coredump-info.txt" && break
    sleep 2
  done
  timeout 180 coredumpctl --no-pager --since="$since" -1 debug "$GAME_EXE" \
    --debugger-arguments='-batch -ex "set pagination off" -ex "info threads" -ex "thread apply all bt 60"' \
    >"$OUTPUT/backtrace.txt" 2>&1 </dev/null || true
  echo "Crash stacks: $OUTPUT/coredump-info.txt, $OUTPUT/backtrace.txt"
}

RESULT="$OUTPUT/RESULTS.json"
while [[ ! -f $RESULT ]]; do
  if [[ -n $GAME_PID ]] && ! kill -0 "$GAME_PID" 2>/dev/null; then
    echo "The game exited before writing RESULTS.json (a startup failure, not a test result). Recent process log:"
    tail -80 "$OUTPUT/process.log" 2>/dev/null || true
    status=0
    wait "$GAME_PID" || status=$?
    if ((status > 128)); then save_crash_backtrace; fi
    exit 1
  fi
  (( $(dt_now_ms) >= DEADLINE_MS )) && break
  sleep "$POLL_INTERVAL"
done
[[ -f $RESULT ]] && RESULTS_MS=$(( $(dt_now_ms) - START_MS ))

if [[ ! -f $RESULT ]]; then
  echo "No RESULTS.json after ${TEST_TIMEOUT}s. Recent game log:"
  tail -80 "$(dt_game_log)" 2>/dev/null || true
  exit 1
fi

# RESULTS.json is written before the game finishes shutting down. Never race the next scenario against this game's exit
# (or mistake it for a user's session).
if [[ -n $GAME_PID ]]; then
  for _ in $(seq 1 150); do
    kill -0 "$GAME_PID" 2>/dev/null || break
    sleep 0.1
  done
  if kill -0 "$GAME_PID" 2>/dev/null; then
    echo "Results exist, but the game has not exited after 15 s; refusing to launch another."
    exit 1
  fi
  wait "$GAME_PID" || true
  EXIT_MS=$(( $(dt_now_ms) - START_MS ))
fi

echo
echo "=== Results: $RESULT ==="
if command -v jq >/dev/null 2>&1; then
  jq '{Scenario, PointerMode, Passed, Error, Steps} + (if has("Host") then {Host, Proof} else {} end)' "$RESULT"
else
  cat "$RESULT"
fi
echo
echo "RESULTS $OUTPUT"
echo "Timings: $OUTPUT/host-timings.json (launch=${LAUNCH_MS:-?}ms results=${RESULTS_MS:-?}ms exit=${EXIT_MS:-?}ms)"
if command -v jq >/dev/null 2>&1; then
  jq -e '.Passed == true' "$RESULT" >/dev/null
fi
