#!/usr/bin/env bash
# Build the desktop test host (tests/DesktopHost) against the installed game and install it as a mod. The game must be
# stopped: .NET memory-maps loaded assemblies, and overwriting one under a running game corrupts its metadata and crashes
# it much later. See docs/TESTING.md.
#
#   scripts/desktop-test/build.sh [extra dotnet build arguments]
#
# Environment: STS2_DATA_DIR, STS2_GAME_EXE (platform.sh), DOTNET (the dotnet to run),
# STS2MOBILE_SOURCE_REVISION / STS2MOBILE_SOURCE_DIRTY / STS2MOBILE_SOURCE_PATH to stamp a build made from a copy of the
# checkout (for example a `git archive` snapshot, which has no .git).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/scripts/desktop-test/platform.sh"

DATA_DIR="$(dt_find_data_dir)"
GAME_EXE="$(dt_find_game_exe "$DATA_DIR")"
MODS_DIR="$(dt_mods_dir "$GAME_EXE")"
HOST_MOD="$MODS_DIR/STS2Mobile.DesktopHost"
PROJECT="$ROOT/tests/DesktopHost"
HOST_OUT="$PROJECT/bin/Release/net9.0"
OUTPUTS=("$HOST_OUT/STS2MobileDesktopHost.dll" "$HOST_OUT/STS2Mobile.DesktopHost.json")

require_game_stopped() {
  if dt_game_running "$GAME_EXE"; then
    echo "Close Slay the Spire 2 before building or installing the test host." >&2
    exit 1
  fi
}
require_game_stopped

# Build once per input: a stamp of everything whose content changes the built assembly. Content hashes, not mtimes, so a
# copy of the checkout still matches; the game's assemblies are compared by size and mtime (too big to hash per build).
STAMP_DIR="$ROOT/.work/build-stamps"
STAMP_FILE="$STAMP_DIR/desktop-host.stamp"
mkdir -p "$STAMP_DIR"
inputs_hash() {
  {
    printf 'args=%s\n' "$*"
    printf 'game=%s %s %s\n' \
      "$(dt_file_signature "$DATA_DIR/sts2.dll")" \
      "$(dt_file_signature "$DATA_DIR/GodotSharp.dll")" \
      "$(dt_file_signature "$DATA_DIR/0Harmony.dll")"
    find "$PROJECT" "$ROOT/src/STS2Mobile.GameHost" "$ROOT/src/STS2Mobile.Platform" "$ROOT/scripts/desktop-test" \
      "$ROOT/Directory.Build.props" "$ROOT/tests/Directory.Build.props" "$ROOT/.editorconfig" "$ROOT/global.json" \
      -type f -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/.godot/*' -print0 2>/dev/null \
      | sort -z | xargs -0 cat | dt_sha256 | cut -d' ' -f1
  } | dt_sha256 | cut -d' ' -f1
}
INPUTS_HASH="$(inputs_hash "$@")"

outputs_present() {
  local file
  for file in "${OUTPUTS[@]}"; do [[ -f $file ]] || return 1; done
}

if [[ -f $STAMP_FILE && "$(cat "$STAMP_FILE")" == "$INPUTS_HASH" ]] && outputs_present; then
  echo "Build inputs unchanged since the last build ($INPUTS_HASH); skipping dotnet build."
else
  echo "Building tests/DesktopHost against $DATA_DIR"
  "$(dt_dotnet "$ROOT")" build "$PROJECT/DesktopHost.csproj" -c Release -p:Sts2DataDir="$DATA_DIR" "$@"
  temporary="$(mktemp "$STAMP_FILE.XXXXXX")"
  printf '%s\n' "$INPUTS_HASH" >"$temporary"
  mv -f -- "$temporary" "$STAMP_FILE"
fi

# Which code this is: RESULTS.json carries it. Only tracked files under the compiled trees count, so untracked notes in the
# checkout do not mark a build dirty.
if [[ -n ${STS2MOBILE_SOURCE_REVISION:-} ]]; then
  REVISION="$STS2MOBILE_SOURCE_REVISION"
  DIRTY="${STS2MOBILE_SOURCE_DIRTY:-false}"
  SOURCE_PATH="${STS2MOBILE_SOURCE_PATH:-$ROOT}"
else
  REVISION="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
  DIRTY=false
  git -C "$ROOT" diff --quiet HEAD -- src tests/DesktopHost scripts/desktop-test 2>/dev/null || DIRTY=true
  SOURCE_PATH="$ROOT"
fi

json_escape() {
  local value="${1//\\/\\\\}"
  printf '%s' "${value//\"/\\\"}"
}

# Atomic replacement: a launch racing the check above never maps a half-written file.
installed_count=0
install_file() {
  local source="$1" destination="$HOST_MOD/$(basename "$1")" temporary
  if [[ -f $destination ]] && cmp -s -- "$source" "$destination"; then return 0; fi
  temporary="$(mktemp "$HOST_MOD/.install.XXXXXX")"
  cp -- "$source" "$temporary"
  chmod 644 "$temporary"
  mv -f -- "$temporary" "$destination"
  installed_count=$((installed_count + 1))
}

require_game_stopped
mkdir -p "$HOST_MOD"
for output in "${OUTPUTS[@]}"; do install_file "$output"; done
staging="$(mktemp -d "${TMPDIR:-/tmp}/sts2mobile-build.XXXXXX")"
printf '{"revision":"%s","dirty":%s,"path":"%s"}\n' "$REVISION" "$DIRTY" "$(json_escape "$SOURCE_PATH")" \
  >"$staging/launcher-source.json"
install_file "$staging/launcher-source.json"
rm -rf -- "$staging"

echo
if [[ $installed_count -eq 0 ]]; then
  echo "Installed files already match the build; nothing was copied."
else
  echo "Installed $installed_count file(s) to $HOST_MOD (revision $REVISION dirty=$DIRTY)."
fi
retired="$(dt_retired_mods "$MODS_DIR")"
if [[ -n $retired ]]; then
  echo "The retired TouchSpireUI mods are still installed and would load during a test; run.sh refuses to start until"
  echo "they are removed:"
  printf '  %s\n' "$retired"
fi
echo "Run a scenario with: $ROOT/scripts/desktop-test/run.sh menus --touch --window=540x1170"
