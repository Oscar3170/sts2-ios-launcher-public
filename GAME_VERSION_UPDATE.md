# Moving the app to a new game version

The app has a *profile* for each game version it has tested. A profile is a block in `tools/dependencies.json`. The phone uses it to check the downloaded files, label the build "tested", and prepare it strictly. A build with no profile can still be played: the phone patches it in tolerant mode and shows it as untested.

Moving to a new version means adding a profile for it. The previous one stays tested, so a phone that hasn't updated keeps working. This was last done for v0.113.0 to v0.114.0 (`4a8b72a`); the earlier move, v0.111.0 to v0.113.0, is `0dbb2b5`.

Never replace a hash just to make setup accept a new build. Every pin comes from the files themselves or from what a phone actually ran.

## What a profile pins

| Field in `game` | Where it comes from |
|---|---|
| `version` | `release_info.json` in the new build |
| `steam_depot`, `steam_manifest` | macOS depot 2868842 and its manifest id for that build |
| `release_info_sha256`, `pack_sha256`, `assembly_sha256`, `other_assemblies_sha256` | SHA-256 of the macOS depot files (see step 2) |
| `textures_kept_original` | the texture quality pass (step 5) |
| `peak_disk_bytes` | the most disk space setup used on the phone, measured (step 8) |

Other places name the version and have to move with it:

- Profile ids: `tools/profiles.py` (3 ids), `TextureProfile` in `src/STS2Mobile.GamePack/GamePackBuilder.cs`, the label in `tools/FullGamePatcher/Program.cs`, and the asset profile in `tests/test_limit_particle_preprocess.py`.
- `GAME_PACK_ENTRIES` in `tools/stage_full_game.py`.
- Version text and Steam build ids: `src/STS2Mobile.FullGame/LauncherPreview.cs` (also the texture count in its "prepare" preview), the doc comments in `src/STS2Mobile.Launcher/GameInstallMessages.cs`, `tests/STS2Mobile.Steam.Tests/VersionScreenTests.cs`, `GameProfileTests`, `InstalledBuildTests`, `UntestedBuildTests`, `OptionalBuiltInModTests`, and `GameInstallerTests`.
- Output pins:
  - `PlayedSha256` / `ModsOnSha256` in `tests/GamePatcher.Tests/InstalledGameTests.cs`, and `PlayedSha256` in `UntestedBuildTests.cs`.
  - `PlayedSha256` / `PlayedEntries` and the texture `Total` in `tests/GamePack.Tests/InstalledGameTests.cs`.
  - `MadeUpTexturesSha256` in `tests/GamePack.Tests/TextureConversionTests.cs`. The texture profile id is written into the overlay manifest, so this hash changes with every bump.
- `README.md` "Game versions", and the version named in `BUILDING.md` "Updating inputs" and "Troubleshooting".

## On a Mac or on Linux

The steps are the same on both. A few use different tools, and Linux can't produce two values on its own:

| Step | On a Mac (how v0.111.0 → v0.113.0 was done) | On Linux (how v0.113.0 → v0.114.0 was done) |
|---|---|---|
| 1. Download | The Steam client, game switched to `public-beta` | `steamcmd`, forced to macOS and `public-beta` |
| 4. Patch | `./setup.sh --prepare-only` and `scripts/prepare-game-files.sh` | `tools/FullGamePatcher` run directly with `dotnet10` |
| 5. Textures | `scripts/godot.sh` (setup's Godot) | The official Linux Godot 4.5.1 editor |
| 7. Tests | `scripts/test.sh`, everything | `dotnet10 test` per project. The pack tests that need the Mac's `analysis/` output are skipped, and the macOS-only tests fail. |
| 8. Pack pin | Built here and checked against the phone | The phone's own record only: the Linux encoder gives different texture bytes |
| 8. Encoder | `vendor/texture-encoder/macos/*.dylib` from setup | Built by hand, used only for `MadeUpTexturesSha256` |

Either way, the phone's run in step 7 is what turns "it prepares" into "it plays".

## Steps

### 1. Get the macOS build of the new version

Profile hashes are of the **macOS** depot (2868842). The Linux or Windows depots have different files, so their hashes are wrong for a profile.

**On Linux** (v0.114.0; the Steam client's console download did not work), use `steamcmd`. It must be forced to the macOS platform and the beta branch. Without the platform line it fetches the Linux depot, and without `-beta` it fetches the public branch.

```sh
steamcmd \
  +@sSteamCmdForcePlatformType macos \
  +force_install_dir "$PWD/.work/sts2-macos-beta" \
  +login <account that owns Slay the Spire 2> \
  +app_update 2868840 -beta public-beta validate \
  +quit
```

Check what arrived before you trust it. A manifest id can't be used to pick an older build: Steam serves the head of the branch.

```sh
R=.work/sts2-macos-beta/SlayTheSpire2.app/Contents/Resources
jq . "$R/release_info.json"                  # the expected version and branch
rg -n '"buildid"|"BetaKey"|"manifest"' .work/sts2-macos-beta/steamapps/appmanifest_2868840.acf
```

`InstalledDepots → 2868842 → manifest` in the `.acf` file is the `steam_manifest` to pin, and `buildid` is the Steam build id used in the preview and in `VersionScreenTests`. As a cross-check, the depot's size can be compared with Steam's public app info (`https://api.steamcmd.net/v1/info/2868840`, branch `public-beta`).

**On a Mac** (v0.113.0), switch the game to the `public-beta` branch in its Steam Betas properties and let it update. The files are then under `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/`, and the manifest id is also the name of `depotcache/2868842_<manifest>.manifest`. Run the same checks with `R` pointing there.

### 2. Write the profile

In `tools/dependencies.json`, move the current `game` block unchanged to the front of `previous_game_profiles`. Write the new `game` block from the hashes:

```sh
A=$R/data_sts2_macos_arm64
sha256sum "$R/release_info.json" "$R/Slay the Spire 2.pck" \
  $A/{sts2,Sentry,Sentry.Godot,Steamworks.NET,SmartFormat,SmartFormat.ZString}.dll
```

`Sentry.dll` and `Sentry.Godot.dll` go in `other_assemblies_sha256` with the rest; the code moves them to the patch inputs itself.

### 3. Move the ids and the pack entry count

Bump every id listed above. Count the pack's entries with the repository's own reader:

```sh
cd tools && python3 -c "from godot_pack import read_directory; print(len(read_directory(open('../$R/Slay the Spire 2.pck','rb'))))"
```

### 4. Patch the new assembly and fix what fails

**On a Mac**, run `./setup.sh --prepare-only` and `scripts/prepare-game-files.sh`. The patch report is `analysis/full-game-managed/public/game/profile.json`.

**On Linux**, run the patcher directly with the pinned SDK (`dotnet10`). It accepts the assembly once the profile has its hash, and writes its report as `profile.json` in the output folder:

```sh
dotnet10 run --project tools/FullGamePatcher -- stage "$A/sts2.dll" .work/<new dir> mods/builtin.json
```

Fix every patch that fails in `src/STS2Mobile.GamePatcher`. Phones on the previous profile run the same patches, so find a renamed target by either name (`GamePatch.Method(type, name, earlierName)`). Then compare the patch report with the previous version's, and look at every method whose patch moved.

If the patch roster changes, `UntestedBuildTests.TheShortListOfPatchesTheGameCannotRunWithout` lists every patch by name and must change in the same commit.

### 5. Run the texture quality pass

Stage the pack, then run the ASTC pass. A texture whose ASTC result falls below 40 dB PSNR stays in its original format, and `textures_kept_original` lists those:

```sh
python3 tools/stage_full_game.py --pack "$R/Slay the Spire 2.pck" --output .work/<v>/assets
python3 tools/transcode_textures_astc.py --assets .work/<v>/assets --output .work/<v>/textures --workers "$(nproc)" \
  --godot <Godot 4.5.1>        # Mac: omit (scripts/godot.sh); Linux: the editor binary
jq -c '.keptOriginal | map(.source)' .work/<v>/textures/texture-report.json
```

**On Linux**, download the official `Godot_v4.5.1-stable_linux.x86_64` editor; it runs the same GDScript worker and the same encoder version. For v0.114.0 it kept the same 7 textures as v0.113.0 (1565 converted, lowest 40.16 dB). The report's `textures` count is the new `Total` in `tests/GamePack.Tests/InstalledGameTests.cs`.

### 6. Check QuickMatch

Build QuickMatch against the new game and run its tests (its repository says how). The weave check sees only the calls a mod makes, not the abstract members it must implement. If the game changed QuickMatch's base types, the app needs a new QuickMatch release, pinned in `mods/builtin.json` and `tools/dependencies.json`. QuickMatch is optional; a build without it plays normally.

### 7. Run the tests and play it on a phone

**On a Mac**, run `scripts/test.sh`. It finds the Steam install and the `analysis/` output of steps 4 and 5 on its own.

**On Linux**, point the installed-game tests at the downloaded files, and put the hand-built encoder (step 8) beside the GamePack tests:

```sh
export STS2_GAME_DATA=$PWD/$A                       # GamePatcher.Tests
export STS2_GAME_PACK="$PWD/$R/Slay the Spire 2.pck" # GamePack.Tests
dotnet10 test tests/GamePatcher.Tests
dotnet10 test tests/GamePack.Tests
dotnet10 test tests/STS2Mobile.Steam.Tests
(cd tests && python3 -m unittest discover -p 'test_*.py')
```

On Linux these always fail, whatever the version: `GameProfileTests.FootprintIsReadable` and 13 Python tests that need macOS tools. Compare against the same run on the previous commit. The GamePack tests that compare with the Mac's `analysis/` reference skip, so the full-pack pin is checked only on a Mac.

Build the app and set the new version up on a phone. Until the profile ships, the phone offers the build as untested and prepares it in tolerant mode; that is fine for this check. Play at least a fight.

### 8. Pin what the phone played

Copy these from the app's `game/` folder on the phone:

| File | What it gives |
|---|---|
| `installed.json` | Branch, manifest and build. Every patch should have `"applied": true`. |
| `assembly-preparation.json` | The SHA-256 of the patched `sts2.dll` (`files["sts2.dll"]`) and the patcher id |
| `pack-preparation.json` | `outputSha256` of the phone's `FullGame.pck`, its pack-steps id, and the input pack hash, which must equal `pack_sha256` |
| `sts2.dll` (optional) | The patched assembly, to compare byte for byte |

Then set the pins:

- **Patched assembly.** Prepare it here from the same commit the phone was built from. If it matches the phone's `sts2.dll` hash, this checkout reproduces the phone. Then pin this checkout's output.
  - If patches changed after the phone's build, the pin is the phone's bytes plus those patches. Say so in the pin's comment.
  - For v0.114.0 the phone ran `1095dc6`. The commit before the Osty patch reproduced the phone's `42707dad…`, and the pin is HEAD's `611ad986…`.
- **Pack.** Pin the phone's `outputSha256` as the GamePack `PlayedSha256`, after checking that `src/STS2Mobile.GamePack` hasn't changed between the phone's commit and HEAD. `PlayedEntries` is the entry count of a pack built here; it depends only on the entries, not the texture bytes.
  - **On a Mac**, `scripts/test.sh` then rebuilds the pack and checks the pin. The Mac's arm64 encoder is the phone's.
  - **On Linux**, the encoder can't check it. A Linux build gives a pack of the same size with different bytes, so the pin rests on the phone's record until a Mac run confirms it.
- **`MadeUpTexturesSha256`.** Recompute it with the new id.
  - **On a Mac**, use setup's encoder.
  - **On Linux**, a hand-built encoder gives the Mac's bytes for these small made-up textures: the old v0.113.0 pin reproduced exactly. Build it this way:
    - Compile `src/STS2Mobile.GamePack/native/texture_encoder.cpp` and the astc-encoder sources pinned in `tools/dependencies.json` with the flags in `tools/build_texture_encoder.py`, except use `-DASTCENC_NEON=0` and add `-fPIC -shared`.
    - The test project copies `vendor/texture-encoder/macos/libsts2_texture_encoder.dylib`, so put the `.so` there under that name for the build, and beside the test output as `libsts2_texture_encoder.so`.
    - Remove it afterwards. `vendor/` is git-ignored, and a Mac setup builds its own.
- **`peak_disk_bytes`.** Measure the most disk space setup uses on the phone. Without a measurement, use the installer's estimate, `files + 1.5 × pack` from `GameInstaller.PeakDiskBytes`, and say so in the commit. It must not exceed that estimate, since `RealManifestTests` checks that the estimate covers it. v0.114.0 uses the estimate, 5.40 GB.

### 9. Commit and update the docs

Make one commit for the profile, the ids, the pins and the version text, in the style of `4a8b72a`. Say in its body which values were measured, which were estimated, and what the phone ran. Update `README.md` "Game versions" and the version named in `BUILDING.md`.
