# Desktop tests

The desktop test host runs the launcher's in-game portrait code (`src/STS2Mobile.GameHost` and `src/STS2Mobile.Platform`) inside the installed desktop game and drives it with scripted fixtures, real pointer input and screenshots. It is the fast way to check portrait layout and touch handling without a phone. It is a development tool: it is not the iOS build, it does not run the Cecil patches (it mirrors them with Harmony shims), and it is no evidence of device performance, safe areas or rotation.

## What it needs

- Slay the Spire 2 installed through Steam, with Steam running. Linux is the tested platform; macOS is described under [macOS](#macos).
- The .NET SDK that [global.json](../global.json) pins, in `tools/ios-dotnet` (as `./setup.sh` installs it) or on `PATH`, or named by `DOTNET`.
- `jq` for the result summaries.

The host is a game mod (`mods/STS2Mobile.DesktopHost` next to the game executable). It does nothing unless the game is started with `--sts2mobile-test=<scenario>` (a test) or `--sts2mobile-portrait` (play the launcher's portrait by hand). The retired TouchSpireUI mods must not be installed alongside it: `run.sh` refuses to start while `mods/TouchSpireUI` or `mods/TouchSpire.TestHarness` exist, and every run fails `only-own-patches` if another mod's Harmony patch is present.

## Running

Close the game first: .NET maps loaded assemblies, and replacing one under a running game crashes it later. The scripts check this and never touch a running game, including the player's own session.

```sh
scripts/desktop-test/build.sh                                  # build and install the host
STS2MOBILE_TEST_INSETS=0.069,0 \
  scripts/desktop-test/run.sh deck-view --touch --window=540x1170   # one scenario
scripts/desktop-test/suite.sh                                   # every scenario at 540x1170 with the inset
scripts/desktop-test/suite.sh --window=720x1280 deck-view hand-rows orbs
python3 scripts/desktop-test/viewer.py                          # browse the results on http://127.0.0.1:8766/
```

`build.sh` skips the build when nothing that goes into the assembly changed, and replaces installed files atomically. It writes `launcher-source.json` next to the installed assembly, so each `RESULTS.json` names the revision it ran (`+dirty` when tracked files under `src/`, `tests/DesktopHost/` or `scripts/desktop-test/` differed from it).

### Sizes

| Window | Canvas | Use |
|---|---|---|
| 540x1170, `STS2MOBILE_TEST_INSETS=0.069,0` | 1080x2340 | The main target: a tall iPhone with its top inset. Full suites run here. |
| 720x1280 | 1080x1920 | 9:16. Focused sets for shared geometry: a pass at 540x1170 says nothing about the shorter height. |
| 1280x720 | landscape | The control: landscape must keep the game's own behaviour. |

The scripts resize the game window, never the display. Review phone-sized copies of the captures (`magick shot.png -thumbnail 390x preview.png`): a bounding-box pass can still be unusably small.

### Environment

| Variable | Meaning |
|---|---|
| `STS2MOBILE_TEST_INSETS=top,side` | Safe-area insets as fractions of the window, returned to `SafeAreaCanvas` by the host's `NativeExports`. Default none. |
| `STS2MOBILE_TEST_TIMEOUT` | Seconds to wait for `RESULTS.json` (default 240; tours 1200; `rest-options` 420). The host finishes a little before it. |
| `STS2MOBILE_TEST_OUTPUT` | The run's folder. Default `.work/test-runs/<scenario>-<timestamp>`. |
| `STS2MOBILE_PROJECT_ROOT` | The checkout results belong to, when the scripts run from a copy. |
| `STS2MOBILE_TEST_VIA_STEAM=1` | Launch through `steam -applaunch`. Steam starts the game from its own environment, so insets, proofs and `STS2_STAGE_ENCOUNTERS` need the default direct launch. |
| `STS2_PORTRAIT_PROOF=<mode>` | Run one of the launcher's `PortraitProof` modes instead of a scenario: each `Portrait proof step` is captured and every proof line lands in `RESULTS.json`. A proof's `Passed` only means no failure line. |
| `STS2_PORTRAIT_NOFIT=1`, `STS2_STAGE_ENCOUNTERS` | Read by the launcher code and by `stage-encounters`. |
| `STS2_DATA_DIR`, `STS2_GAME_EXE`, `DOTNET` | Where the game and the SDK are, when the scripts cannot find them. |

## Reading results

Each run folder has `RESULTS.json` (`Passed`, `Steps` with `Name`, `Status` and `Detail`, the host configuration and shim results), the captures, `process.log` (the game's console) and `host-timings.json`. `suite.tsv` lists each scenario with its status, step counts and failed step names.

- A run without `RESULTS.json` is a startup or fixture failure, not a test result. `run.sh` says which, and keeps `.NET` crash reports and core dump stacks with the run.
- `host-config` records the revision, window, canvas, insets and proof; a run whose window differs from the request fails at the main menu (`Requested test window`), and `suite.sh` then stops, since every later scenario would be at the wrong size.
- Check passed steps' details as well as failures: several checks report measurements as information.

## Layout of the code

- `tests/DesktopHost/Host/`: the host itself. `LauncherHostEntry` (the mod entry and its gate), `LauncherHostWiring` (what `PhoneGraphics`/`GameEntry` start on the phone), `LauncherHostRunner` (scenario dispatch, `RESULTS.json`), `PhonePatchShims` (one Harmony shim per `PhonePatches` step the portrait path needs), `NativeExports` (insets from the environment), `UserDataGuard` (proves the player's data is unchanged), and the `Launcher*Checks.cs` files, which hold the portrait layout answers.
- `tests/DesktopHost/Harness/`: the scenarios, fixtures, `VirtualPointer` (touch and mouse through the engine's input), screenshots and the report. A scenario asks its layout questions through partial methods that the `Host/` files implement, so a scenario reads as behaviour and the launcher's geometry lives in one place per screen.
- The project compiles `GameHost` and `Platform` from `src/` with warnings as errors against the real game assembly, so a new warning there fails the desktop build even though the `GameHost` project itself only warns.
- All fixtures use the seed `TOUCHSPIRE-TEST` (the harness's name before it moved here): every fixture's map, encounters and rewards follow from it.

To add a check for a bug, write it first and run it against the unfixed code; keep the failing numbers for the commit message. A new shim mirrors one Cecil step's native boundary and nothing more: launcher code stays Harmony-free.

## Known limits

- **A covered window fails touch scenarios.** The pointer is real: if another window sits over the game, presses land elsewhere. The host detects this (`<scenario>_window_unobscured`) and skips the touch steps. Keep the game window in front and the mouse still during a run; before blaming code for a touch failure, look for large `[test pointer]` deltas in `process.log`.
- **Device-only behaviour.** The engine never focuses a card under a released touch here, so the device's cancel-release hover snap cannot be reproduced, and the device's frame timing (a one-frame flicker between touch input and layout) usually is not either. Ask for a phone check after changes to touch release, hover or per-frame layout.
- **Engine crashes.** About one run in a hundred dies in the game's atlas loader (`gchandle.get_intptr()` FATAL, or silently after `Missing sprite` warnings) or with a native `double free`. Rerun once before investigating; it is a finding only when it repeats on the same scenario.
- **`menu-audit` unlock timeline.** With no epochs to show, the game closes the unlock screen by itself right after `Created 0 Epoch slots`; when the check looks a moment later it reports `unlock-timeline: screen not found`. Rerun before investigating.
- **No `PhoneGraphics`.** Proof steps that turn Portrait Mode on or off through its settings rows fail with `Missing paginator in PhonePortraitMode`.
- **Rotation.** `portrait-leave` simulates the landscape request; a real portrait → landscape → portrait transition is device-only.

## macOS

The scripts are written for macOS too but have not been run on a Mac yet. What differs:

- The game is found under `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/`; the data folder (with `sts2.dll`) is searched for inside the `.app`, the executable is the one in `Contents/MacOS`, and mods go in `Contents/MacOS/mods` (the game loads mods next to its executable). Set `STS2_DATA_DIR` and `STS2_GAME_EXE` if the search picks the wrong files.
- The window keeps the native display driver, and macOS clamps a window to the screen: 1170 or 1280 pixels tall must fit the display's usable height. On a Retina display 540x1170 is 270x585 points and 720x1280 is 360x640, so they should fit; whether the game sizes its window in pixels or points there is untested. A clamped window fails the run at the main menu rather than testing the wrong size.
- No core dump capture (`coredumpctl` is Linux-only); the .NET crash report still lands in the run folder.
- The macOS game assembly is the one the iOS build patches, so a Mac run exercises the shims against that exact file. It is still Harmony, not the Cecil patch.
