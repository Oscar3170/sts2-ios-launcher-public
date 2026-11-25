using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace STS2Mobile.GamePatcher;

internal static class OstyPositionPatch
{
    public static void SuppressNativePosition(GamePatch patch)
    {
        var method = patch.Method("MegaCrit.Sts2.Core.Nodes.Combat.NCreature", "ScaleAndPositionOsty");
        var gate = patch.Adapter("OstyPositionGate", "SuppressNativePosition");
        if (!method.HasBody || method.Body.HasExceptionHandlers || !method.HasThis ||
            method.Parameters.Count != 2 || method.Parameters[0].ParameterType.MetadataType != MetadataType.Single ||
            method.Parameters[1].ParameterType.MetadataType != MetadataType.Double || gate.HasThis ||
            gate.Parameters.Count != 1 || gate.Parameters[0].ParameterType.MetadataType != MetadataType.Object ||
            gate.ReturnType.MetadataType != MetadataType.Boolean)
            throw new InvalidOperationException($"Unexpected Osty position signature: {method.FullName}.");

        var code = method.Body.Instructions;
        var local = code.Single(i => Calls(i, "MegaCrit.Sts2.Core.Context.LocalContext", "IsMe"));
        var branch = local.Next;
        var start = branch.Next;
        var parallel = code.Single(i => Calls(i, "Godot.Tween", "Parallel"));
        var position = code.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "position");
        var scale = code.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "scale");
        var callback = code.Single(i => Calls(i, "Godot.Tween", "TweenCallback"));
        var tweens = code.Where(i => Calls(i, "Godot.Tween", "TweenProperty")).ToArray();
        if (branch.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) || branch.Operand is not Instruction resume ||
            start.OpCode != OpCodes.Ldarg_0 || !IsScaleTweenLoad(start.Next) || start.Next.Next != parallel ||
            parallel.Next.OpCode != OpCodes.Ldarg_0 || parallel.Next.Next != position || tweens.Length != 2 ||
            !(scale.Offset < tweens[0].Offset && tweens[0].Offset < local.Offset && position.Offset < tweens[1].Offset) ||
            tweens[1].Next.OpCode != OpCodes.Pop || tweens[1].Next.Next != resume ||
            resume.OpCode != OpCodes.Ldarg_0 || !IsScaleTweenLoad(resume.Next) ||
            !(resume.Offset < callback.Offset) || code.Any(i => i.Operand == start))
            throw new InvalidOperationException($"Osty's position tween no longer joins at its bounds callback: {method.FullName}.");

        // Skip just the local-pet root tween; both branches still grow the visuals and update bounds.
        method.Body.SimplifyMacros();
        var il = method.Body.GetILProcessor();
        il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(start, il.Create(OpCodes.Call, gate));
        il.InsertBefore(start, il.Create(OpCodes.Brtrue, resume));
        method.Body.OptimizeMacros();
        patch.Record(method.FullName, "Keep the placed portrait Osty at his slot while native growth and bounds updates run.");
    }

    private static bool IsScaleTweenLoad(Instruction i) =>
        i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference { Name: "_scaleTween", FieldType.FullName: "Godot.Tween" };

    private static bool Calls(Instruction i, string type, string name) =>
        i.OpCode.Code is Code.Call or Code.Callvirt &&
        i.Operand is MethodReference m && m.Name == name && m.DeclaringType.FullName == type;
}
