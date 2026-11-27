using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Settings;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// One narrow Harmony shim per launcher <c>PatchStep</c> in <c>STS2Mobile.GamePatcher/PhonePatches.cs</c> that portrait depends on,
/// and nothing else. On the phone those are Cecil edits baked into the game's assembly; here they are applied at run time, only
/// when the host is active, through one explicit <see cref="Harmony"/> instance. The shims carry no <c>[HarmonyPatch]</c>
/// attribute on purpose: <c>TestProfileScope.Install</c> calls <c>Harmony.PatchAll(assembly)</c>, which would otherwise apply
/// them a second time under another id.
/// <list type="bullet">
/// <item><c>FillScreen</c> ("The game fills the screen"): <c>SettingsSave.AspectRatioSetting</c> reads as Auto. It is never saved:
/// while the game serializes its settings the stored value is read, so the player's own aspect ratio is not rewritten.</item>
/// <item><c>KeepClearOfTheNotch</c> ("The game keeps clear of the notch and the island"): <c>NGlobalUi.OnWindowChange()</c> and
/// <c>NMainMenu.OnWindowChange(bool)</c> keep their automatic-aspect check and replace what follows with
/// <see cref="SafeAreaCanvas.Layout"/>.</item>
/// <item><c>CancelCardReleasedBelowPlayZone</c> ("A card let go below the play zone goes back"): after
/// <c>NMouseCardPlay._Input</c> stores <c>_isLeftMouseDown = false</c> it calls <c>CancelPlayCard()</c> unless
/// <c>IsCardInPlayZone()</c>, the same IL the Cecil step inserts.</item>
/// <item><c>HandCardPress</c> ("A hand card is picked up when the finger moves"): <c>NHandCardHolder.OnMousePressed</c> notes
/// the event's device and <c>NPlayerHand.OnHolderPressed</c> asks <see cref="HandPickupGate"/> whether to start the play. The
/// Cecil step makes the same two edits in the game's assembly; the gate is the launcher's, and the dragged card's own
/// presentation runs after the game's move.</item>
/// <item><c>HandLayoutOwnership</c> ("The portrait hand keeps its own card targets"): <c>NPlayerHand.RefreshLayout</c> and
/// <c>NHandCardHolder.SetDefaultTargets</c> keep every native bookkeeping write (hitbox filter, focus neighbours, index
/// labels and the default-targets ZIndex reset) but skip their per-card position, scale and angle targets while
/// <see cref="HandLayoutGate.SuppressNativeTargets(object)"/> lets the portrait layout own that hand; with the gate false
/// they run the game's own IL unchanged. The Cecil step makes the same two edits in the game's assembly.</item>
/// <item><c>CardGridWindow</c> ("Card grids keep their rows while scrolled in portrait"): <c>NCardGrid.AllocateCardHolders</c>
/// first asks <see cref="CardGridWindowGate.KeepsWindow(object)"/> and returns when the portrait host kept the window; the
/// Cecil step inserts the same call and return at the method's start.</item>
/// <item><c>CardHolderScale</c> ("Grid cards keep their column size in portrait"): <c>NCardHolder.SmallScale</c> and
/// <c>HoverScale</c> first ask <see cref="CardHolderScaleGate.ScaleFor(object)"/> and return that uniform scale when it is
/// above 0; the Cecil step inserts the same call at both getters' start.</item>
/// </list>
/// The launcher's other PhonePatches steps (load limit, cursor, release_info path) do not affect portrait and are not reproduced.
/// </summary>
internal static class PhonePatchShims
{
    public const string HarmonyId = "sts2mobile.desktophost";

    private static Harmony? _harmony;
    private static readonly List<(string Name, string? Error)> ShimResults = new();

    [ThreadStatic]
    private static int _serializingSettings;

    private static int _suppressedSettingsWrites;

    /// <summary>
    /// Test isolation, set by test runs only: settings.save is shared by every profile, so a test run never writes it
    /// (the game's own in-memory settings are untouched). Without it a settings write is allowed, with FillScreen's value hidden.
    /// </summary>
    public static bool SuppressSettingsWrites { get; set; }

    public static int SuppressedSettingsWrites => Volatile.Read(ref _suppressedSettingsWrites);

