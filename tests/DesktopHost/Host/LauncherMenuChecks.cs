using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the shared menu/settings/pause/game-over scenarios, with the launcher's thresholds:
/// 127 units are 44 points on the 1080 unit canvas (<see cref="PortraitBands.MinTarget"/>), numbers from the original
/// 1680-wide reference canvas are converted by 1080/1680 = 0.643, and a control's target is its drawn rectangle merged with
/// the launcher's larger <see cref="PhoneHitArea"/>.
/// </summary>
internal sealed partial class TestDriver
{
    private partial float HostTouchTargetMinimum() => PortraitBands.MinTarget;

    private partial Rect2 HostTouchTargetBox(Control control)
    {
        Rect2 rect = control.GetGlobalRect();
        if (PhoneHitArea.Of(control) is { } area && area.IsVisibleInTree()) rect = rect.Merge(area.GetGlobalRect());
        return rect;
    }

    // No settings exemptions on this host: its settings controls (paginator arrows, dropdowns) are gated at 127 units like
    // any other touch target.
    private partial string[] HostSettingsGateAllowlist() => Array.Empty<string>();

    /// <summary>
    /// T5 on this host means every interactive control has a 44 point target, so the gate's native-size exemptions for back
    /// buttons, gold arrows and dropdown rows are dropped: those are padded to 127 units and measured, and fail until they are.
    /// Only the game over summary's award chips keep their shared exemption (their pads are checked by
    /// <c>game_over_summary_chip_targets</c>).
    /// </summary>
    private partial bool HostAllowsTouchTargetExemption(string entry) =>
        entry != NativeBackAllow && entry != NativeArrowAllow && entry != DropdownRowsAllow;

    // ---- menus ---------------------------------------------------------------------------------------

