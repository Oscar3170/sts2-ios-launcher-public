# Launcher development guidance

This repository builds an iOS launcher and Godot/.NET host for Slay the Spire 2. The IPA contains the launcher, not the game: an owning Steam account downloads the game at runtime. Start with [README.md](README.md) for product behavior, [BUILDING.md](BUILDING.md) for commands and version upgrades, and [TODO.md](TODO.md) for current issues. The source and current test results take precedence over old project notes.

## Where changes belong

| Area | Owner |
| --- | --- |
| Sign-in, download and preparation screens, app flow | [`STS2Mobile.Launcher`](src/STS2Mobile.Launcher) |
| App entry point, iOS/Godot packaging and native integration | [`STS2Mobile.FullGame`](src/STS2Mobile.FullGame), [`scripts/`](scripts), [`tools/mono/`](tools/mono) |
| In-game input, screen layout, node lifetimes, and animation | [`STS2Mobile.GameHost`](src/STS2Mobile.GameHost) |
| Game-independent layout math, policies, and patch gates | [`STS2Mobile.Platform`](src/STS2Mobile.Platform); keep it free of Godot and game types |
| Calls from rewritten game code into platform implementations | [`STS2Mobile.GameAdapters`](src/STS2Mobile.GameAdapters); [`GameStub`](src/STS2Mobile.GameStub) supplies build-time game signatures, not implementations or compatibility evidence |
| Assembly patching and mod weaving; resource pack conversion | [`STS2Mobile.GamePatcher`](src/STS2Mobile.GamePatcher) and [`STS2Mobile.GamePack`](src/STS2Mobile.GamePack), respectively |
| Steam account, downloads, Cloud and networking; wire serialization | [`STS2Mobile.Steam`](src/STS2Mobile.Steam) and [`STS2Mobile.SteamWire`](src/STS2Mobile.SteamWire); see [NETWORK.md](NETWORK.md) |

Use the existing owner instead of adding a second path. Split large classes by independent state and lifetime, not just line count: e.g. combat room staging, hand, and action rail should not all move because a card gained focus. Put pure geometry in the existing `Platform` math files and keep native node access and cleanup in `GameHost`. Keep behavioral fixes and file moves in separate commits when practical.

[`PortraitCombatRail`](src/STS2Mobile.GameHost/PortraitCombatRail.cs) owns the combat action controls, pile entrance and visibility handling, and their desktop restore values. `PortraitCombatLayout` passes the shared HUD geometry and keeps the stage → rail → hand → Proceed restore order.

[`PortraitCombatStage`](src/STS2Mobile.GameHost/PortraitCombatStage.cs) owns the frozen encounter plan, backdrop, creature roots and hitboxes, world-effect anchors, and Osty’s position gate. [`PortraitCreatureHud`](src/STS2Mobile.GameHost/PortraitCreatureHud.cs) owns creature health displays, intents, and powers; it batches after placement. [`PortraitCombatOrbs`](src/STS2Mobile.GameHost/PortraitCombatOrbs.cs) uses the stage hitboxes to fit the queue before draw, after native tweens. The coordinator keeps that order and the per-creature native restore sequence.

[`PortraitRunHeader`](src/STS2Mobile.GameHost/PortraitRunHeader.cs) owns the stone header, potion and relic rows, native holder input, and their desktop restore values. The coordinator discovers those nodes and passes canvas geometry; the header owns its pre-draw and swipe callbacks.

[`PortraitMapLayout`](src/STS2Mobile.GameHost/PortraitMapLayout.cs) owns the map graph, drawing footer, legend, map-point touch targets, and their restore state. It tells navigation where the footer starts; [`PortraitNavigationPads`](src/STS2Mobile.GameHost/PortraitNavigationPads.cs) still places Back.

[`PortraitShopLayout`](src/STS2Mobile.GameHost/PortraitShopLayout.cs) owns the standard, weird, and Blitz shop compositions, inventory pages, price labels and touch pads. It keeps native stock nodes in place and restores the room and inventory through their existing lifecycle.