    /// <summary>Per shim, whether it applied (null error) or why not.</summary>
    public static IReadOnlyList<(string Name, string? Error)> Results => ShimResults;

    /// <summary>Records a host-only (non-PhonePatches) shim in the same list, so RESULTS shows it as <c>shim:&lt;name&gt;</c>.</summary>
    public static void AddResult(string name, string? error) => ShimResults.Add((name, error));

    public static void Install()
    {
        if (_harmony != null) return;
        var harmony = new Harmony(HarmonyId);
        _harmony = harmony;
        Apply("FillScreen", () => FillScreen(harmony));
        Apply("KeepClearOfTheNotch", () => KeepClearOfTheNotch(harmony));
        Apply("CancelCardReleasedBelowPlayZone", () => CancelCardReleasedBelowPlayZone(harmony));
        Apply("HandCardPress", () => HandCardPress(harmony));
        Apply("HandLayoutOwnership", () => HandLayoutOwnership(harmony));
        Apply("OstyPositionOwnership", () =>
            harmony.Patch(Required(AccessTools.Method(typeof(NCreature), nameof(NCreature.ScaleAndPositionOsty),
                [typeof(float), typeof(double)]), "NCreature.ScaleAndPositionOsty(float, double)"),
                transpiler: Own(nameof(SuppressOstyPosition))));
        Apply("CardGridWindow", () =>
            harmony.Patch(Required(AccessTools.Method(typeof(NCardGrid), "AllocateCardHolders", Type.EmptyTypes),
                "NCardGrid.AllocateCardHolders()"), prefix: Own(nameof(KeepCardGridWindow))));
        Apply("CardHolderScale", () =>
        {
            harmony.Patch(Required(AccessTools.PropertyGetter(typeof(NCardHolder), "SmallScale"), "NCardHolder.SmallScale"),
                prefix: Own(nameof(AskCardHolderScale)));
            harmony.Patch(Required(AccessTools.PropertyGetter(typeof(NCardHolder), "HoverScale"), "NCardHolder.HoverScale"),
                prefix: Own(nameof(AskCardHolderScale)));
        });
    }

    private static void Apply(string name, Action patch)
    {
        try
        {
            patch();
            ShimResults.Add((name, null));
            Harness.Log.Info($"Launcher host shim applied: {name}");
        }
        catch (Exception ex)
        {
            ShimResults.Add((name, ex.Message));
            Harness.Log.Error($"Launcher host shim {name} does not fit this game: {ex}");
        }
    }

    private static MethodInfo Required(MethodInfo? method, string description) =>
        method ?? throw new InvalidOperationException($"{description} not found.");

    private static HarmonyMethod Own(string name) => new(typeof(PhonePatchShims), name);

    // ---- FillScreen ----

    private static void FillScreen(Harmony harmony)
    {
        var getter = Required(AccessTools.PropertyGetter(typeof(SettingsSave), nameof(SettingsSave.AspectRatioSetting)),
            "SettingsSave.AspectRatioSetting getter");
        var save = Required(AccessTools.Method(typeof(SettingsSaveManager), nameof(SettingsSaveManager.SaveSettings), Type.EmptyTypes),
            "SettingsSaveManager.SaveSettings()");
        // The launcher checks the constant before patching; so do we.
        if ((int)AspectRatioSetting.Auto != 5) throw new InvalidOperationException("Unexpected AspectRatioSetting values.");
        harmony.Patch(getter, prefix: Own(nameof(AspectRatioPrefix)));
        harmony.Patch(save, prefix: Own(nameof(SaveSettingsPrefix)), finalizer: Own(nameof(SaveSettingsFinalizer)));
    }

    private static bool AspectRatioPrefix(ref AspectRatioSetting __result)
    {
        if (_serializingSettings > 0) return true;
        __result = AspectRatioSetting.Auto;
        return false;
    }

    private static bool SaveSettingsPrefix(out bool __state)
    {
        if (SuppressSettingsWrites)
        {
            __state = false;
            Interlocked.Increment(ref _suppressedSettingsWrites);
            return false;
        }
        __state = true;
        _serializingSettings++;
        return true;
    }

    private static void SaveSettingsFinalizer(bool __state)
    {
        if (__state && _serializingSettings > 0) _serializingSettings--;
    }

