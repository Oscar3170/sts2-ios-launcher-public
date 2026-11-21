using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

internal static partial class PortraitProof
{
    /// <summary>
    /// Captures the Kaiser Crab fight with the portrait placement (the only owner): the skeleton scaled uniformly and
    /// centred so its claws land on the portrait hit areas, inside the frozen stage.
    /// </summary>
    private static async Task RunCrabProofAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);

        Assembly sts2 = typeof(NGame).Assembly;
        object player = await StartProofRunAsync(
            tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, pageTwo: false);

        await EnterEncounterAsync(sts2, tree, player, "KAISER_CRAB_BOSS", 42UL);
        await WaitForCombatBannerAsync(tree);
        LogCrabStep(tree, "crab");
        await WaitForHostStepAckAsync(tree, "crab");
    }

    /// <summary>
    /// Waits until the "Player Turn" / combat-start banner and any FTUE modal have left the screen, then for the stage to
    /// settle, so the Crab capture shows the fight rather than a banner.
    /// </summary>
    private static async Task WaitForCombatBannerAsync(SceneTree tree)
    {
        for (int frame = 0; frame < 300; frame++)
        {
            bool banner = false;
            foreach (Node node in Descendants(tree.Root))
            {
                if (node is not CanvasItem item || !item.IsVisibleInTree())
                    continue;
                string type = node.GetType().Name;
                if (type.Contains("TurnBanner", StringComparison.Ordinal) ||
                    type.Contains("CombatStartBanner", StringComparison.Ordinal) ||
                    type.Contains("CombatRulesFtue", StringComparison.Ordinal))
                {
                    banner = true;
                    break;
                }
            }
            if (!banner)
            {
                await WaitFramesAsync(tree, 45);
                return;
            }
            await WaitFramesAsync(tree, 5);
        }
    }

    private static void LogCrabStep(SceneTree tree, string step)
    {
        Vector2I win = DisplayServer.WindowGetSize();
        Vector2I canvas = tree.Root.ContentScaleSize;
        var creatures = new List<string>();
        string crab = "crab=none";
        foreach (Node node in Descendants(tree.Root))
        {
            string? type = node.GetType().FullName;
            if (type == "MegaCrit.Sts2.Core.Nodes.Combat.NCreature" && node is Control creature)
            {
                creatures.Add($"{creature.Name}@({creature.GlobalPosition.X:F0},{creature.GlobalPosition.Y:F0}) " +
                              $"s={creature.Scale.X:F2} z={creature.ZIndex}");
            }
            else if (type == "MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground" && node is Node2D body)
            {
                Vector2 visualsScale = body.GetNodeOrNull<Node2D>("Visuals")?.Scale ?? Vector2.One;
                crab = $"crab@({body.GlobalPosition.X:F0},{body.GlobalPosition.Y:F0}) s={body.Scale.X:F2} " +
                       $"visualsX={visualsScale.X:F2}";
            }
        }
        GD.Print(
            $"Portrait proof step: {step} window={win.X}x{win.Y} canvas={canvas.X}x{canvas.Y} " +
            $"{crab} creatures=[{string.Join(", ", creatures)}]");
    }
}
