using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace STS2Mobile.GamePatcher;

/// <summary>
/// Lets the portrait host own where the hand's cards go without taking over the game's hand bookkeeping. The native
/// <c>NPlayerHand.RefreshLayout</c> still runs every loop to set each hitbox's mouse filter, the focus neighbours and the
/// card numbers, and <c>NHandCardHolder.SetDefaultTargets</c> still clears the holder's ZIndex; only the target transforms
/// of the desktop fan are skipped, and only when <c>HandLayoutGate</c> says the host can write them. A build whose two
/// methods do not have the tested shape is left exactly as the game ships it.
/// </summary>
internal static class HandLayoutPatches
{
    private const string HandType = "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand";
    private const string HolderType = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder";

    public static void SuppressNativeTargets(GamePatch patch)
    {
        var refresh = patch.Method(HandType, "RefreshLayout");
        var defaults = patch.Method(HolderType, "SetDefaultTargets");
        Require(refresh, refresh.HasBody && !refresh.Body.HasExceptionHandlers && !refresh.HasParameters, "unexpected signature");
        Require(defaults, defaults.HasBody && !defaults.Body.HasExceptionHandlers && !defaults.HasParameters, "unexpected signature");

        // Both branches of RefreshLayout's geometry converge at the holder's hitbox. Everything after that
        // join (mouse filter, navigation and index labels) must run in both modes.
        var positions = Calls(refresh, "SetTargetPosition", 2);
        var instantAngle = Calls(refresh, "SetAngleInstantly", 1)[0];
        var instantScale = Calls(refresh, "SetScaleInstantly", 1)[0];
        var move = Calls(refresh, "set_Position", 1)[0];
        var scale = Calls(refresh, "SetTargetScale", 1)[0];
        var angle = Calls(refresh, "SetTargetAngle", 1)[0];
        var focus = instantAngle.Previous?.Previous;
        var fan = positions[0].Next?.Next;
        var join = angle.Next;
        Require(refresh, focus != null && fan != null && join != null &&
            focus.Previous?.Operand == fan && positions[0].Next?.Operand == join &&
            instantAngle.Offset < instantScale.Offset && instantScale.Offset < move.Offset && move.Offset < positions[0].Offset &&
            fan.Next?.Next == positions[1] && positions[1].Offset < scale.Offset && scale.Offset < angle.Offset &&
            IsCall(join.Next, "get_Hitbox"), "focused and ordinary geometry no longer join at the hitbox");

        // The return path clears ZIndex first, then finds the holder's index and writes its desktop targets.
        var code = defaults.Body.Instructions;
        var position = Calls(defaults, "SetTargetPosition", 1)[0];
        var returnAngle = Calls(defaults, "SetTargetAngle", 1)[0];
        var returnScale = Calls(defaults, "SetTargetScale", 1)[0];
        var findIndex = Calls(defaults, "IndexOf", 1)[0];
        var cardIndexGuard = code.SingleOrDefault(i => (i.OpCode.Code is Code.Bge or Code.Bge_S) && i.Next?.OpCode == OpCodes.Ret);
        var returnStart = cardIndexGuard?.Operand as Instruction;
        var finish = code[^1];
        Require(defaults, code.Count > 5 && code[0].OpCode == OpCodes.Ldarg_0 && code[1].OpCode == OpCodes.Ldc_I4_0 &&
            IsCall(code[2], "set_ZIndex") && code[4].OpCode == OpCodes.Ldfld &&
            code[4].Operand is FieldReference { Name: "_hand" } hand && hand.FieldType.FullName == HandType &&
            returnStart != null && findIndex.Offset < returnStart.Offset && returnStart.Offset < position.Offset &&
            position.Offset < returnAngle.Offset && returnAngle.Offset < returnScale.Offset &&
            returnScale.Next == finish && finish.OpCode == OpCodes.Ret, "unexpected default target block or ZIndex setup");

        // Preflight both seams before writing either one: this step is optional even on tested builds.
        var gate = patch.Adapter("HandLayoutGate", "SuppressNativeTargets");
        Require(refresh, !gate.HasThis && gate.Parameters.Count == 1 &&
            gate.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
            gate.ReturnType.MetadataType == MetadataType.Boolean, "unexpected HandLayoutGate signature");
        InsertGuards(refresh, gate, null, join!, focus!, fan!);
        InsertGuards(defaults, gate, (FieldReference)code[4].Operand, finish, returnStart!);
        patch.Record(refresh.FullName, "Skip desktop geometry while portrait owns the hand's targets.");
        patch.Record(defaults.FullName, "Keep portrait geometry on return without removing the native ZIndex reset.");
    }

    private static void InsertGuards(MethodDefinition method, MethodReference gate, FieldReference? hand,
        Instruction resume, params Instruction[] sites)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        var suppress = new VariableDefinition(method.Module.TypeSystem.Boolean);
        body.Variables.Add(suppress);
        var entry = body.Instructions[0];
        il.InsertBefore(entry, il.Create(OpCodes.Ldarg_0));
        if (hand != null) il.InsertBefore(entry, il.Create(OpCodes.Ldfld, hand));
        il.InsertBefore(entry, il.Create(OpCodes.Call, gate));
        il.InsertBefore(entry, il.Create(OpCodes.Stloc, suppress));
        foreach (var site in sites)
        {
            var load = il.Create(OpCodes.Ldloc, suppress);
            foreach (var instruction in body.Instructions)
            {
                if (instruction.Operand == site) instruction.Operand = load;
                else if (instruction.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++)
                        if (targets[i] == site) targets[i] = load;
            }
            il.InsertBefore(site, load);
            il.InsertBefore(site, il.Create(OpCodes.Brtrue, resume));
        }
        body.OptimizeMacros();
    }

    private static bool IsCall(Instruction? instruction, string name) =>
        instruction?.OpCode.Code is Code.Call or Code.Callvirt &&
        instruction.Operand is MethodReference method && method.Name == name;

    private static Instruction[] Calls(MethodDefinition method, string name, int expected)
    {
        var calls = method.Body.Instructions.Where(i => IsCall(i, name)).ToArray();
        Require(method, calls.Length == expected, $"expected {expected} {name} call(s), found {calls.Length}");
        return calls;
    }

    private static void Require(MethodDefinition method, bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException($"{method.FullName}: {reason}.");
    }
}