    // ---- KeepClearOfTheNotch ----

    private static void KeepClearOfTheNotch(Harmony harmony)
    {
        var globalUi = Required(AccessTools.Method(typeof(NGlobalUi), "OnWindowChange", Type.EmptyTypes), "NGlobalUi.OnWindowChange()");
        var mainMenu = Required(AccessTools.Method(typeof(NMainMenu), "OnWindowChange", [typeof(bool)]), "NMainMenu.OnWindowChange(bool)");
        // sts2.dll is not publicized here, so the game's private _window fields are read by name.
        _globalUiWindow = AccessTools.Field(typeof(NGlobalUi), "_window") ?? throw new InvalidOperationException("NGlobalUi._window not found.");
        _mainMenuWindow = AccessTools.Field(typeof(NMainMenu), "_window") ?? throw new InvalidOperationException("NMainMenu._window not found.");
        harmony.Patch(globalUi, prefix: Own(nameof(GlobalUiOnWindowChange)));
        harmony.Patch(mainMenu, prefix: Own(nameof(MainMenuOnWindowChange)));
    }

    private static bool GlobalUiOnWindowChange(NGlobalUi __instance)
    {
        if (SaveManager.Instance.SettingsSave.AspectRatioSetting == AspectRatioSetting.Auto) FitCanvasToSafeArea(WindowOf(_globalUiWindow, __instance));
        return false;
    }

    // __0 is the game's isAspectRatioAuto.
    private static bool MainMenuOnWindowChange(NMainMenu __instance, bool __0)
    {
        if (__0) FitCanvasToSafeArea(WindowOf(_mainMenuWindow, __instance));
        return false;
    }

    private static FieldInfo? _globalUiWindow, _mainMenuWindow;

    private static Window WindowOf(FieldInfo? field, object instance) =>
        field?.GetValue(instance) as Window ?? throw new InvalidOperationException("The game's window field is not available.");

    private static void FitCanvasToSafeArea(Window window)
    {
        Vector2I size = window.Size;
        long aspect = SafeAreaCanvas.Layout(size.X, size.Y, out int canvasWidth, out int canvasHeight);
        window.ContentScaleAspect = (Window.ContentScaleAspectEnum)aspect;
        window.ContentScaleSize = new Vector2I(canvasWidth, canvasHeight);
    }

    // ---- CancelCardReleasedBelowPlayZone ----

    private static void CancelCardReleasedBelowPlayZone(Harmony harmony)
    {
        var input = Required(AccessTools.Method(typeof(NMouseCardPlay), "_Input", [typeof(InputEvent)]), "NMouseCardPlay._Input");
        harmony.Patch(input, transpiler: Own(nameof(CancelReleasedCardTranspiler)));
    }

    private static IEnumerable<CodeInstruction> CancelReleasedCardTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        FieldInfo leftDown = AccessTools.Field(typeof(NMouseCardPlay), "_isLeftMouseDown")
            ?? throw new InvalidOperationException("NMouseCardPlay._isLeftMouseDown not found.");
        MethodInfo inPlayZone = Required(AccessTools.Method(typeof(NMouseCardPlay), "IsCardInPlayZone", Type.EmptyTypes), "NMouseCardPlay.IsCardInPlayZone");
        MethodInfo cancel = Required(AccessTools.Method(typeof(NCardPlay), "CancelPlayCard", Type.EmptyTypes), "NCardPlay.CancelPlayCard");

        var code = instructions.ToList();
        int released = -1, matches = 0;
        for (int i = 1; i < code.Count; i++)
        {
            if (code[i].StoresField(leftDown) && code[i - 1].opcode == OpCodes.Ldc_I4_0)
            {
                released = i;
                matches++;
            }
        }
        if (matches != 1 || released + 1 >= code.Count)
            throw new InvalidOperationException("NMouseCardPlay._Input does not store _isLeftMouseDown = false exactly once.");

