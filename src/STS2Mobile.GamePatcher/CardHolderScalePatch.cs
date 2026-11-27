using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class CardHolderScalePatch
{
    private const string CardHolder = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder";

    // A grid card rests at SmallScale (0.8) and snaps to HoverScale (1.0) under the pointer; its unhover tween returns it to
    // SmallScale. A portrait grid draws its cards at the three-column size instead, and correcting the scale after the game
    // each frame could lose the race to the frame drawn: the phone drew a card shrinking for a frame as the next touch landed.
    // Both getters now first ask CardHolderScaleGate; with no host or a 0 answer (landscape, desktop, any holder outside a
    // portrait grid) the game's IL runs unchanged.
    public static void LetThePortraitGridSizeItsCards(GamePatch patch)
    {
        var gate = patch.Adapter("CardHolderScaleGate", "ScaleFor");
        if (gate.HasThis || gate.Parameters.Count != 1 || gate.Parameters[0].ParameterType.MetadataType != MetadataType.Object ||
            gate.ReturnType.MetadataType != MetadataType.Single)
            throw new InvalidOperationException($"Unexpected card holder scale gate: {gate.FullName}.");
        AskTheGate(patch, patch.Method(CardHolder, "get_SmallScale"), gate);
        AskTheGate(patch, patch.Method(CardHolder, "get_HoverScale"), gate);
    }

    private static void AskTheGate(GamePatch patch, MethodDefinition getter, MethodReference gate)
    {
        var vector = getter.ReturnType;
        if (!getter.HasBody || getter.Body.HasExceptionHandlers || !getter.HasThis || getter.Parameters.Count != 0 ||
            vector.FullName != "Godot.Vector2")
            throw new InvalidOperationException($"Unexpected card holder scale getter: {getter.FullName}.");
        // Vector2(float, float), from the type the game itself names, so no Godot assembly has to be resolved.
        var single = patch.Module.TypeSystem.Single;
        var constructor = new MethodReference(".ctor", patch.Module.TypeSystem.Void, vector) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(single));
        constructor.Parameters.Add(new ParameterDefinition(single));
        var scale = new VariableDefinition(single);
        getter.Body.Variables.Add(scale);
        getter.Body.InitLocals = true;
        var first = getter.Body.Instructions[0];
        var il = getter.Body.GetILProcessor();
        foreach (var instruction in new[]
        {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, gate), il.Create(OpCodes.Stloc, scale),
            il.Create(OpCodes.Ldloc, scale), il.Create(OpCodes.Ldc_R4, 0f), il.Create(OpCodes.Ble_Un, first),
            il.Create(OpCodes.Ldloc, scale), il.Create(OpCodes.Ldloc, scale),
            il.Create(OpCodes.Newobj, patch.Module.ImportReference(constructor)), il.Create(OpCodes.Ret),
        })
            il.InsertBefore(first, instruction);
        patch.Record(getter.FullName, "Let the portrait host give a grid card its column size, resting and hovered.");
    }
}
