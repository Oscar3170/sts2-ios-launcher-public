using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Nodes.Events;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// The stacked-dialogue fixture (L8): six real bubbles, each visible over the stable art, earlier ones dimmed and
    /// re-brightened by the native hover, rewards hidden until the last line, then the last bubble above the options. The
    /// art clip, its crop and the content column are the same native portrait nodes.
    /// </summary>
    private async Task RunAncientDialogueScenarioAsync()
    {
        if (!IsPortraitHost())
        {
            _report.Skip("ancient_dialogue_overlay", "portrait-only layout");
            return;
        }

        NAncientEventLayout layout = await GetAncientLayoutAsync();
        var authored = ((IEnumerable<AncientDialogueLine>)AccessTools.Field(typeof(NAncientEventLayout), "_dialogue")
            .GetValue(layout)!).ToArray();
        _report.Check("ancient_dialogue_uses_native_copy", authored.Length > 0, $"native dialogue lines={authored.Length}");
        if (authored.Length == 0) return;

        // Presentation-only fixture: repeat localized lines with both speakers.
        // This makes a deep stack independently of the native dialogue RNG.
        AncientDialogueLine[] fixture = Enumerable.Range(0, 6).Select(i =>
        {
            AncientDialogueLine source = authored[i % authored.Length];
            return new AncientDialogueLine(source.GetSfxOrFallbackPath())
            {
                LineText = source.LineText,
                NextButtonText = source.NextButtonText ?? source.LineText,
                Speaker = i % 2 == 0 ? AncientDialogueSpeaker.Ancient : AncientDialogueSpeaker.Character
            };
        }).ToArray();
        var choices = layout.OptionButtons.Select(b => b.Option).ToArray();
        layout.ClearDialogue();
        layout.SetDialogue(fixture);
        layout.ClearOptions();
        layout.AddOptions(choices);
        await WaitFramesAsync(4);
        layout.OnSetupComplete();

        var dialogue = layout.GetNode<VBoxContainer>("%DialogueContainer");
        var lines = dialogue.GetChildren().OfType<NAncientDialogueLine>().ToArray();
        var bubbles = lines.Select(l => l.GetNode<Control>("%Bubble")).ToArray();
        var clip = layout.GetNode<Control>("%ContentContainer");
        var hitbox = layout.GetNode<Control>("%DialogueHitbox");
        var art = layout.GetNodeOrNull<Control>(AncientArtNodeName());
        var artScene = layout.GetNode<Control>("%AncientBgContainer");
        _report.Check("ancient_dialogue_art_clipped", art is { ClipContents: true },
            $"art={(art is null ? "missing" : $"{art.GetGlobalRect()} clip={art.ClipContents}")}");
        Rect2 initialArt = (art ?? artScene).GetGlobalRect();
        Transform2D initialCamera = artScene.GetGlobalTransform();
        // The launcher places the painting once and eases only the clip's bottom edge (dialogue height -> rewards height), so
        // its dialogue-height clip is recorded on the first line and compared on the later ones.
        float? dialogueClipHeight = null;
        // Use the uncovered edge of the art for repeat taps. As history
        // fills the stack, its reread hitboxes can cross the art's centre.
        Vector2 advance = new(clip.GetGlobalRect().Position.X - 12f, initialArt.Position.Y + initialArt.Size.Y * 0.20f);
        _report.Check("ancient_dialogue_six_real_bubble_nodes", lines.Length == fixture.Length, $"nodes={lines.Length}");

        for (int i = 0; i < lines.Length; i++)
        {
            bool final = i == lines.Length - 1;
            await WaitSecondsAsync(1.1);
            Rect2 current = bubbles[i].GetGlobalRect();
            Rect2 visibleFrame = clip.GetGlobalRect();
            float overlap = current.Intersection(initialArt).Size.Y;
            _report.Check($"ancient_dialogue_{i + 1}_visible_on_portrait", lines[i].Modulate.A > 0.9f
                && visibleFrame.Grow(2f).Encloses(current) && overlap >= 20f,
                $"bubble={current} overlap={overlap:F0} clip={visibleFrame}");
            bool stable = CheckAncientArtStable(i, lines.Length, art, initialArt, artScene, initialCamera,
                ref dialogueClipHeight, out string stableDetail);
            _report.Check($"ancient_dialogue_{i + 1}_portrait_stable", stable, stableDetail);
            if (!final)
            {
                bool rewardsHidden = layout.OptionButtons.All(b => b.GetGlobalRect().Intersection(visibleFrame).Size.Y <= 1f);
                _report.Check($"ancient_dialogue_{i + 1}_rewards_stay_hidden", hitbox.Visible && rewardsHidden,
                    $"hitbox={hitbox.Visible} rewardsHidden={rewardsHidden}");
            }

            if (i == 0) await CaptureAsync("ancient_dialogue_01_opening");
            if (i == 2)
            {
                Rect2 earlier = bubbles[0].GetGlobalRect();
                bool earlierOnArt = visibleFrame.Grow(2f).Encloses(earlier) && earlier.Intersection(initialArt).Size.Y > 20f;
                _report.Check("ancient_dialogue_history_visible_and_dim", earlierOnArt && lines[0].Modulate.A <= 0.30f,
                    $"first={earlier} alpha={lines[0].Modulate.A:F2}");
                await _pointer.MoveAsync(earlier.GetCenter(), pressed: false);
                await WaitSecondsAsync(0.2);
                _report.Check("ancient_dialogue_history_native_hover", lines[0].Modulate.A > 0.9f && lines[1].Modulate.A > 0.9f,
                    $"alpha={lines[0].Modulate.A:F2}/{lines[1].Modulate.A:F2}");
                await CaptureAsync("ancient_dialogue_02_rereading_three_bubbles");
                await _pointer.MoveAsync(advance, pressed: false);
                await WaitSecondsAsync(0.2);
                _report.Check("ancient_dialogue_history_redims", lines[0].Modulate.A <= 0.30f,
                    $"alpha={lines[0].Modulate.A:F2}");
            }

            if (!final)
            {
                await _pointer.TapAsync(i == 0 ? current.GetCenter() : advance);
                if (i == lines.Length - 2)
                    await WaitUntilAsync(() => !hitbox.Visible, "last dialogue tap reveals ancient rewards", ShortWait);
            }
            else
            {
                float rewardTop = layout.OptionButtons.Min(b => b.GetGlobalRect().Position.Y);
                _report.Check("ancient_dialogue_final_above_rewards", !hitbox.Visible && current.End.Y <= rewardTop + 2f,
                    $"lastBottom={current.End.Y:F0} rewardTop={rewardTop:F0}");
                CheckAncientOptions(layout, "ancient_dialogue_final");
                await CaptureAsync("ancient_dialogue_03_rewards");
            }
        }
    }

    /// <summary>
    /// Whether the ancient painting is still where the setup put it on this dialogue line. The launcher places the painting
    /// once and only eases the clip's bottom edge, so it holds Position and Size.X fixed, holds the camera fixed, and
    /// requires the clip height recorded on line 1 to persist through the dialogue lines (the final line may show the
    /// rewards height). Detail prints top/width/height.
    /// </summary>
    private partial bool CheckAncientArtStable(int index, int count, Control? art, Rect2 initialArt, Control artScene,
        Transform2D initialCamera, ref float? dialogueClipHeight, out string detail);
}