[`PortraitEventLayout`](src/STS2Mobile.GameHost/PortraitEventLayout.cs) owns event and Ancient composition, early option sizing, and native restore state. Event art extends behind the stone's torn lower edge; dialogue and choice bounds are independent of that overlap.

Live combat events keep their native fight scene and description reveal. In portrait they share the standard event choice band, row sizing, and text measurement without reparenting their nodes. The `event` scenario captures Punch Off before and after the choice, checks drawn frames for event UI left visible after combat starts, then plays a card and ends the turn.

[`PortraitRestSiteLayout`](src/STS2Mobile.GameHost/PortraitRestSiteLayout.cs) owns campsite framing, the option rows and copy, and their desktop restore state. It leaves native option nodes and click handling in place.

## Developing safely

- Check the working tree and active game/profile before editing. Preserve unrelated changes, user settings, saves, and local signing configuration. Keep installs and writes to live game data opt-in; never overwrite an assembly while the game is running.
- Preserve native art, animation, hit testing, accessibility of controls, and gameplay. For portrait, use the applied canvas and safe area, not device names. Keep 9:16, taller phones, and landscape usable. Own each property in one place; restore only what portrait changed and disconnect callbacks or cancel stale deferred work on exit. Do not casually reparent initialized or pooled Godot nodes.
- Keep game assemblies, extracted assets, generated Xcode projects and IPAs, credentials, and local Steam or signing state out of source control. Use the ignored locations in [BUILDING.md](BUILDING.md#local-state); deliver changes to the scripts or source inputs, not generated outputs. Keep test fixtures out of the app.
- Treat profile changes as compatibility work, not a string bump. Follow [Updating inputs](BUILDING.md#updating-inputs) for the exact macOS depot, retained profiles, patch reports, game-facing references, asset conversion, and mod compatibility. Do not replace an expected hash with a mismatched file's hash merely to make preparation pass.

## Testing and delivery

1. Start with the narrowest check that exercises the change. Run `dotnet test tests/<project>` for isolated .NET logic; use `STS2Mobile.Platform.Tests` for pure layout and input policy and `GamePatcher.Tests` for assembly preparation. Use [BUILDING.md](BUILDING.md#changing-the-app) for setup, `scripts/test.sh`, and the relevant installed-game prerequisites. A skipped installed-game test is not a compatibility pass.
2. For UI, exercise the real input and its effect, including screen transitions. Inspect phone-sized captures at compact and tall portrait; use landscape as a control. For animation or flicker, observe successive drawn frames. For orientation changes, test portrait → landscape → portrait while the affected screen is active, not just separate launches.
3. If working with the sibling TouchSpireUI Linux adapter, run the focused launcher-host scenario (for example, `touchspire-test run menus --touch --window=720x1280 --host=launcher` when the broker is available). The broker builds committed `HEAD` in both checkouts. Inspect `RESULTS.json`, the logs, and captures; record the launcher revision and game version. This covers desktop behavior, not the iOS build or device runtime.
4. For iOS-specific changes or a profile bump, follow the [build and device workflow](BUILDING.md#signing-and-installing). A Mac or the repository's [Build IPA workflow](BUILDING.md#releases-for-people-with-no-mac) can produce the build; a simulator does not substitute for device checks of touch, safe areas, performance, or memory. Report which checks ran, what was skipped, and where the evidence is.

## Use Cecil when native code owns the transition

- If a portrait change is static and the native game does not rewrite it, use the screen's layout owner. Apply on open, rebuild, or resize; do not add an IL hook or a per-frame writer just to place a control.
- If native focus, return, or show/hide code writes a conflicting target or starts a tween, change the target at its source. Prefer supplying portrait endpoints before the native animation starts. If the native geometry cannot be reused, guard only its writes and give portrait one replacement writer. Do not let native geometry run and then restore snapshots on focus, a timer, or every frame. A pre-draw commit that owns the only targets is different from a pre-draw repair that fights native code.
- The shipped app uses Mono full AOT plus interpreter, without JIT detours. Use the existing Mono.Cecil preparation pipeline in [`STS2Mobile.GamePatcher`](src/STS2Mobile.GamePatcher). Linux Harmony shims are a test adapter, not iOS implementation or proof that Cecil applied.

## How to add or change a hook

1. Trace the real native method and scene from the installed game build. List every relevant geometry writer and the native work that must still run. Check supported profiles in `tools/dependencies.json`; the stub and a nearby Linux version are not substitutes for the target assembly.
2. Register the step in [`PhonePatches.cs`](src/STS2Mobile.GamePatcher/PhonePatches.cs) for phone UI changes. Use `GamePatch.Method` and `GamePatch.Adapter`. Keep the IL hook small, put the mode/ownership gate in `STS2Mobile.Platform`, and keep node lifetime, geometry, and presentation in the appropriate `STS2Mobile.GameHost` screen owner. The original path must remain intact in landscape and when no host can take ownership.
3. Preflight **all** methods and imports for the step before mutating any method. Match meaningful calls, fields, counts and branch joins rather than absolute IL offsets. Check stack balance at both branch outcomes, preserve incoming labels and exception-region boundaries, and handle short branches when adding instructions. Report what was patched with `GamePatch.Record`. Set `CanSkipOnTestedBuild` only if refusing the step leaves native behavior intact and the host can detect that it did not acquire ownership. The optional-step catch does not roll back partial writes.
4. Run the actual Cecil step on the exact inputs for every affected profile and inspect the patch report; a skipped optional step is not a passing patch test. Test rejection of a missing seam for coupled hooks. If the Linux launcher harness needs a shim, mirror the native boundary there and use it for fast behavioral coverage. Inspect visible geometry on drawn frames, not just target fields. Verify portrait interaction, landscape fallback, and active rotation on device. Build and profile commands are in [BUILDING.md](BUILDING.md).

Osty is a stage-specific example: `NCreature.ScaleAndPositionOsty` still grows the visuals and updates bounds. Its parallel desktop root tween is skipped only after the portrait stage has placed that creature. Use the Linux `stage-osty` scenario to compare drawn-frame position and growth in portrait and landscape; run Cecil against the target Mac assembly separately.

## Reference: portrait combat hand

[`PortraitHandLayout`](src/STS2Mobile.GameHost/PortraitHandLayout.cs) owns hand targets, in-hand selection, and temporary changes to pooled hand cards. `PortraitCombatLayout` keeps the frame and restore order and passes the computed bands to the hand. `PhoneTouchCards` still owns gestures and native play/selection input; it does not own layout targets.

Draw bookkeeping belongs to the active native hand, not to the portrait toggle. When combat replaces the hand, discard the previous turn number, slot-count lock, motion records, and draw-pile binding before laying out the new opening draw. `combat-piles` checks this by opening a second fight with Big Mushroom and measuring each card’s path to its final slot.

[`HandLayoutPatches.cs`](src/STS2Mobile.GamePatcher/HandLayoutPatches.cs) guards both the focused and ordinary geometry in `NPlayerHand.RefreshLayout` and the return geometry in `NHandCardHolder.SetDefaultTargets` as one step. It does not skip either whole method: hitbox filters, focus neighbours, index labels, and the return `ZIndex` reset still run. [`HandLayoutGate`](src/STS2Mobile.Platform/HandLayoutGate.cs) allows suppression only after the portrait host can own targets. The host uses native motion setters for portrait slots and scales only the pressed card's artwork, leaving its hitbox and neighbours stable. Arrival bounds are not a reason to bring back after-the-fact desktop retarget repairs. Verify slow pickup and cancelled return over another card by sampling its visible artwork, holder, and hitbox separately.
