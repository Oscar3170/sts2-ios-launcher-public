# Everything the desktop test scripts need to know about the operating system and the installed game. Sourced by build.sh,
# run.sh and suite.sh; defines functions and variables only.
#
# Overrides: STS2_DATA_DIR (the game's data_sts2_* folder holding sts2.dll), STS2_GAME_EXE (the game executable).
# Linux is the tested platform. macOS follows the same layout rules but has not been run yet (docs/TESTING.md).

case "$(uname -s)" in
  Linux) DT_PLATFORM=linux ;;
  Darwin) DT_PLATFORM=macos ;;
  *) echo "Unsupported platform: $(uname -s) (Linux and macOS only)." >&2; exit 1 ;;
esac

# Milliseconds since the epoch. BSD date has no %N.
dt_now_ms() {
  if [[ $DT_PLATFORM == linux ]]; then date +%s%3N; else perl -MTime::HiRes=time -e 'printf "%d\n", time * 1000'; fi
}

dt_now_iso() { date +%Y-%m-%dT%H:%M:%S%z; }

# Size and modification time, enough to notice a changed game assembly without hashing it.
dt_file_signature() {
  if [[ ! -f $1 ]]; then printf 'missing'; return; fi
  if [[ $DT_PLATFORM == linux ]]; then stat -c '%s:%Y' -- "$1"; else stat -f '%z:%m' -- "$1"; fi
}

dt_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

# Steam library roots that may hold the game.
dt_library_candidates() {
  if [[ $DT_PLATFORM == linux ]]; then
    printf '%s\n' \
      "$HOME/.steam/steam/steamapps/common/Slay the Spire 2" \
      "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2" \
      "$HOME/.var/app/com.valvesoftware.Steam/data/Steam/steamapps/common/Slay the Spire 2" \
      "/mnt/steam/steamapps/common/Slay the Spire 2"
    local root
    for root in /mnt/*/steamapps/common/"Slay the Spire 2" /mnt/*/SteamLibrary/steamapps/common/"Slay the Spire 2"; do
      [[ -d $root ]] && printf '%s\n' "$root"
    done
  else
    printf '%s\n' \
      "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2" \
      "/Applications/Slay the Spire 2"
  fi
  return 0
}

# The data folder: data_sts2_<platform> next to the Linux executable; somewhere inside the .app bundle on macOS, so it is
# searched for rather than named.
dt_find_data_dir() {
  if [[ -n ${STS2_DATA_DIR:-} ]]; then
    [[ -f $STS2_DATA_DIR/sts2.dll ]] || { echo "STS2_DATA_DIR has no sts2.dll: $STS2_DATA_DIR" >&2; return 1; }
    printf '%s\n' "$STS2_DATA_DIR"
    return 0
  fi
  local root found
  while IFS= read -r root; do
    [[ -d $root ]] || continue
    found="$(find "$root" -maxdepth 5 -type f -name sts2.dll -path '*data_sts2_*' 2>/dev/null | head -1)"
    if [[ -n $found ]]; then
      dirname "$found"
      return 0
    fi
  done < <(dt_library_candidates)
  echo "Could not find the game's sts2.dll. Set STS2_DATA_DIR to its data_sts2_* folder." >&2
  return 1
}

# The game executable. Linux: SlayTheSpire2 in the install folder. macOS: the bundle's Contents/MacOS executable.
dt_find_game_exe() {
  if [[ -n ${STS2_GAME_EXE:-} ]]; then
    [[ -x $STS2_GAME_EXE ]] || { echo "STS2_GAME_EXE is not executable: $STS2_GAME_EXE" >&2; return 1; }
    printf '%s\n' "$STS2_GAME_EXE"
    return 0
  fi
  local data_dir="$1" candidate
  if [[ $DT_PLATFORM == linux ]]; then
    candidate="$(dirname "$data_dir")/SlayTheSpire2"
    [[ -x $candidate ]] && { printf '%s\n' "$candidate"; return 0; }
  else
    local contents="$data_dir"
    while [[ $contents != / && $(basename "$contents") != Contents ]]; do contents="$(dirname "$contents")"; done
    if [[ -d $contents/MacOS ]]; then
      candidate="$(find "$contents/MacOS" -maxdepth 1 -type f -perm -u+x | head -1)"
      [[ -n $candidate ]] && { printf '%s\n' "$candidate"; return 0; }
    fi
  fi
  echo "Could not find the game executable next to $data_dir. Set STS2_GAME_EXE." >&2
  return 1
}

# The game loads mods from a "mods" folder next to its executable (ModManager: OS.GetExecutablePath()).
dt_mods_dir() { printf '%s/mods\n' "$(dirname "$1")"; }

# The game's own log, for a run that never wrote RESULTS.json.
dt_game_log() {
  if [[ $DT_PLATFORM == linux ]]; then
    printf '%s\n' "$HOME/.local/share/SlayTheSpire2/logs/godot.log"
  else
    printf '%s\n' "$HOME/Library/Application Support/SlayTheSpire2/logs/godot.log"
  fi
}

# Whether any copy of the game is running: a user's session counts too, and is never touched.
dt_game_running() {
  pgrep -x "$(basename "$1")" >/dev/null 2>&1
}

# The game's window arguments for a requested WxH. Linux forces X11, so the window can be any size (a Wayland session
# would let the compositor decide); macOS keeps its native driver and may clamp a window taller than the screen, which
# the host then reports as a failed run.
dt_window_args() {
  local size="$1"
  if [[ $DT_PLATFORM == linux ]]; then
    printf '%s\n' --windowed --display-driver x11 --resolution "$size"
  else
    printf '%s\n' --windowed --resolution "$size"
  fi
}

# The retired TouchSpireUI mods load as active alongside this host (they were only inert under its old flag).
dt_retired_mods() {
  local mods="$1" name
  for name in TouchSpireUI TouchSpire.TestHarness; do
    [[ -d $mods/$name ]] && printf '%s\n' "$mods/$name"
  done
  return 0
}

# The pinned .NET SDK (global.json): tools/ios-dotnet in the checkout, else whatever dotnet is on PATH (which then has to
# be that same version).
dt_dotnet() {
  local root="$1"
  if [[ -n ${DOTNET:-} ]]; then printf '%s\n' "$DOTNET"
  elif [[ -x $root/tools/ios-dotnet/dotnet ]]; then printf '%s\n' "$root/tools/ios-dotnet/dotnet"
  else printf '%s\n' dotnet
  fi
}
