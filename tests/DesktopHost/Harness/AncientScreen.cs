using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The Ancient scenarios (L8): advance the native dialogue, inspect the relic options at phone size, sweep them over 2..4
/// choices, walk every act's Ancient art, and check Pael's inline energy icons. The launcher's own checks (art crop and
/// fade into the room cover, settled nameplate, 62.5 pt rows, 37.5 pt icons) live in LauncherAncientChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{

    private async Task RunAncientScenarioAsync()
    {
        NAncientEventLayout layout = await GetAncientLayoutAsync();
        await AdvanceAncientDialogueAsync(layout);
        EventOption[] nativeOptions = layout.OptionButtons.Select(button => button.Option).ToArray();
        _report.Check("ancient_has_relic_choices", nativeOptions.Any(option => option.Relic is not null), $"count={nativeOptions.Length}");
        CheckAncientOptions(layout, "ancient_native");
        Control? art = layout.GetNodeOrNull<Control>(AncientArtNodeName());
        await CheckAncientNameplateAndArtAsync(layout, art);
        await CaptureAsync("ancient_01_options");

        if (IsPortraitHost())
        {
            NEventOptionButton first = layout.OptionButtons.First(button => !button.Option.IsLocked);
            var outline = first.GetNode<NinePatchRect>("Outline");
            await _pointer.MoveAsync(CenterOf(first), pressed: false);
            await WaitSecondsAsync(0.16);
            _report.Check("ancient_native_hover", outline.Modulate.A > 0.1f && first.Scale.X > 1f,
                $"alpha={outline.Modulate.A:F2} scale={first.Scale.X:F3}");
            await CaptureAsync("ancient_02_hover");
            await _pointer.MoveAsync(new Vector2(12f, GetViewportSize().Y * 0.5f), pressed: false);
            await WaitSecondsAsync(0.6);
            _report.Check("ancient_native_unhover", outline.Modulate.A < 0.01f && Mathf.IsEqualApprox(first.Scale.X, 1f),
                $"alpha={outline.Modulate.A:F2} scale={first.Scale.X:F3}");

            Task flash = first.FlashConfirmation();
            await WaitSecondsAsync(0.16);
            var blue = first.GetNode<NinePatchRect>("BlueFlash");
            _report.Check("ancient_native_confirmation_flash", blue.Modulate.A > 0.1f && blue.Scale.Y > 1f,
                $"alpha={blue.Modulate.A:F2} height={blue.Size.Y:F0} scale={blue.Scale.Y:F3}");
            await CaptureAsync("ancient_03_confirm");
            await flash;

            // Isolated presentation fixtures using real relic data; never click a
            // reordered/synthetic list (it doesn't match the event synchronizer).
            Rect2 originalArt = art?.GetGlobalRect() ?? default;
            EventOption[] shortestFirst = nativeOptions.OrderBy(o => o.Description.GetFormattedText().Length).ToArray();
            foreach (float scale in AncientOptionScales())
            {
                SetAncientOptionScale(scale);
                for (int count = 2; count <= 4; count++)
                {
                    EventOption[] fixture = Enumerable.Range(0, count)
                        .Select(i => i == count - 1 ? shortestFirst[^1] : shortestFirst[i % shortestFirst.Length]).ToArray();
                    layout.ClearOptions();
                    layout.AddOptions(fixture);
                    await WaitFramesAsync(4);
                    layout.OnSetupComplete();
                    await AdvanceAncientDialogueAsync(layout);
                    string name = $"ancient_{count}_choices_scale_{(int)(scale * 100)}";
                    _report.Check(name + "_count", layout.OptionButtons.Count() == count, $"count={layout.OptionButtons.Count()}");
                    CheckAncientOptions(layout, name);
                    if (count == 4) await CaptureAsync(name);
                }
            }
            _report.Check("ancient_art_does_not_reframe_on_choice_count", art is not null && art.GetGlobalRect() == originalArt,
                $"art={art?.GetGlobalRect()}");
            layout.ClearOptions();
            layout.AddOptions(nativeOptions);
            await WaitFramesAsync(4);
            layout.OnSetupComplete();
            await AdvanceAncientDialogueAsync(layout);
        }

        NEventOptionButton selected = layout.OptionButtons.First(o => o.Option.Relic is not null && !o.Option.IsLocked
            && o.Option.WillKillPlayer?.Invoke(o.Event.Owner!) != true);
        await _pointer.TapAsync(CenterOf(selected.GetNode<TextureRect>("%RelicIcon")));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(selected) || !selected.IsInsideTree(),
            "native ancient relic selection", MediumWait);
        _report.Pass("ancient_relic_icon_tap_selects_native_reward");
    }

    private async Task RunAncientArtTourAsync(RunState state)
    {
        foreach (string act in new[] { "Overgrowth", "Hive", "Glory" })
        {
            await ConfigureTourActAsync(state, act);
            foreach (var model in state.Act.AllAncients)
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: model, showTransition: false);
                var layout = await GetAncientLayoutAsync();
                await AdvanceAncientDialogueAsync(layout);
                string name = "ancient_art_" + model.GetType().Name;
                var blend = layout.GetNodeOrNull<TextureRect>(AncientArtFadeNodeName());
                if (IsPortraitHost())
                {
                    var backdrop = NEventRoom.Instance!.GetNodeOrNull<TextureRect>(EventRoomCoverNodeName());
                    _report.Check(name + "_blend_matches_room", blend is not null && backdrop is not null
                        && blend.Texture == backdrop.Texture && blend.GetGlobalRect().IsEqualApprox(backdrop.GetGlobalRect()),
                        $"blend={blend?.GetGlobalRect()} backdrop={backdrop?.GetGlobalRect()}");
                }
                else _report.Check(name + "_unchanged_landscape", blend is null, "native backdrop");
                await CaptureAsync(name);
            }
        }
    }

    private async Task RunAncientPaelAsync(RunState state)
    {
        await ConfigureTourActAsync(state, "Hive");
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.AncientEvent<Pael>(), showTransition: false);
        var layout = await GetAncientLayoutAsync();
        Pael model = (Pael)layout.OptionButtons.First().Event;
        if (!layout.OptionButtons.Any(b => b.Option.Relic is PaelsTears))
        {
            // Force the real Tears description into the presentation fixture.
            // Do not select this substituted option through the event synchronizer.
            EventOption tears = model.AllPossibleOptions.Single(o => o.Relic is PaelsTears);
            EventOption[] options = layout.OptionButtons.Take(2).Select(b => b.Option).Append(tears).ToArray();
            layout.ClearOptions();
            layout.AddOptions(options);
            await WaitFramesAsync(4);
            layout.OnSetupComplete();
        }
        await AdvanceAncientDialogueAsync(layout);
        var label = layout.OptionButtons.Single(b => b.Option.Relic is PaelsTears).GetNode<RichTextLabel>("%Text");
        int font = label.GetThemeFontSize("normal_font_size");
        int size = Mathf.RoundToInt(font * 0.85f);
        string tag = IsPortraitHost() ? $"[img={size}x{size}]" : "[img]";
        _report.Check("ancient_pael_energy_icons_follow_native_or_portrait_font", label.Text.Contains(tag + "res://images/packed/sprite_fonts/")
            && label.Text.Contains("_energy_icon.png[/img]"), $"font={font} expected tag={tag} text={label.Text}");
        CheckAncientOptions(layout, "ancient_pael");
        await CaptureAsync("ancient_pael_options");
    }

    private async Task<NAncientEventLayout> GetAncientLayoutAsync()
    {
        if (NEventRoom.Instance?.Layout is not NAncientEventLayout)
            await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.AncientEvent<Tezcatara>(), showTransition: false);
        await WaitUntilAsync(() => NEventRoom.Instance?.Layout is NAncientEventLayout a && a.OptionButtons.Any(),
            "ancient event options", MediumWait);
        return (NAncientEventLayout)NEventRoom.Instance!.Layout!;
    }

    private async Task AdvanceAncientDialogueAsync(NAncientEventLayout layout)
    {
        Control hitbox = layout.GetNode<Control>("%DialogueHitbox");
        for (int i = 0; i < 12 && hitbox.IsVisibleInTree(); i++)
        {
            await _pointer.TapAsync(CenterOf(hitbox));
            await WaitSecondsAsync(0.25);
        }
        await WaitUntilAsync(() => !hitbox.IsVisibleInTree(), "ancient dialogue reveals rewards", ShortWait);
        await WaitSecondsAsync(1.15);
    }

    private void CheckAncientOptions(NAncientEventLayout layout, string name)
    {
        bool portrait = IsPortraitHost();
        Rect2 frame = portrait ? PortraitSafeRect() : new Rect2(Vector2.Zero, GetViewportSize());
        NEventOptionButton[] buttons = layout.OptionButtons.ToArray();
        _report.Check(name + "_fits", buttons.All(b => frame.Grow(1f).Encloses(b.GetGlobalRect())),
            $"buttons={string.Join(", ", buttons.Select(b => b.GetGlobalRect()))} frame={frame}");
        if (!portrait) return;

        float minIcon = AncientRelicIconMinSize();
        foreach (NEventOptionButton button in buttons)
        {
            string id = $"{name}_{button.GetIndex()}";
            var hbox = button.GetNode<HBoxContainer>("HBoxContainer");
            var text = button.GetNode<RichTextLabel>("%Text");
            var icon = button.GetNode<TextureRect>("%RelicIcon");
            var spacer = hbox.GetNode<Control>("Spacer");
            float offered = hbox.Size.X - (icon.Visible ? icon.Size.X + spacer.Size.X : 0f);
            _report.Check(id + "_text_fills_available_width", Math.Abs(text.Size.X - offered) <= 2f
                && text.GetContentHeight() <= text.Size.Y + 1f && text.GetContentWidth() <= text.Size.X + 1f,
                $"title={button.Option.Title.GetFormattedText()} text={text.Size} availableWidth={offered:F0} rendered={text.GetContentWidth()}x{text.GetContentHeight()}");
            if (icon.Visible)
            {
                var iconOutline = icon.GetNode<TextureRect>("%Outline");
                _report.Check(id + "_native_icon_enlarged", icon.Texture is not null && icon.Size.X >= minIcon
                    && Mathf.IsEqualApprox(icon.Size.X, icon.Size.Y) && iconOutline.GetRect().Size.IsEqualApprox(icon.Size),
                    $"icon={icon.Size} outline={iconOutline.Size} button={button.Size} min={minIcon:F0}");
            }
            _report.Check(id + "_effects_follow_button", new[] { "Outline", "RedFlash", "BlueFlash" }.All(path =>
            {
                var effect = button.GetNode<NinePatchRect>(path);
                return effect.GetRect().Encloses(new Rect2(Vector2.Zero, button.Size)) && effect.PivotOffset.IsEqualApprox(effect.Size * 0.5f);
            }), $"button={button.Size}");
            CheckAncientOptionLayout(button, id);
        }
    }

    /// <summary>The settled nameplate and the art reaching the rewards (the launcher's bands).</summary>
    /// <summary>The host's ancient art clip, its fade and the shared room cover, by the host's own names.</summary>
    private partial string AncientArtNodeName();
    private partial string AncientArtFadeNodeName();

    private partial Task CheckAncientNameplateAndArtAsync(NAncientEventLayout layout, Control? art);

    /// <summary>The presentation scales the Ancient fixtures run at; the launcher has one phone layout.</summary>
    private partial IReadOnlyList<float> AncientOptionScales();

    private partial void SetAncientOptionScale(float scale);

    /// <summary>The smallest relic icon side in the launcher's units: 37.5 pt.</summary>
    private partial float AncientRelicIconMinSize();

    /// <summary>The launcher's own row height, font and icon rule.</summary>
    private partial void CheckAncientOptionLayout(NEventOptionButton button, string id);
}