    private partial void CheckQuitPopupLayout(NMainMenu menu, Control popup)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        Rect2 plate = popup.GetGlobalRect();
        Control noButton = popup.GetNode<Control>("NoButton");
        Control yesButton = popup.GetNode<Control>("YesButton");
        Rect2 no = noButton.GetGlobalRect();
        Rect2 yes = yesButton.GetGlobalRect();
        Rect2 noTarget = HostTouchTargetBox(noButton), yesTarget = HostTouchTargetBox(yesButton);
        // The plate is drawn 1.9157x and the buttons only 1.745x, so the buttons are padded to the 44 point target; judge what
        // a finger can reach (drawn rect merged with the pad), and that both targets are on screen and do not overlap. The
        // plate's own drawn box is what must stay inside the frame; it is not required to be pixel-centred (its scale limit
        // comes from the width).
        // An off-centre popup is a visual defect even when every target fits, so the drawn plate must sit on the canvas centre
        // (the scale limit leaves a little slack on each side); the tolerance is 8 u, not the 2 u used for exact centring.
        _report.Check("menu_quit_portrait_popup", safe.Encloses(plate)
            && Mathf.Abs(plate.GetCenter().X - safe.GetCenter().X) <= 8f
            && noTarget.Size.Y >= PortraitBands.MinTarget - 0.5f
            && yesTarget.Size.Y >= PortraitBands.MinTarget - 0.5f
            && safe.Encloses(noTarget) && safe.Encloses(yesTarget)
            && !yes.Intersects(no),
            $"plate={plate} centreDeltaX={plate.GetCenter().X - safe.GetCenter().X:F1} no={no} target={noTarget} " +
            $"yes={yes} target={yesTarget} safe={safe}");
    }

    private partial void CheckModeCardsLayout(Control modes, Control[] cards)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        _report.Check("menu_mode_cards_fit", cards.Length >= 4 && cards.All(card => safe.Encloses(card.GetGlobalRect()))
            && cards.Select(card => Mathf.RoundToInt(card.GetGlobalRect().GetCenter().Y)).Distinct().Count() >= 2,
            string.Join(", ", cards.Select(card => $"{card.Name}={card.GetGlobalRect()}")));
        // Up to three cards a line, the lines balanced (5 = 3 + 2, 4 = 2 + 2), in the native order, and a full line spans
        // most of the safe width instead of leaving wide empty sides.
        var lines = cards.GroupBy(card => Mathf.RoundToInt(card.GetGlobalRect().GetCenter().Y)).OrderBy(line => line.Key)
            .Select(line => line.OrderBy(card => card.GetGlobalRect().Position.X).ToArray()).ToArray();
        int expectedLines = (cards.Length + 2) / 3;
        bool inOrder = lines.SelectMany(line => line).Select(card => card.GetIndex())
            .SequenceEqual(cards.Select(card => card.GetIndex()).OrderBy(i => i));
        float widest = lines.Max(line => line[^1].GetGlobalRect().End.X - line[0].GetGlobalRect().Position.X);
        _report.Check("menu_mode_cards_three_a_line", lines.Length == expectedLines
            && lines.All(line => line.Length <= 3) && lines.Max(l => l.Length) - lines.Min(l => l.Length) <= 1
            && inOrder && widest >= safe.Size.X * 0.85f,
            $"lines={string.Join("+", lines.Select(l => l.Length))} expected={expectedLines} inOrder={inOrder} " +
            $"widest={widest:F0} safeWidth={safe.Size.X:F0}");
    }

    private partial void CheckModeSetupLayout(string mode, Control setup, IReadOnlyList<NCharacterSelectButton> roster)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // Two rows of character cards, each card a 44 point target across (190 units on the original 1680-wide reference
        // canvas are 122 here; the launcher's standard is 127).
        _report.Check($"menu_{mode}_portrait_roster", roster.Count > 1
            && roster.All(b => HostTouchTargetBox(b).Size.X >= PortraitBands.MinTarget - 0.5f)
            && roster.Select(b => Mathf.RoundToInt(b.GetGlobalRect().GetCenter().Y)).Distinct().Count() == 2,
            string.Join(", ", roster.Select(b => $"{b.Name}={b.GetGlobalRect()}")));
        if (mode != "Blitz") return;
        var title = setup.GetNode<RichTextLabel>("BlitzPlaceholder");
        var badge = setup.GetNode<Control>("%UnderConstruction");
        Vector2 inkTop = (title.Size - new Vector2(title.GetContentWidth(), title.GetContentHeight())) * 0.5f;
        float titleTop = (title.GetGlobalTransform() * inkTop).Y;
        float safeTop = PortraitSafeRect().Position.Y;
        _report.Check("menu_blitz_title_and_badge_below_safe_top", titleTop >= safeTop
            && (!badge.IsVisibleInTree() || badge.GetGlobalRect().Position.Y >= safeTop),
            $"title={titleTop:F1}, badge={badge.GetGlobalRect().Position.Y:F1}, safeTop={safeTop:F1}");
    }

    private partial void CheckCharacterGrid(Control characters, IReadOnlyList<NCharacterSelectButton> buttons, string suffix)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        var rows = buttons.GroupBy(b => Mathf.RoundToInt(b.GetGlobalRect().GetCenter().Y)).ToArray();
        _report.Check("menu_character_two_rows" + suffix, rows.Length == 2 && Math.Abs(rows[0].Count() - rows[1].Count()) <= 1,
            string.Join(", ", rows.Select(row => $"Y={row.Key}: {row.Count()}")));
        var ascension = characters.GetNode<Control>("%AscensionPanel");
        Control icon = ascension.GetNode<Control>("HBoxContainer/AscensionContainer/AscensionIconStandard");
        Control number = ascension.GetNode<Control>("HBoxContainer/AscensionContainer/AscensionLevel");
        _report.Check("menu_ascension_number_on_flame" + suffix,
            Mathf.Abs(icon.GetGlobalRect().GetCenter().X - number.GetGlobalRect().GetCenter().X) < 1f
                && icon.GetGlobalRect().HasPoint(number.GetGlobalRect().GetCenter()),
            $"number={number.GetGlobalRect().GetCenter()} flame={icon.GetGlobalRect()}");
        Rect2 safe = PortraitSafeRect();
        // The original 54 point card width: the same 54 points at the launcher's 2.88 units per point.
        float minimumWidth = PortraitBands.MinTarget * (54f / 44f);
        _report.Check("menu_character_phone_targets" + suffix, buttons.All(b => HostTouchTargetBox(b).Size.X >= minimumWidth),
            $"minimum={minimumWidth:F0}, targets={string.Join(", ", buttons.Select(b => b.GetGlobalRect().Size.X))}");
        _report.Check("menu_character_compact_row_spacing" + suffix, rows.All(row =>
        {
            Rect2[] ordered = row.Select(b => b.GetGlobalRect()).OrderBy(rect => rect.Position.X).ToArray();
            return ordered.Zip(ordered.Skip(1)).All(pair =>
                pair.Second.Position.X - pair.First.End.X <= minimumWidth * 0.29f);
        }), "adjacent buttons use the available space without overlapping");
        var ascensionArt = ascension.GetNode<Control>("Background");
        Control confirm = characters.GetNode<Control>("ConfirmButton");
        _report.Check("menu_character_confirm_touch_target" + suffix,
            HostTouchTargetBox(confirm).Size.Y >= PortraitBands.MinTarget - 0.5f && safe.Encloses(confirm.GetGlobalRect()),
            $"confirm={confirm.GetGlobalRect()} target={HostTouchTargetBox(confirm)}");
        Rect2 infoArt = characters.GetNode<Control>("InfoPanel/NinePatchRect").GetGlobalRect();
        _report.Check("menu_character_info_centered" + suffix, safe.Encloses(infoArt)
            && Mathf.Abs(infoArt.GetCenter().X - safe.GetCenter().X) < 2f, $"plate={infoArt} safe={safe}");
        _report.Check("menu_character_info_clear_of_ascension" + suffix,
            infoArt.End.Y <= ascensionArt.GetGlobalRect().Position.Y + 1f,
            $"info ends at {infoArt.End.Y:F0}; ascension starts at {ascensionArt.GetGlobalRect().Position.Y:F0}");
        Control[] adjacent = { ascensionArt, characters.GetNode<Control>("BackButton"), confirm };
        _report.Check("menu_character_grid_clear_of_navigation" + suffix, buttons.All(button =>
            adjacent.All(control => !control.GetGlobalRect().Intersects(button.GetGlobalRect()))),
            $"ascension art ends at {ascensionArt.GetGlobalRect().End.Y:F0}; grid starts at {buttons.Min(b => b.GetGlobalRect().Position.Y):F0}");
        _report.Check("menu_character_cross_row_focus" + suffix, rows.Length == 2 && rows[0].All(button =>
            rows[1].Contains(button.GetNodeOrNull<NCharacterSelectButton>(button.FocusNeighborBottom))),
            "Down from the first row focuses a native button in the second row");
    }

    private partial Func<bool>? CharacterArtDrawnUnframed(Control characters)
    {
        if (!SafeAreaCanvas.IsPortrait) return null;
        Control container = characters.GetNode<Control>("AnimatedBg");
        // A drawn frame is bad when the art has no portrait frame yet, when the art is hidden (a blank background), or when its
        // frame moved after it was first drawn framed (a figure that jumps on the second frame is the same flicker).
        var firstDrawn = new Dictionary<ulong, (Variant Art, Variant Figure)>();
        return () => container.GetChildren().OfType<Control>().Any(art =>
        {
            if (art.IsQueuedForDeletion()) return false;
            if (!art.IsVisibleInTree()) return container.IsVisibleInTree();
            if (!art.HasMeta("phone_character_art_frame")) return true;
            var now = (art.GetMeta("phone_character_art_frame"), art.GetMeta("phone_character_figure_frame", new Rect2()));
            if (!firstDrawn.TryGetValue(art.GetInstanceId(), out var first))
            {
                firstDrawn[art.GetInstanceId()] = now;
                return false;
            }
            return !first.Art.AsRect2().IsEqualApprox(now.Item1.AsRect2()) || !first.Figure.AsRect2().IsEqualApprox(now.Item2.AsRect2());
        });
    }

    private partial async Task CheckCharacterArtAsync(Control characters, string name, bool isRandom)
    {
        Control background = characters.GetNode<Control>("AnimatedBg").GetChildren().OfType<Control>().Single();
        Control art = FindAll<Control>(characters).FirstOrDefault(c => c.HasMeta("phone_character_art_frame")) ?? background;
        bool portrait = SafeAreaCanvas.IsPortrait;
        _report.Check("menu_character_native_landscape_" + name,
            portrait || !art.HasMeta("phone_character_art_frame"), "portrait framing must not run in landscape");
        if (!portrait) return;
        Vector2 viewport = _tree.Root.GetVisibleRect().Size;
        Rect2 frame = art.GetMeta("phone_character_art_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_art_cover_" + name,
            frame.Grow(1).Encloses(new Rect2(Vector2.Zero, viewport)), frame.ToString());
        var ambient = background.FindChildren("*", "CPUParticles2D", true, false).OfType<CpuParticles2D>()
            .Where(p => p.EmissionShape == CpuParticles2D.EmissionShapeEnum.Rectangle).ToArray();
        _report.Check("menu_character_particles_at_top_" + name,
            ambient.Any(p => p.Emitting && new[] { Vector2.Zero, new Vector2(viewport.X, 0f) }.All(corner =>
            {
                Vector2 local = p.ToLocal(corner).Abs();
                return local.X <= p.EmissionRectExtents.X && local.Y <= p.EmissionRectExtents.Y;
            })), $"live ambient emitter covers both top corners (found {ambient.Length})");
        // The backdrop must actually draw under the figure at full-canvas cover size: the split masks the original
        // playback to the scenery slots, so the named backdrop attachment has to remain attached and the cover fill the canvas.
        string? backdropSlot = name switch
        {
            "Ironclad" => "bg",
            "Regent" => "Layer 5",
            "Defect" => "backgroundbottom",
            _ => null,
        };
        if (backdropSlot is not null)
        {
            // The split keeps the scenery on the original playback and masks it on the figure: any playback that keeps the
            // named backdrop attachment proves the backdrop still draws.
            bool attached = false;
            foreach (Node2D candidate in FindAll<Node2D>(art).Where(n => n.IsClass("SpineSprite")))
            {
                if (candidate.Call("get_skeleton").AsGodotObject() is not { } skeleton) continue;
                foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
                {
                    if (value.AsGodotObject() is not { } slot) continue;
                    string slotName = slot.Call("get_data").AsGodotObject()?.Call("get_attachment_name").AsString() ?? "";
                    if (slotName != backdropSlot) continue;
                    attached = slot.Call("get_attachment").AsGodotObject() is not null;
                    break;
                }
                if (attached) break;
            }
            _report.Check("menu_character_backdrop_drawn_" + name,
                attached && frame.Grow(1f).Encloses(new Rect2(Vector2.Zero, viewport)),
                $"slot={backdropSlot} attached={attached} art={frame} viewport={viewport}");
        }
        if (isRandom) return;
        if (!art.HasMeta("phone_character_figure_frame"))
        {
            _report.Pass("menu_character_independent_figure_" + name,
                $"single-sprite figure-biased cover: no separate figure rect (backdrop={frame})");
            return;
        }
        Rect2 figure = art.GetMeta("phone_character_figure_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_independent_figure_" + name,
            figure.Size.Y > 0 && figure.Size.Y < viewport.Y * 0.65f && figure.Size.X < frame.Size.X,
            $"figure={figure} backdrop={frame}");
        Node2D? spine = characters.GetNodeOrNull<Node2D>("AnimatedBg/SpineSprite")
            ?? FindAll<Node2D>(characters).FirstOrDefault(n => n.GetType().Name.Contains("SpineSprite", StringComparison.Ordinal));
        static double TrackTime(Node2D? sprite)
        {
            GodotObject? track = sprite?.Call("get_animation_state").AsGodotObject()?.Call("get_current", 0).AsGodotObject();
            return track?.HasMethod("get_track_time") == true ? track.Call("get_track_time").AsDouble() : -1.0;
        }
        double before = TrackTime(spine);
        await WaitSecondsAsync(0.25);
        double after = TrackTime(spine);
        if (before < 0) _report.Skip("menu_character_live_spine_" + name, "no native spine node found under character select");
        else _report.Check("menu_character_live_spine_" + name, after > before, $"native track {before:F2} → {after:F2}");
        // Framing is applied once when the art is ready: the frames must not move after selection.
        Rect2 artBefore = frame, figureBefore = figure;
        await WaitFramesAsync(30);
        Rect2 artAfter = art.GetMeta("phone_character_art_frame", new Rect2()).AsRect2();
        Rect2 figureAfter = art.GetMeta("phone_character_figure_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_stable_" + name,
            artBefore.IsEqualApprox(artAfter) && figureBefore.IsEqualApprox(figureAfter),
            $"art {artBefore} -> {artAfter}; figure {figureBefore} -> {figureAfter}");
        Vector2 focus = figure.GetCenter();
        // The figure's centre is not its face: on compact portrait its torso sits behind the info plate.
        _report.Check("menu_character_figure_in_art_band_" + name,
            figure.Position.Y >= -1f && focus.Y >= viewport.Y * 0.30f && focus.Y <= viewport.Y * 0.47f
            && new Rect2(Vector2.Zero, viewport).HasPoint(focus) && frame.Grow(1f).HasPoint(focus),
            $"focus={focus} figure={figure} viewport={viewport} art={frame}");
    }

    // ---- menu-hover ----------------------------------------------------------------------------------

    private partial async Task CheckModConfigOutlineAsync(NMainMenu menu)
    {
        string modPath = "MainMenuTextButtons/ModConfigButton/Label";
        if (!SafeAreaCanvas.IsPortrait) return;
        if (menu.GetNodeOrNull<Label>(modPath) is not { } mods) return;
        Label settings = menu.GetNode<Label>("MainMenuTextButtons/SettingsButton/Label");
        int FontSize(Label label) => label.LabelSettings?.FontSize ?? label.GetThemeFontSize("font_size");
        _report.Check("mod_config_matches_settings_font_size", FontSize(mods) == FontSize(settings),
            $"settings={FontSize(settings)} mod config={FontSize(mods)}");
        int Outline(Label label) => label.LabelSettings?.OutlineSize ?? label.GetThemeConstant("outline_size");
        Color OutlineColor(Label label) => label.LabelSettings?.OutlineColor ?? label.GetThemeColor("font_outline_color");
        _report.Check("mod_config_matches_settings_outline", Outline(mods) == Outline(settings)
            && Outline(mods) > 0 && OutlineColor(mods).IsEqualApprox(OutlineColor(settings)),
            $"settings={Outline(settings)} {OutlineColor(settings)} mod config={Outline(mods)} {OutlineColor(mods)}");
        await CaptureAsync("menu_mod_config_outline");
    }

    private partial void CheckMenuButtonHover(NMainMenu menu, Control button, string name)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Label label = button.GetChild<Label>(0);
        Control left = menu.GetNode<Control>("%ButtonReticleLeft");
        Control right = menu.GetNode<Control>("%ButtonReticleRight");
        Rect2 safe = PortraitSafeRect();
        Rect2 text = label.GetGlobalRect();
        _report.Check(name + "_native_outline", label.GetThemeConstant("outline_size") >= label.GetThemeFontSize("font_size") * 0.5f
            && label.GetThemeColor("font_outline_color").R < 0.01f, $"font={label.GetThemeFontSize("font_size")} outline={label.GetThemeConstant("outline_size")}");
        _report.Check(name + "_carets_visible", left.Modulate.A > 0.9f && right.Modulate.A > 0.9f, "native hover is active");
        // The original 24 unit tolerance is 15 on the 1080 canvas.
        _report.Check(name + "_carets_aligned", Math.Abs(left.GetGlobalRect().GetCenter().Y - text.GetCenter().Y) < 15f
            && Math.Abs(right.GetGlobalRect().GetCenter().Y - text.GetCenter().Y) < 15f,
            $"left={left.GetGlobalRect()} right={right.GetGlobalRect()} text={text}");
        _report.Check(name + "_carets_in_frame", safe.Encloses(left.GetGlobalRect()) && safe.Encloses(right.GetGlobalRect()),
            "native carets stay inside the phone frame");
        // The original 80 unit caret width is 51 here.
        _report.Check(name + "_carets_sized", left.Size.X >= 51f && right.Size.X >= 51f, $"left={left.Size} right={right.Size}");
        if (name != "ContinueButton") return;
        var info = menu.ContinueRunInfo;
        _report.Check("continue_info_visible", info.HasResult && info.Modulate.A > 0.9f, "saved-run information opens on hover");
        _report.Check("continue_info_in_frame", safe.Encloses(info.GetGlobalRect()), $"info={info.GetGlobalRect()} safe={safe}");
        _report.Check("continue_info_above_buttons", info.GetGlobalRect().End.Y < button.GetGlobalRect().Position.Y, "native information does not cover menu targets");
        // The original 2.5 is 1.607 on the 1080 canvas (contract decision 1).
        _report.Check("continue_info_readable_scale", info.Scale.X >= 1.607f, $"scale={info.Scale}");
    }

    // ---- menu-utilities ------------------------------------------------------------------------------

    private partial async Task CheckMenuUtilitiesLayoutAsync(NMainMenu menu, Control profile, Control profileRow, Control notes, Label modded)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("menu_utilities_landscape_native", profileRow.Scale.IsEqualApprox(Vector2.One)
                && notes.Size.X < 100f && modded.GetThemeFontSize("font_size") < 32,
                $"profile content scale={profileRow.Scale} notes={notes.Size} mod font={modded.GetThemeFontSize("font_size")}");
            return;
        }
        Rect2 safe = PortraitSafeRect();
        float minimum = PortraitBands.MinTarget;
        Rect2 profileRect = HostTouchTargetBox(profile), notesRect = HostTouchTargetBox(notes);
        _report.Check("menu_utilities_phone_targets", safe.Encloses(profileRect) && safe.Encloses(notesRect)
            && profileRect.Size.Y >= minimum - 0.5f && notesRect.Size.Y >= minimum - 0.5f && notesRect.Size.X >= minimum - 0.5f
            && !profileRect.Intersects(notesRect) && profileRect.Grow(1f).Encloses(profileRow.GetGlobalRect()),
            $"profile={profileRect} drawn={profile.GetGlobalRect()} contents={profileRow.GetGlobalRect()} notes={notesRect} minimum={minimum:F0}");
        // Resting state: the row is kept at the game's 1x size (its 11 point labels come from the reflow, not a permanent
        // enlargement); the native hover enlarges it only while the pointer is on it.
        _report.Check("menu_profile_row_resting_scale", profileRow.Scale.IsEqualApprox(Vector2.One),
            $"content={profileRow.Scale}");
        var buttons = menu.GetNode<Control>("%MainMenuTextButtons").GetChildren().OfType<Control>()
            .Where(c => c.IsVisibleInTree()).ToArray();
        var before = buttons.Select(c => c.GetGlobalRect()).ToArray();
        modded.Hide();
        menu.RefreshButtons();
        await WaitFramesAsync(2);
        _report.Check("menu_mod_status_does_not_reflow_buttons", before.SequenceEqual(buttons.Select(c => c.GetGlobalRect())),
            "button rectangles are identical with and without the mod status");
        modded.Show();
        Rect2 status = modded.GetGlobalRect();
        _report.Check("menu_mod_status_small_bottom_right", modded.IsVisibleInTree() && safe.Encloses(status)
            && status.GetCenter().X > safe.GetCenter().X && safe.End.Y - status.End.Y <= 40f,
            $"status={status} font={modded.GetThemeFontSize("font_size")} safe={safe}");
        // Contract decision 1: the mod status is a 44 point target in the reflowed utility corner.
        _report.Check("menu_mod_status_touch_target", HostTouchTargetBox(modded).Size.Y >= minimum - 0.5f,
            $"target={HostTouchTargetBox(modded)} drawn={status}");
    }

    private partial void CheckMenuProfileHover(Control profile, Control profileRow)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // The row itself stays at the game's 1x size with its 11 point labels (so it cannot overlap the menu); only the
        // button's own native hover growth applies.
        _report.Check("menu_profile_native_hover_retains_size", profile.Scale.X > 1.01f
            && profileRow.Scale.IsEqualApprox(Vector2.One),
            $"hover={profile.Scale} content={profileRow.Scale}");
    }

    private partial void CheckMenuModDetails(Control details, Label modded)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _report.Check("menu_mod_details_above_status", PortraitSafeRect().Encloses(details.GetGlobalRect())
            && details.GetGlobalRect().End.Y <= modded.GlobalPosition.Y,
            $"details={details.GetGlobalRect()} status={modded.GetGlobalRect()}");
    }

    // ---- settings ------------------------------------------------------------------------------------

    private partial void CheckSettingsLanguageListLayout(NSettingsScreen settings, NDropdownContainer list)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        Rect2 rect = list.GetGlobalRect();
        _report.Check("settings_language_list_safe", safe.Grow(1).Encloses(rect), rect.ToString());
        // The original 600 unit list cap is 386 here.
        _report.Check("settings_language_list_capped", rect.Size.Y <= 386f + 2f, $"height={rect.Size.Y:F0} cap=386");
    }

    private partial Vector2 SettingsDismissPoint() => new(20, PortraitSafeRect().End.Y - 300f * (1080f / 1680f));

    private partial void CheckSettingsInputLayout(NSettingsScreen settings, Control input)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach (Control column in input.GetNode<Control>("VBoxContainer/Header").GetChildren().OfType<Control>())
            _report.Check("settings_input_column_safe_" + column.Name, PortraitSafeRect().Grow(1).Encloses(column.GetGlobalRect()),
                column.GetGlobalRect().ToString());
    }

    private partial void CheckSettingsReopenStable(NSettingsScreen settings, Control general)
    {
        // The launcher scales the settings page itself, `ScrollContainer/Mask/Clipper/GeneralSettings` (1.55); its
        // VBoxContainer stays at 1 and inherits that scale, so the page is the node that proves the layout was neither
        // stacked nor reset on reopen. Landscape is the game's own desktop layout: there the page must be back at its
        // native 1x scale, so no portrait scale was left behind.
        Vector2 scale = general.Scale;
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("settings_reopen_scale_stable", scale.IsEqualApprox(Vector2.One),
                $"landscape page scale={scale} (1, native)");
            return;
        }
        _report.Check("settings_reopen_scale_stable", Mathf.Abs(scale.X - 1.55f) <= 0.01f && Mathf.Abs(scale.Y - 1.55f) <= 0.01f,
            $"page scale={scale} (1.55)");
    }

    private partial void CheckSettingsTabLayout(Control settings, string tab, Control page)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (settings.GetNodeOrNull<Control>("SettingsTabManager") is not { } tabManager) return;
        bool tabsInside = PortraitSafeRect().Encloses(tabManager.GetGlobalRect())
            && tabManager.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree())
                .All(c => PortraitSafeRect().Grow(1f).Encloses(c.GetGlobalRect()));
        _report.Check("settings_tab_inside_frame_" + tab.ToLowerInvariant(), tabsInside, $"tabs={tabManager.GetGlobalRect()}");
        if (settings.GetNodeOrNull<Control>("ScrollContainer/Mask/Clipper") is not { } clipper) return;
        // The page is the scroll extent (1.55-scaled panel, several screens tall), so only its clipped part is drawn: compare
        // that with the frame, and require the page's own top edge to start inside the clip.
        Rect2 visible = page.GetGlobalRect();
        for (Node? node = page.GetParent(); node is not null; node = node.GetParent())
            if (node is Control clip && (clip is ScrollContainer || clip.ClipContents))
                visible = visible.Intersection(clip.GetGlobalRect());
        _report.Check("settings_rows_below_tabs_" + tab.ToLowerInvariant(),
            clipper.GetGlobalRect().Position.Y >= tabManager.GetGlobalRect().End.Y - 1f
                && clipper.GetGlobalRect().Grow(1f).Encloses(new Rect2(page.GlobalPosition, Vector2.One))
                && visible.HasArea() && PortraitSafeRect().Grow(1f).Encloses(visible),
            $"clipper={clipper.GetGlobalRect()} tabs={tabManager.GetGlobalRect()} page={page.GetGlobalRect()} visible={visible}");
    }

    // ---- pause ---------------------------------------------------------------------------------------

    private partial void CheckPauseLayout(NPauseMenu menu, Control panel, IReadOnlyList<NPauseMenuButton> visible, Label title, Rect2 before)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("pause_landscape_keeps_native_scale", panel.Scale.IsEqualApprox(Vector2.One), $"panel scale={panel.Scale}");
            return;
        }
        Rect2 safe = PortraitSafeRect();
        // One point is 2.88 units on this canvas; 127 units are the 44 points a row needs.
        const float pointsPerUnit = 375f / 1080f;
        _report.Check("pause_all_buttons_safe_and_touch_sized", visible.All(button => safe.Encloses(button.GetGlobalRect())
            && HostTouchTargetBox(button).Size.Y >= PortraitBands.MinTarget - 0.5f),
            string.Join("; ", visible.Select(button => $"{button.Name}: {button.GetGlobalRect()}")));
        float titlePoints = title.GetThemeFontSize("font_size") * Math.Abs(title.GetGlobalTransform().Scale.Y) * pointsPerUnit;
        _report.Check("pause_title_enlarged_inside_safe_frame", safe.Encloses(title.GetGlobalRect()) && titlePoints >= 28f,
            $"title={title.GetGlobalRect()} at {titlePoints:F1}pt");
        _report.Check("pause_panel_centered", safe.Encloses(panel.GetGlobalRect())
            && Mathf.Abs(panel.GetGlobalRect().GetCenter().X - safe.GetCenter().X) < 2f,
            $"panel={panel.GetGlobalRect()} safe={safe}");
    }

    // ---- game over -----------------------------------------------------------------------------------

    /// <summary>
    /// The launcher scrolls the settled summary in place: the game's own CenterContainer is the clipped window
    /// (ClipContents, scaled 1.337) and the column's offset is carried on it as canvas-unit metadata, so no ScrollContainer
    /// exists. The offsets are read through delegates because they change as the drag is processed after this call.
    /// </summary>
    private partial GameOverSummary? GameOverSummaryOf(NGameOverScreen screen)
    {
        if (!SafeAreaCanvas.IsPortrait) return null;
        if (screen.GetNodeOrNull<Control>("%ScreenshakeContainer/CenterContainer") is not { } window) return null;
        if (!window.ClipContents) return null;
        return new GameOverSummary(window, window.GetGlobalRect(),
            () => window.HasMeta("phone_summary_scroll") ? window.GetMeta("phone_summary_scroll").AsInt32() : 0,
            () => window.HasMeta("phone_summary_max_scroll") ? window.GetMeta("phone_summary_max_scroll").AsInt32() : 0);
    }

    private partial async Task CheckGameOverOpeningAsync(NGameOverScreen screen, NGameOverContinueButton proceed,
        IReadOnlyList<(Node2D Actor, float Y)> actors, bool win)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        CheckGameOverButton(proceed, "game_over_continue");
        var tableau = screen.GetNode<Control>("%CreatureContainer");
        _report.Check("game_over_actors_keep_combat_height", actors.All(actor => GodotObject.IsInstanceValid(actor.Actor)
            && actor.Actor.GetParent() == tableau && Mathf.Abs(actor.Actor.GlobalPosition.Y - actor.Y) < 3f),
            string.Join(", ", actors.Select(actor => $"{actor.Y:F1}->{actor.Actor.GlobalPosition.Y:F1}")));
        if (!win) return;
        var message = screen.GetNode<RichTextLabel>("%VictoryDamageLabel");
        await WaitUntilAsync(() => message.VisibleRatio >= 1f, "settled native victory message", ShortWait);
        _report.Check("game_over_victory_message_fits", message.GetContentHeight() <= message.Size.Y
            && PortraitSafeRect().Encloses(message.GetGlobalRect()),
            $"label={message.GetGlobalRect()} text height={message.GetContentHeight()}");
    }

    private partial void CheckGameOverSettled(NGameOverScreen screen, NReturnToMainMenuButton mainMenu, GridContainer scores,
        GameOverSummary? summary, NCombatRoom? fight, bool win)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("game_over_landscape_untouched", summary is null, "native layout");
            return;
        }
        CheckGameOverButton(mainMenu, "game_over_main_menu");
        // A fight keeps its tableau behind the summary; with no fight the act's art stands in for the room, so the screen
        // still has a backdrop, and that backdrop is what the summary window has to sit inside.
        bool backdrop = fight is null
            ? screen.FindChild("PhoneGameOverArt", true, false) is Control { } art && art.IsVisibleInTree()
            : fight.IsInsideTree();
        _report.Check("game_over_scrolling_summary_and_art", summary is not null
            && backdrop && PortraitSafeRect().Encloses(summary.Clip),
            $"window={summary?.Clip} backdrop={(fight is null ? "act art" : "fight")}");
        _report.Check("game_over_scores_visible_inside_scroll", summary is not null && scores.IsVisibleInTree()
            && summary.Clip.Grow(2f).Encloses(scores.GetGlobalRect()),
            $"scoreRect={scores.GetGlobalRect()} clip={summary?.Clip}");
        // The shared gate exempts the summary's award chips (NBadge/Nn); on this host they are padded, so they are measured
        // here: every interactive control inside the clipped window keeps a 127 u target (the pads' overlaps are allowed,
        // each target is measured on its own).
        if (summary is null) return;
        var chips = new List<string>();
        foreach (Node node in FindAll<Control>(summary.Window))
        {
            if (node is not Control chip || !chip.IsVisibleInTree() || chip.IsQueuedForDeletion()) continue;
            if (chip is not NClickableControl and not BaseButton and not Slider) continue;
            if (chip.MouseFilter == Control.MouseFilterEnum.Ignore) continue;
            Rect2 box = HostTouchTargetBox(chip);
            if (Mathf.Min(box.Size.X, box.Size.Y) + 0.5f < PortraitBands.MinTarget)
                chips.Add($"{chip.GetType().Name}:{chip.Name} {box.Size.X:F0}x{box.Size.Y:F0}");
        }
        _report.Check("game_over_summary_chip_targets", chips.Count == 0,
            chips.Count == 0 ? "every summary chip has a 127 u target" : string.Join("; ", chips.Take(20)));
    }

    private partial void CheckGameOverOutsideCombat(NGameOverScreen screen, GameOverSummary? summary)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 frame = _tree.Root.GetViewport().GetVisibleRect();
        Control? art = screen.FindChild("PhoneGameOverArt", true, false) as Control;
        _report.Check("game_over_no_fight_act_art_fills_frame", art is not null && art.IsVisibleInTree()
            && art.GetGlobalRect().Grow(2f).Encloses(frame),
            $"art={(art is null ? "none" : art.GetGlobalRect().ToString())} frame={frame}");
        // The native screen still makes a tableau for a room without a fight (the rest site's own branch places one visuals
        // node per seated player); the fallback art is for the room behind it, not a replacement for the bodies.
        var creatures = screen.GetNode<Control>("%CreatureContainer");
        _report.Check("game_over_no_fight_room_bodies_placed", creatures.GetChildCount() > 0,
            $"creature container children={creatures.GetChildCount()}");
        var banner = screen.GetNode<Control>("%Banner");
        Rect2 bannerRect = banner.GetGlobalRect();
        Rect2 safe = PortraitSafeRect();
        _report.Check("game_over_no_fight_banner_under_run_bar", safe.Grow(2f).Encloses(bannerRect),
            $"banner={bannerRect} safe={safe}");
        var quote = screen.GetNode<RichTextLabel>("%DeathQuoteLabel");
        Rect2 quoteRect = quote.GetGlobalRect();
        _report.Check("game_over_no_fight_quote_wraps_in_banner", quoteRect.Position.X >= bannerRect.Position.X - 1f
            && quoteRect.End.X <= bannerRect.End.X + 1f
            && quoteRect.Position.Y >= bannerRect.Position.Y - 1f && quoteRect.Position.Y <= bannerRect.End.Y,
            $"quote={quoteRect} banner={bannerRect}");
        _report.Check("game_over_no_fight_banner_above_summary", summary is null
            || bannerRect.End.Y <= summary.Clip.Position.Y + 1f,
            $"banner={bannerRect} window={summary?.Clip}");
    }

    /// <summary>The lowest drawn rect of the summary's content: its score rows and the chips below them.</summary>
    private static Rect2 LowestContent(GridContainer scores, Control[] rows)
    {
        Rect2 lowest = rows[0].GetGlobalRect();
        foreach (Control row in rows)
        {
            Rect2 rect = row.GetGlobalRect();
            if (rect.End.Y > lowest.End.Y) lowest = rect;
        }
        for (Node? node = scores.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is not Control block) break;
            foreach (Node child in block.GetChildren())
            {
                if (child is not Control part || part == scores || !GodotObject.IsInstanceValid(part)) continue;
                if (!part.IsVisibleInTree() || part.Size.Y < 2f || part.Modulate.A < 0.05f) continue;
                Rect2 rect = part.GetGlobalRect();
                if (rect.Size.Y < 2f || rect.Intersects(lowest)) continue;
                if (rect.End.Y > lowest.End.Y) lowest = rect;
            }
            if (node.GetParent() is Control up && up != node) block = up;
        }
        return lowest;
    }

    /// <summary>
    /// The in-place summary must actually clip: every visible score row needs a <see cref="Control.ClipContents"/> ancestor
    /// whose rect sits inside the reported window, and anything that is not clipped that way must not reach the banner or the
    /// act-art backstop. Rects alone (the settled check) cannot see a list drawn over them.
    /// </summary>
    private partial void CheckGameOverSummaryClipping(NGameOverScreen screen, GameOverSummary? summary, GridContainer scores)
    {
        if (!SafeAreaCanvas.IsPortrait || summary is null) return;
        Rect2 window = summary.Clip;
        var rows = scores.GetChildren().OfType<Control>()
            .Where(row => row.IsVisibleInTree() && row.GetGlobalRect().Size.X >= 2f && row.GetGlobalRect().Size.Y >= 2f).ToArray();
        var unclipped = new List<string>();
        var unclippedRects = new List<Rect2>();
        var notInert = new List<string>();
        foreach (Control row in rows)
        {
            Rect2 rect = row.GetGlobalRect();
            Control? clipNode = null;
            Rect2 clipUsed = default;
            for (Node? node = row.GetParent(); node is not null && node != screen; node = node.GetParent())
            {
                if (node is not Control clip || !clip.ClipContents) continue;
                // Where the pinned local rect actually lands on the canvas, through the clipping item's live transform:
                // the layout pins Rect2(Vector2.Zero, window / scale) in the item's own space, so the canvas rect is that
                // rect transformed. Comparing the window against the metadata the layout itself writes would be
                // tautological - this reads the geometry the renderer will use.
                Transform2D toCanvas = clip.GetGlobalTransformWithCanvas();
                Vector2 scale = toCanvas.Scale.Abs();
                if (scale.X < 0.01f || scale.Y < 0.01f) continue;
                var local = new Rect2(Vector2.Zero, window.Size / scale);
                var landed = new Rect2(toCanvas * local.Position, toCanvas.BasisXform(local.Size));
                bool positioned = MathF.Abs(landed.Position.X - window.Position.X) <= 2f &&
                                  MathF.Abs(landed.Position.Y - window.Position.Y) <= 2f &&
                                  MathF.Abs(landed.Size.X - window.Size.X) <= 2f &&
                                  MathF.Abs(landed.Size.Y - window.Size.Y) <= 2f;
                if (!positioned) continue;
                clipNode = clip;
                clipUsed = landed;
                break;
            }
            if (clipNode is null)
            {
                unclipped.Add($"{row.Name} {rect}");
                unclippedRects.Add(rect);
                continue;
            }
            if (!rect.Intersects(clipUsed) && row.MouseFilter != Control.MouseFilterEnum.Ignore)
                notInert.Add($"{row.Name} {rect} outside clip {clipUsed}");
        }
        _report.Check("game_over_summary_rows_clipped", rows.Length == 0 || unclipped.Count == 0,
            $"rows={rows.Length} window={window} unclipped={(unclipped.Count == 0 ? "none" : string.Join("; ", unclipped.Take(8)))}");
        _report.Check("game_over_summary_outside_rows_inert", notInert.Count == 0,
            $"outside={(notInert.Count == 0 ? "none" : string.Join("; ", notInert.Take(8)))}");

        Control? banner = screen.FindChild("Banner", true, false) as Control;
        Control? art = screen.FindChild("PhoneGameOverArt", true, false) as Control;
        var clashes = new List<string>();
        foreach (Rect2 rect in unclippedRects)
        {
            if (banner is { } b && b.IsVisibleInTree() && b.GetGlobalRect().Intersects(rect))
                clashes.Add($"banner {b.GetGlobalRect()} x row {rect}");
            if (art is { } a && a.IsVisibleInTree() && a.GetGlobalRect().Intersects(rect))
                clashes.Add($"art {a.GetGlobalRect()} x row {rect}");
        }
        // The scroll's end: at the maximum offset the last score row must sit on the window's bottom, not leave an empty
        // band. Predicted from the current rects and the two offsets, so no second drag is needed.
        // At capture time the rows must fill the window: an empty band under the last drawn row means the clip, the range or
        // the rows themselves are hiding content, even when the predicted end of the range looks right.
        if (rows.Length > 0)
        {
            // What a finger sees as the column's content: the score rows and the chips below them. A short summary is
            // centred in the window on purpose, so an empty band is only a defect when there is something to scroll.
            Rect2 lowest = LowestContent(scores, rows);
            float band = window.End.Y - MathF.Min(lowest.End.Y, window.End.Y);
            float rowHeight = MathF.Max(lowest.Size.Y, rows.Max(row => row.GetGlobalRect().Size.Y));
            // At the offset the run is at, the window must have no empty band under the content; a summary shorter than the
            // window is centred on purpose and has nothing to scroll.
            _report.Check("game_over_summary_fills_window",
                summary.Max() <= 8f || band <= MathF.Max(rowHeight, 8f),
                $"band={band:F1} (row {rowHeight:F1}) offset={summary.Offset()} max={summary.Max()} " +
                $"lowest={lowest} window={window}");
        }

        if (rows.Length > 0 && summary.Max() > summary.Offset())
        {
            // At the end of the range the last thing the column draws (a score row or a chip under it) must sit on the
            // window's bottom: anything left over was an empty tail the range should not have included.
            Rect2 lowest = LowestContent(scores, rows);
            float remaining = summary.Max() - summary.Offset();
            float predictedBottom = lowest.End.Y - remaining;
            float band = window.End.Y - predictedBottom;
            float rowHeight = MathF.Max(lowest.Size.Y, rows.Min(row => row.GetGlobalRect().Size.Y));
            _report.Check("game_over_summary_end_shows_last_row",
                MathF.Abs(band) <= MathF.Max(rowHeight, 8f),
                $"lowest content bottom at max {predictedBottom:F1} vs window bottom {window.End.Y:F1} " +
                $"(band {band:F1}, row {rowHeight:F1}, offset {summary.Offset()} of {summary.Max()})");
        }

        // The death quote over the tableau: a creature's drawn rect may reach the quote's line, but then the quote must be
        // drawn above it (the launcher lifts the banner), so the text stays readable.
        RichTextLabel? quote = screen.FindChild("DeathQuoteLabel", true, false) as RichTextLabel;
        Control? quotesParent = quote?.GetParent() as Control;
        int quoteZ = quotesParent?.ZIndex ?? 0;
        var quoteOverlaps = new List<string>();
        if (quote is { } q && q.IsVisibleInTree() && q.GetContentHeight() > 1f &&
            screen.GetNodeOrNull<Control>("%CreatureContainer") is { } tableau)
        {
            Vector2 scale = q.GetGlobalTransformWithCanvas().Scale;
            Rect2 text = new(q.GetGlobalRect().Position, new Vector2(q.Size.X * MathF.Abs(scale.X), q.GetContentHeight() * MathF.Abs(scale.Y)));
            foreach (Node child in tableau.GetChildren())
            {
                if (child is not NCreatureVisuals body || !GodotObject.IsInstanceValid(body)) continue;
                Rect2 drawn = body.Bounds.GetGlobalRect();
                if (!text.Intersects(drawn)) continue;
                // The quote may sit over a body as long as it is drawn above it (the launcher lifts the banner): only a
                // body drawn over the quote makes the text unreadable.
                if (quoteZ > body.ZIndex) continue;
                quoteOverlaps.Add($"body drawn over quote: {drawn} x {text} (quote z {quoteZ} vs body z {body.ZIndex})");
            }
        }
        _report.Check("game_over_quote_clear_or_above_bodies", quoteOverlaps.Count == 0,
            quoteOverlaps.Count == 0
                ? $"clear; quote z={quoteZ}"
                : $"clashes={(quoteOverlaps.Count == 0 ? "none" : string.Join("; ", quoteOverlaps.Take(6)))}");

        _report.Check("game_over_unclipped_rows_clear_of_banner_and_art", clashes.Count == 0,
            $"banner={(banner is { } vb ? vb.GetGlobalRect().ToString() : "none")} " +
            $"art={(art is { } va ? va.GetGlobalRect().ToString() : "none")} " +
            $"clashes={(clashes.Count == 0 ? "none" : string.Join("; ", clashes.Take(6)))}");
    }

    private void CheckGameOverButton(Control button, string name)
    {
        Rect2 rect = button.GetGlobalRect();
        _report.Check(name + "_safe_touch_target", rect.Size.Y >= PortraitBands.MinTarget - 1f && PortraitSafeRect().Encloses(rect),
            $"rect={rect} minimum={PortraitBands.MinTarget}");
    }
}
