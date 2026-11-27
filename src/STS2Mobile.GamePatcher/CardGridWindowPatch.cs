using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class CardGridWindowPatch
{
    // NCardGrid recycles its rows from screen coordinates: row 0 below the screen's top means "add a row above" and the last
    // row above the screen's bottom means "add one below". A portrait grid sits between the header and the footer, so both
    // were true at once and the game rebound the same rows back and forth every frame the content moved. The recycler now
    // first asks CardGridWindowGate, and the portrait host keeps the window from the grid's own view instead; with no host
    // (landscape, desktop) the game's IL runs unchanged.
    public static void LetThePhoneKeepTheRowWindow(GamePatch patch)
    {
        var method = patch.Method("MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid", "AllocateCardHolders");
        var gate = patch.Adapter("CardGridWindowGate", "KeepsWindow");
        if (!method.HasBody || method.Body.HasExceptionHandlers || !method.HasThis || method.Parameters.Count != 0 ||
            method.ReturnType.MetadataType != MetadataType.Void || gate.HasThis || gate.Parameters.Count != 1 ||
            gate.Parameters[0].ParameterType.MetadataType != MetadataType.Object || gate.ReturnType.MetadataType != MetadataType.Boolean)
            throw new InvalidOperationException($"Unexpected card grid recycler signature: {method.FullName}.");
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, gate));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        patch.Record(method.FullName, "Let the portrait host keep a card grid's rows on the cards its view shows.");
    }
}