        // Inserted before the instruction that follows the store, so branches that already targeted it still skip the check.
        CodeInstruction resume = code[released + 1];
        System.Reflection.Emit.Label resumeLabel = generator.DefineLabel();
        resume.labels.Add(resumeLabel);
        code.InsertRange(released + 1,
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, inPlayZone),
            new CodeInstruction(OpCodes.Brtrue, resumeLabel),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, cancel),
        ]);
        return code;
    }

    // ---- HandCardPress ----

    private static void HandCardPress(Harmony harmony)
    {
        var press = Required(AccessTools.Method(typeof(NHandCardHolder), "OnMousePressed", [typeof(InputEvent)]),
            "NHandCardHolder.OnMousePressed(InputEvent)");
        var holderPressed = Required(AccessTools.Method(typeof(NPlayerHand), "OnHolderPressed", [typeof(NCardHolder)]),
            "NPlayerHand.OnHolderPressed(NCardHolder)");
        harmony.Patch(press, prefix: Own(nameof(NoteHandCardPress)));
        harmony.Patch(holderPressed, prefix: Own(nameof(DeferHandCardPickup)));
    }

    private static void NoteHandCardPress(InputEvent __0) => HandPickupGate.NotePress(__0.Device);

    private static bool DeferHandCardPickup() => !HandPickupGate.Defer();

    private static bool KeepCardGridWindow(NCardGrid __instance) => !CardGridWindowGate.KeepsWindow(__instance);

    private static bool AskCardHolderScale(NCardHolder __instance, ref Vector2 __result)
    {
        float scale = CardHolderScaleGate.ScaleFor(__instance);
        if (scale <= 0f) return true;
        __result = new Vector2(scale, scale);
        return false;
    }

    private static IEnumerable<CodeInstruction> SuppressOstyPosition(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int local = code.FindIndex(i => i.operand is MethodInfo { Name: "IsMe", DeclaringType.FullName: "MegaCrit.Sts2.Core.Context.LocalContext" });
        int start = local + 2;
        if (local < 0 || code.Count < start + 6 ||
            (code[local + 1].opcode != OpCodes.Brfalse && code[local + 1].opcode != OpCodes.Brfalse_S) ||
            code[local + 1].operand is not System.Reflection.Emit.Label resume ||
            code[start].opcode != OpCodes.Ldarg_0 || code[start].labels.Count != 0 ||
            code[start + 2].operand is not MethodInfo { Name: "Parallel", DeclaringType.FullName: "Godot.Tween" } ||
            code[start + 4].operand is not "position")
            throw new InvalidOperationException("Unexpected Osty position tween; leaving native motion intact.");
        code.InsertRange(start, [
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, AccessTools.Method(typeof(OstyPositionGate), nameof(OstyPositionGate.SuppressNativePosition))),
            new(OpCodes.Brtrue, resume)
        ]);
        return WithLongBranches(code);
    }

    // ---- HandLayoutOwnership ----

    private static void HandLayoutOwnership(Harmony harmony)
    {
        var refresh = Required(AccessTools.Method(typeof(NPlayerHand), "RefreshLayout", Type.EmptyTypes),
            "NPlayerHand.RefreshLayout()");
        var defaults = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetDefaultTargets), Type.EmptyTypes),
            "NHandCardHolder.SetDefaultTargets()");
        // Resolve the gate before patching anything: on a game without it neither half of the pair is installed.
        _ = HandLayoutGateMethod();

        HarmonyMethod refreshPatch = Own(nameof(SuppressRefreshLayoutGeometry));
        HarmonyMethod defaultsPatch = Own(nameof(SuppressDefaultTargetsGeometry));
        harmony.Patch(refresh, transpiler: refreshPatch);
        try
        {
            harmony.Patch(defaults, transpiler: defaultsPatch);
        }
        catch (Exception error)
        {
            // Never leave half of the pair installed: a refresh without the default targets still takes the native
            // target for one animation step when a card returns, which is exactly the visible wobble this removes.
            try
            {
                harmony.Unpatch(defaults, defaultsPatch.method);
            }
            catch
            {
                // The failed half never landed; the refresh below is the half that did.
            }
            try
            {
                harmony.Unpatch(refresh, refreshPatch.method);
            }
            catch (Exception undo)
            {
                throw new InvalidOperationException(
                    "NPlayerHand.RefreshLayout could not be unpatched after NHandCardHolder.SetDefaultTargets failed: " +
                    undo.Message, error);
            }
            throw;
        }
    }

    private static MethodInfo HandLayoutGateMethod() =>
        Required(AccessTools.Method(typeof(HandLayoutGate), nameof(HandLayoutGate.SuppressNativeTargets), [typeof(object)]),
            "HandLayoutGate.SuppressNativeTargets(object)");

    private static readonly IReadOnlyDictionary<OpCode, OpCode> LongBranches = new Dictionary<OpCode, OpCode>
    {
        [OpCodes.Br_S] = OpCodes.Br,
        [OpCodes.Brfalse_S] = OpCodes.Brfalse,
        [OpCodes.Brtrue_S] = OpCodes.Brtrue,
        [OpCodes.Beq_S] = OpCodes.Beq,
        [OpCodes.Bge_S] = OpCodes.Bge,
        [OpCodes.Bge_Un_S] = OpCodes.Bge_Un,
        [OpCodes.Bgt_S] = OpCodes.Bgt,
        [OpCodes.Bgt_Un_S] = OpCodes.Bgt_Un,
        [OpCodes.Ble_S] = OpCodes.Ble,
        [OpCodes.Ble_Un_S] = OpCodes.Ble_Un,
        [OpCodes.Blt_S] = OpCodes.Blt,
        [OpCodes.Blt_Un_S] = OpCodes.Blt_Un,
        [OpCodes.Bne_Un_S] = OpCodes.Bne_Un,
        [OpCodes.Leave_S] = OpCodes.Leave,
    };

    /// <summary>
    /// Harmony emits the opcode it is handed, and an inserted guard lengthens the way to a nearby label; the methods are
    /// small enough that the native short branches sit near the 127-byte limit, so all of them become their long forms.
    /// </summary>
    private static List<CodeInstruction> WithLongBranches(List<CodeInstruction> code)
    {
        foreach (CodeInstruction instruction in code)
            if (LongBranches.TryGetValue(instruction.opcode, out OpCode opcode))
                instruction.opcode = opcode;
        return code;
    }

    /// <summary>
    /// Skips the native instructions from <paramref name="start"/> up to (not including) <paramref name="resume"/> when the
    /// method's gate local is true. Labels already targeting <paramref name="start"/> move to the guard, so every native
    /// branch still takes the same path (with the guard in front of it).
    /// </summary>
    private static void SuppressRegion(List<CodeInstruction> code, int start, CodeInstruction resume, LocalBuilder suppress,
        ILGenerator generator)
    {
        System.Reflection.Emit.Label skip = generator.DefineLabel();
        resume.labels.Add(skip);
        var guard = new[]
        {
            new CodeInstruction(OpCodes.Ldloc, suppress),
            new CodeInstruction(OpCodes.Brtrue, skip),
        };
        guard[0].labels.AddRange(code[start].labels);
        code[start].labels.Clear();
        code.InsertRange(start, guard);
    }

    private static List<int> CallsTo(List<CodeInstruction> code, MethodInfo method)
    {
        var calls = new List<int>();
        for (int i = 0; i < code.Count; i++)
            if (code[i].Calls(method))
                calls.Add(i);
        return calls;
    }

    private static bool IsLocalLoad(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S
        || instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1
        || instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;

    private static bool IsBranch(OpCode opcode) =>
        opcode == OpCodes.Br || opcode == OpCodes.Br_S;

    /// <summary>
    /// <c>NPlayerHand.RefreshLayout</c> has exactly two per-holder geometry blocks: the focused snap (angle and scale
    /// instantly, the Y position that centres the hitbox on the card, then its target position) and the fan-out targets
    /// (position, scale and angle). The loop's bookkeeping after them (hitbox filter, focus neighbours and index label) is
    /// never inside a guarded range. Verified anchors, v0.113.1: the method starts with <c>ldarg.0; call get_ActiveHolders</c>;
    /// the focused block starts at <c>ldloc holder; ldc.r4 0; callvirt SetAngleInstantly</c> and ends at its
    /// <c>SetTargetPosition</c>, followed by the <c>br</c> over the fan-out block; the fan-out block starts at the following
    /// <c>ldloc holder</c> and both blocks resume at the instruction after its <c>SetTargetAngle</c>.
    /// </summary>
    private static IEnumerable<CodeInstruction> SuppressRefreshLayoutGeometry(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        MethodInfo gate = HandLayoutGateMethod();
        MethodInfo activeHolders = Required(AccessTools.PropertyGetter(typeof(NPlayerHand), "ActiveHolders"),
            "NPlayerHand.ActiveHolders getter");
        MethodInfo angleInstantly = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetAngleInstantly),
                [typeof(float)]), "NHandCardHolder.SetAngleInstantly(float)");
        MethodInfo scaleInstantly = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetScaleInstantly),
                [typeof(Vector2)]), "NHandCardHolder.SetScaleInstantly(Vector2)");
        MethodInfo setPosition = Required(AccessTools.Method(typeof(Control), "set_Position", [typeof(Vector2)]),
            "Control.set_Position");
        MethodInfo targetPosition = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetPosition),
                [typeof(Vector2)]), "NHandCardHolder.SetTargetPosition(Vector2)");
        MethodInfo targetScale = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetScale),
                [typeof(Vector2)]), "NHandCardHolder.SetTargetScale(Vector2)");
        MethodInfo targetAngle = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetAngle),
                [typeof(float)]), "NHandCardHolder.SetTargetAngle(float)");

        var code = instructions.ToList();
        if (code.Count < 2 || code[0].opcode != OpCodes.Ldarg_0 || !code[1].Calls(activeHolders))
            throw new InvalidOperationException("NPlayerHand.RefreshLayout no longer starts with ActiveHolders.");

        List<int> positions = CallsTo(code, targetPosition);
        List<int> scales = CallsTo(code, targetScale);
        List<int> angles = CallsTo(code, targetAngle);
        List<int> instantAngles = CallsTo(code, angleInstantly);
        List<int> instantScales = CallsTo(code, scaleInstantly);
        List<int> positionSets = CallsTo(code, setPosition);
        if (positions.Count != 2 || scales.Count != 1 || angles.Count != 1
            || instantAngles.Count != 1 || instantScales.Count != 1 || positionSets.Count != 1)
            throw new InvalidOperationException(
                "NPlayerHand.RefreshLayout no longer has one focused snap and one fan-out target per card " +
                $"(positions={positions.Count}, scales={scales.Count}, angles={angles.Count}, " +
                $"instantAngles={instantAngles.Count}, instantScales={instantScales.Count}, positionSets={positionSets.Count}).");

        int focusStart = instantAngles[0] - 2;
        int focusPosition = positions[0];
        int fanPosition = positions[1];
        int fanScale = scales[0];
        int fanAngle = angles[0];
        if (focusStart < 0 || !IsLocalLoad(code[focusStart]) || code[focusStart + 1].opcode != OpCodes.Ldc_R4
            || code[focusStart + 1].operand is not float zero || zero != 0f)
            throw new InvalidOperationException("NPlayerHand.RefreshLayout's focused snap no longer starts with SetAngleInstantly(0).");
        if (!(focusStart < instantScales[0] && instantScales[0] < positionSets[0] && positionSets[0] < focusPosition)
            || !IsLocalLoad(code[focusPosition - 2]) || !IsLocalLoad(code[focusPosition - 1]))
            throw new InvalidOperationException("NPlayerHand.RefreshLayout's focused snap order changed.");
        if (focusPosition + 1 >= code.Count || !IsBranch(code[focusPosition + 1].opcode))
            throw new InvalidOperationException("NPlayerHand.RefreshLayout's focused snap is not followed by the branch over the fan-out targets.");
        int fanStart = focusPosition + 2;
        if (fanPosition != fanStart + 2 || !IsLocalLoad(code[fanStart]) || !IsLocalLoad(code[fanStart + 1]))
            throw new InvalidOperationException("NPlayerHand.RefreshLayout's fan-out targets no longer follow the focused branch.");
        if (!(fanPosition < fanScale && fanScale < fanAngle) || fanAngle + 1 >= code.Count)
            throw new InvalidOperationException("NPlayerHand.RefreshLayout's fan-out target order changed.");

        CodeInstruction join = code[fanAngle + 1];
        LocalBuilder suppress = generator.DeclareLocal(typeof(bool));
        // Insert the later guard first so the focused block's index needs no adjustment.
        SuppressRegion(code, fanStart, join, suppress, generator);
        SuppressRegion(code, focusStart, join, suppress, generator);

        var entry = new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, gate),
            new CodeInstruction(OpCodes.Stloc, suppress),
        };
        entry[0].labels.AddRange(code[0].labels);
        code[0].labels.Clear();
        code.InsertRange(0, entry);
        return WithLongBranches(code);
    }

    /// <summary>
    /// <c>NHandCardHolder.SetDefaultTargets</c> writes ZIndex = 0, resolves the holder's index in the hand and returns when it
    /// is not there, then sets position, angle and scale targets. Only that last block is skipped; ZIndex and the native
    /// early return stay. Verified anchors, v0.113.1: the method starts with <c>ldarg.0; ldc.i4.0; callvirt set_ZIndex</c>;
    /// the geometry block starts at the target of the unique <c>bge</c> that is followed by <c>ret</c> (the
    /// <c>index &gt;= 0</c> guard), its first target call is four instructions in, and the method ends in <c>ret</c>.
    /// </summary>
    private static IEnumerable<CodeInstruction> SuppressDefaultTargetsGeometry(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        MethodInfo gate = HandLayoutGateMethod();
        FieldInfo hand = AccessTools.Field(typeof(NHandCardHolder), "_hand")
            ?? throw new InvalidOperationException("NHandCardHolder._hand not found.");
        MethodInfo zIndex = Required(AccessTools.Method(typeof(CanvasItem), "set_ZIndex", [typeof(int)]),
            "CanvasItem.set_ZIndex");
        MethodInfo targetPosition = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetPosition),
                [typeof(Vector2)]), "NHandCardHolder.SetTargetPosition(Vector2)");
        MethodInfo targetScale = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetScale),
                [typeof(Vector2)]), "NHandCardHolder.SetTargetScale(Vector2)");
        MethodInfo targetAngle = Required(AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetAngle),
                [typeof(float)]), "NHandCardHolder.SetTargetAngle(float)");

        var code = instructions.ToList();
        if (code.Count < 3 || code[0].opcode != OpCodes.Ldarg_0 || code[1].opcode != OpCodes.Ldc_I4_0
            || !code[2].Calls(zIndex))
            throw new InvalidOperationException("NHandCardHolder.SetDefaultTargets no longer starts with ZIndex = 0.");

        int geometryStart = -1;
        for (int i = 0; i + 1 < code.Count; i++)
            if ((code[i].opcode == OpCodes.Bge || code[i].opcode == OpCodes.Bge_S) && code[i + 1].opcode == OpCodes.Ret)
            {
                if (geometryStart >= 0)
                    throw new InvalidOperationException("NHandCardHolder.SetDefaultTargets has more than one index guard.");
                geometryStart = i + 2;
            }
        if (geometryStart < 0 || geometryStart >= code.Count
            || code[geometryStart].opcode != OpCodes.Ldarg_0 || !IsLocalLoad(code[geometryStart + 1])
            || !IsLocalLoad(code[geometryStart + 2]))
            throw new InvalidOperationException("NHandCardHolder.SetDefaultTargets' index guard no longer precedes the target block.");

        List<int> positions = CallsTo(code, targetPosition);
        List<int> scales = CallsTo(code, targetScale);
        List<int> angles = CallsTo(code, targetAngle);
        if (positions.Count != 1 || scales.Count != 1 || angles.Count != 1
            || positions[0] != geometryStart + 4
            || !(positions[0] < angles[0] && angles[0] < scales[0])
            || code.Count == 0 || code[^1].opcode != OpCodes.Ret)
            throw new InvalidOperationException("NHandCardHolder.SetDefaultTargets' position, angle and scale target block changed.");

        LocalBuilder suppress = generator.DeclareLocal(typeof(bool));
        SuppressRegion(code, geometryStart, code[^1], suppress, generator);

        var entry = new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, hand),
            new CodeInstruction(OpCodes.Call, gate),
            new CodeInstruction(OpCodes.Stloc, suppress),
        };
        entry[0].labels.AddRange(code[0].labels);
        code[0].labels.Clear();
        code.InsertRange(0, entry);
        return WithLongBranches(code);
    }

}
