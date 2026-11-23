using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The end of a run in portrait: defeat, victory and a boss victory. The reference portrait layout of this screen is;
/// it is laid out on a 1680 unit wide canvas where one phone point is 4.48 units, against 2.88 here (1080 across), so a
/// length is ported at 1080/1680 = 0.643 of the figure it gives, and so is a factor it applies to a native size (the
/// banner's scale, the summary's).
/// <para>
/// The run bar stays over this screen: the top bar and the relic strip are siblings above the overlay screens in the game's
/// own UI, so the banner starts under the relic band rather than under the top bar as it does there, and everything
/// follows from there. The death quote wraps inside the banner, the victory text sits under it at the phone's 12 points,
/// and the run summary is scrolled where the game keeps it, between the death tableau and the footer: the game's own
/// 1200 unit wide summary is four times the phone's width, so its own CenterContainer becomes a clipped window of the
/// screen's width and the column inside it is moved by the drag (the game's buttons act on release, so a drag lets the
/// press under it go first).
/// </para>
/// <para>
/// The tableau keeps the fight's own backdrop: the native backstop is made translucent to let the room through and the
/// fight's UI fades out (the game skips its own fade once a fight has already ended), a body is eased in from an edge only
/// as far as it needs to be and never off its ground line, and when there is no fight to show — a defeat outside combat —
/// the act's rest-site art stands behind the summary instead. Continue, Leaderboard and Main Menu are 44 points tall at the
/// footer with View Run above them. All of it is put back when Portrait Mode is turned off.
/// </para>
/// </summary>
internal sealed class PortraitGameOverLayout
{
    public const string TypeName = "MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen";

    /// <summary>The summary's window and how far it is scrolled, for the proof runs and the harness.</summary>
    internal const string SummaryScrollMeta = "phone_summary_scroll";
    internal const string SummaryMaxScrollMeta = "phone_summary_max_scroll";
    /// <summary>The clip actually applied to the canvas item, in canvas units (what is drawn inside, not the
    /// container's own Control rect, which the engine may have grown).</summary>
    internal const string SummaryClipMeta = "phone_summary_clip_rect";
    private const string ArtName = "PhoneGameOverArt";

    private const string CombatRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom";
    private const string CreatureTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NCreature";
    private const string CreatureEntityTypeName = "MegaCrit.Sts2.Core.Entities.Creatures.Creature";
    private const string CreatureVisualsTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals";
    private const string BannerTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NCommonBanner";

    private static readonly BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ---- the screen's own numbers, from the reference layout's 1680 canvas at 0.643 ------------------------------------------------------

    /// <summary>The banner starts this far under the band below the run bar (the reference 110 under its header).</summary>
    private const float BannerTopGap = 71f;

    /// <summary>The banner's largest drawn scale, and the room its width leaves (the reference 1.95 and 110 together).</summary>
    private const float BannerMaxScale = 1.254f;
    private const float BannerSideRoom = 71f;

    /// <summary>
    /// The death quote inside the banner's own unscaled space: 24 units in from its left, 156 down, 48 off its width.
    /// These are the game's own units (the quote is the banner's child and is drawn at the banner's scale), and 156 is where
    /// the game's own slide ends.
    /// </summary>
    private const float QuoteLeft = 24f;
    private const float QuoteTop = 156f;
    private const float QuoteWidthTrim = 48f;
    private const float QuoteFloor = 40f;

    /// <summary>
    /// The death quote's own size: 26 units in the banner's space, which at the banner's 1.254 is 32.6 units on the canvas,
    /// 11.3 points — the phone's readable floor for text (the game's own 24 is 10.4 points there).
    /// </summary>
    private const int QuoteFont = 26;

    /// <summary>
    /// The victory text under the banner: 51 in from each edge, 103 off the width, a 264 unit floor and the phone's 12
    /// points (the reference 80, 160, 410 and 54), 17 under the banner (the reference 26).
    /// </summary>
    private const float VictorySide = 51f;
    private const float VictoryWidthTrim = 103f;
    private const float VictoryHeight = 264f;
    private const float VictoryTopGap = 17f;
    private const int VictoryFont = 35;

    /// <summary>
    /// The summary's window: 31 in from each edge, its column drawn at up to 1.337 times the game's own size over a 720 unit
    /// wide body (the reference 48, 2.08 and 720). The window is the game's own CenterContainer, clipped, and the column
    /// inside it is offset by the drag; a drag starts after 18 units (about 6 points) and the wheel moves it 160 at a time.
    /// </summary>
    private const float SummarySide = 31f;
    private const float SummaryMaxScale = 1.337f;
    private const float SummaryContentWidth = 720f;
    private const float SummaryDragDistance = 18f;
    private const float SummaryWheelStep = 160f;

    /// <summary>
    /// Where the summary starts: below the run bar by 360 (the reference 560 under its header), never above 44% of the canvas
    /// (the reference layout's own fraction), and below the tableau's feet by 44 (the reference 68).
    /// </summary>
    private const float SummaryTopHeader = 360f;
    private const float SummaryTopFraction = 0.44f;
    private const float ActorGap = 44f;

    /// <summary>A gap between where the death quote's lines end and the summary's first row, so a long quote never runs into it.</summary>
    private const float SummaryQuoteGap = 15f;

    /// <summary>
    /// The summary's bottom stays 183 above the footer (the reference 285), and a tall tableau may not squeeze it below 289 units,
    /// with 161 as the least it may ever be and 15 under its content (the reference 450, 250 and 24).
    /// </summary>
    private const float SummaryAboveButtons = 183f;
    private const float SummaryMinHeight = 289f;
    private const float SummaryFloorHeight = 161f;
    private const float SummaryContentPad = 15f;

    /// <summary>The summary is the game's own: its side margins shrink to 8 so its body is 720 wide (the reference does the same).</summary>
    private const float SummaryMargin = 8f;

    /// <summary>
    /// A body is kept 15 units off the edge (the reference 24), and a fallen player is given 35% of its width there too: the upright
    /// hit box does not cover the pose and the weapon it lies down with.
    /// </summary>
    private const float ActorInset = 15f;
    private const float FallenActorRoom = 0.35f;
    private const float ActorEaseSeconds = 0.65f;

    /// <summary>How long the screen waits for the death tableau to be moved into it before it stops following it (15 seconds).</summary>
    private const int TableauWaitFrames = 900;

    /// <summary>
    /// The fight shows through the native backstop at 68%, and the summary's tint thins to 53% of its own alpha over the
    /// fight or the act's art (the reference layout's own fractions).
    /// </summary>
    private const float BackstopAlpha = 0.68f;
    private const float SummaryBackstopAlpha = 0.53f;
    private const float FightUiFadeSeconds = 0.3f;
    private static readonly Color ArtTint = new(0.42f, 0.39f, 0.40f, 0.9f);

    /// <summary>
    /// The four buttons: 560 x 128 units (44 points), the lower row's edge 123 above the canvas (the reference layout's footer), View Run
    /// 30 above that row (the reference 52), the text at the phone's 14 points (the reference 63) and its own margins scaled from the native
    /// 36 (as the reference scales them by its 3.35).
    /// </summary>
    private const float ButtonWidth = 560f;
    private const float ButtonHeight = 128f;
    private const float ButtonBottom = 123f;
    private const float ButtonGap = 30f;
    private const int ButtonFont = 40;
    private const int ButtonMinFont = 33;
    private const float ButtonLabelInset = 78f;

    private static readonly string[] FontSizeNames =
        ["normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size"];

    private static readonly (string Name, int Row)[] ButtonNames =
        [("ContinueButton", 0), ("LeaderboardButton", 0), ("MainMenuButton", 0), ("ViewRunButton", 1)];

    /// <summary>One game over screen's nodes and what the game's own layout had, so landscape gets it back.</summary>
    private sealed class State
    {
        public ulong Screen;
        public Vector2 Canvas;
        public float Cap;
        public Vector2 BannerSize;
        public float BannerBottom;
        public Vector2 BannerShow, BannerHide;
        public bool BannerRaised;
        public int BannerZ;
        public bool BannerSlid;
        public bool Built;
        public bool Settled;
        public bool ActorScanDone;
        public bool ActorsSeen;
        public float ActorBottom;
        public bool ArtTried;
        public bool ArtAdded;
        public int ArtTries;
        public bool FightFaded;
        public bool BackstopSeen;
        public bool SummaryTinted;
        public float SummaryScale = 1f;
        public float SummaryView, SummaryViewWidth, SummaryContent, SummaryColumnWidth, SummaryMax, SummaryScroll;
        public bool RefitBusy;
        public readonly List<ulong> RefitNodes = new(3);
        public bool CenterClip;
        public Action? CenterDraw;
        public Action? CenterSort;
        public Rect2 SummaryWindow;
        public bool BackstopFilterSet;
        public Control.MouseFilterEnum BackstopFilter;
        public Control.MouseFilterEnum CenterFilter;
        public int Frames;
        public ulong Shake, Banner, Quote, Victory, Backstop, SummaryBackstop, Center, Summary, Creatures, FightUi;
        public Color BackstopModulate = Colors.White;
        public Color SummaryBackstopColor = Colors.White;
        public float FightUiAlpha = 1f;
        public float QuoteBottom;
        public bool VictoryFitContent;
        public VerticalAlignment VictoryAlign;
        public readonly List<(string Name, bool Had, int Size)> VictoryFonts = new(5);
        public readonly List<(string Name, bool Had, int Size)> QuoteFonts = new(5);
        public int QuoteMax = -1, QuoteMin = -1, VictoryMax = -1, VictoryMin = -1;
        public Action? Refit, QuoteFit, VictoryFit;
        public readonly HashSet<ulong> CorrectedActors = new();
        public readonly Dictionary<ulong, Tween> ActorTweens = new();
        public readonly Dictionary<ulong, float> ActorStart = new();
        public bool Failed;
    }

    /// <summary>The game's members this file reaches: the fight, its creatures, and the banner's slide.</summary>
    private sealed class GameReflection
    {
        public readonly PropertyInfo? RoomInstance, RoomNodes, RoomUi;
        public readonly PropertyInfo? CreatureVisuals, CreatureEntity, EntityIsPlayer, EntityIsDead, VisualsBounds;
        public readonly FieldInfo? BannerShow, BannerHide;

        public GameReflection()
        {
            try
            {
                Assembly game = typeof(NGame).Assembly;
                Type? room = game.GetType(CombatRoomTypeName);
                Type? creature = game.GetType(CreatureTypeName);
                Type? entity = game.GetType(CreatureEntityTypeName);
                Type? banner = game.GetType(BannerTypeName);
                RoomInstance = room?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                RoomNodes = room?.GetProperty("CreatureNodes", InstAll);
                RoomUi = room?.GetProperty("Ui", InstAll);
                CreatureVisuals = creature?.GetProperty("Visuals", InstAll);
                CreatureEntity = creature?.GetProperty("Entity", InstAll);
                EntityIsPlayer = entity?.GetProperty("IsPlayer", InstAll);
                EntityIsDead = entity?.GetProperty("IsDead", InstAll);
                VisualsBounds = game.GetType(CreatureVisualsTypeName)?.GetProperty("Bounds", InstAll);
                BannerShow = banner?.GetField("_showPos", InstAll);
                BannerHide = banner?.GetField("_hidePos", InstAll);
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait game over: the game's members were not found: ", error.Message);
            }
        }
    }

    private static readonly Lazy<GameReflection> Reflection = new(() => new GameReflection());

    private sealed record ButtonState(
        Vector2 Show, Vector2 Size, Vector2 Pivot, int MaxFont, int MinFont, float LabelLeft, float LabelRight);

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, ButtonState> _buttons = new();
    private readonly Dictionary<ulong, TextServer.AutowrapMode> _wrapModes = new();
    private readonly HashSet<ulong> _logged = new();
    private readonly HashSet<ulong> _padded = new();
    private readonly Dictionary<ulong, Control.MouseFilterEnum> _rowFilters = new();
    private readonly HashSet<ulong> _padHolders = new();
    private Window? _dragWindow;
    private bool _dragDown, _dragMoving;
    private float _dragStartY, _dragStartScroll;
    private State? _state;

    // ---- entry points ----------------------------------------------------------------------------------------------------

    /// <summary>Lay the screen out; idempotent, as it runs on every deferred layout pass while the screen is up.</summary>
    public void Apply(Control screen, float capHeight, Vector2 canvas)
    {
        if (screen.GetType().FullName != TypeName || !SafeAreaCanvas.IsPortrait) return;
        canvas = new Vector2(SafeAreaCanvas.PortraitWidth, MathF.Max(canvas.Y, SafeAreaCanvas.MinPortraitHeight));
        State state = StateFor(screen);
        if (state.Failed) return;
        state.Canvas = canvas;
        state.Cap = capHeight;
        state.Settled = false;
        try
        {
            Resolve(state, screen);
            PlaceBanner(state, screen);
            PlaceQuote(state, screen);
            PlaceVictory(state, screen);
            PlaceButtons(screen);
            EnsureSummary(state, screen);
            PlaceSummary(state);
        }
        catch (Exception error)
        {
            Fail(state, error);
        }
    }

    /// <summary>
    /// Per frame while the screen is up: the death tableau is moved into it after it opens, so the bodies are eased in as
    /// they arrive and the summary's top follows their feet, the fight's own backdrop is faded, and the act's art is added
    /// when there is no fight behind the screen. Once all of that is done the pass costs nothing.
    /// </summary>
    public void OnProcessFrame()
    {
        State? state = _state;
        if (state == null || state.Settled || state.Failed || !SafeAreaCanvas.IsPortrait) return;
        if (GodotObject.InstanceFromId(state.Screen) is not Control screen || !GodotObject.IsInstanceValid(screen) ||
            !screen.IsVisibleInTree())
            return;
        try
        {
            if (PlaceTableau(state, screen)) PlaceSummary(state);
            state.Settled = state.ArtTried && state.FightFaded && state.ActorsSeen;
        }
        catch (Exception error)
        {
            Fail(state, error);
        }
    }

    public void RestoreLandscape()
    {
        State? state = _state;
        if (state == null) return;
        _state = null;
        try
        {
            RestoreState(state);
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait game over: could not put everything back: ", error.Message);
        }
    }

    public void PruneDead()
    {
        // A screen that went away without a landscape restore (back to the menu) has nothing left to put back.
        if (_state is not { } state || GodotObject.IsInstanceIdValid(state.Screen)) return;
        _state = null;
        _snapshots.Clear();
        _buttons.Clear();
        _wrapModes.Clear();
        _padded.Clear();
        _padHolders.Clear();
        DetachSummaryDrag();
    }

    private State StateFor(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (_state is { } known && known.Screen == id) return known;
        var created = new State { Screen = id };
        _state = created;
        return created;
    }

    private void Fail(State state, Exception error)
    {
        if (state.Failed) return;
        state.Failed = true;
        GD.PrintErr($"Portrait game over: the screen is left as it last stood: {error}");
    }

    /// <summary>The screen's nodes by their unique names: they are the game's own and never replaced while it is up.</summary>
    private static void Resolve(State state, Control screen)
    {
        if (state.Shake == 0) state.Shake = Id(screen.GetNodeOrNull<Control>("%ScreenshakeContainer"));
        if (state.Creatures == 0) state.Creatures = Id(screen.GetNodeOrNull<Control>("%CreatureContainer"));
        if (state.Backstop == 0) state.Backstop = Id(screen.GetNodeOrNull<ColorRect>("%Backstop"));
        if (state.SummaryBackstop == 0) state.SummaryBackstop = Id(screen.GetNodeOrNull<ColorRect>("%SummaryBackstop"));
        if (state.Banner == 0) state.Banner = Id(screen.GetNodeOrNull<Control>("%Banner"));
    }

    // ---- banner, quote and victory text ----------------------------------------------------------------------------------

    /// <summary>
    /// The banner is 654 x 162 centred on the game's screen. In portrait it is drawn up to 1.254 times that (as wide as the
    /// screen allows) under the run bar's bands, with the game's own slide kept: the game tweens its global position from
    /// <c>_hidePos</c> to <c>_showPos</c> when it appears, so both are written to where it now lives.
    /// </summary>
    private void PlaceBanner(State state, Control screen)
    {
        if (state.Banner == 0) return;
        if (Live(state.Banner) is not Control banner) return;
        if (state.BannerSize == Vector2.Zero)
        {
            PortraitControlSnapshot snap = Capture(banner);
            state.BannerSize = new Vector2(
                MathF.Max(1f, snap.InitialOffsetRight - snap.InitialOffsetLeft),
                MathF.Max(1f, snap.InitialOffsetBottom - snap.InitialOffsetTop));
        }
        float scale = MathF.Min(BannerMaxScale, MathF.Max(0.2f, (state.Canvas.X - BannerSideRoom) / state.BannerSize.X));
        var target = new Vector2(
            MathF.Round((state.Canvas.X - state.BannerSize.X * scale) * 0.5f),
            MathF.Round(PortraitBands.RelicBandBottom(state.Cap) + BannerTopGap));
        state.BannerBottom = target.Y + state.BannerSize.Y * scale;
        if (MathF.Abs(banner.Scale.X - scale) < 0.001f && banner.Position.IsEqualApprox(target) &&
            (Reflection.Value.BannerShow?.GetValue(banner) is not Vector2 shown || shown.IsEqualApprox(banner.GlobalPosition)))
            return;
        Capture(banner);
        if (!state.BannerRaised)
        {
            // The death tableau is drawn under the screen's Ui, but the bodies can still reach the quote's line at 9:16; the
            // banner is lifted above the whole screen so the quote always stays readable over them.
            state.BannerRaised = true;
            state.BannerZ = banner.ZIndex;
            banner.ZIndex = 6;
        }
        banner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        banner.PivotOffset = Vector2.Zero;
        banner.Scale = new Vector2(scale, scale);
        banner.Position = target;
        Vector2 show = banner.GlobalPosition;
        if (!state.BannerSlid)
        {
            state.BannerSlid = true;
            if (Reflection.Value.BannerShow?.GetValue(banner) is Vector2 wasShow) state.BannerShow = wasShow;
            if (Reflection.Value.BannerHide?.GetValue(banner) is Vector2 wasHide) state.BannerHide = wasHide;
        }
        Reflection.Value.BannerShow?.SetValue(banner, show);
        Reflection.Value.BannerHide?.SetValue(banner, show + new Vector2(0f, 50f));
        if (_logged.Add(banner.GetInstanceId()))
            GD.Print($"Portrait game over: banner {state.BannerSize.X:F0}x{state.BannerSize.Y:F0} drawn at {scale:F3} " +
                     $"({state.BannerSize.X * scale:F0}x{state.BannerSize.Y * scale:F0}) at {target.X:F0},{target.Y:F0}.");
    }

    /// <summary>
    /// The death quote is one unwrapped line as wide as its text inside the banner, so a long quote runs off both sides of
    /// the phone. It wraps inside the banner's width instead, from where the game's own slide ends, and grows down over the
    /// tableau as its lines need.
    /// </summary>
    private void PlaceQuote(State state, Control screen)
    {
        if (state.Banner == 0 || state.BannerSize == Vector2.Zero) return;
        if (state.Quote == 0)
        {
            if (screen.FindChild("DeathQuoteLabel", recursive: true, owned: false) is not RichTextLabel found) return;
            state.Quote = found.GetInstanceId();
        }
        if (Live(state.Quote) is not RichTextLabel quote) return;
        if (state.QuoteFit == null)
        {
            Capture(quote);
            RecordThemeFontSizes(state.QuoteFonts, quote);
            state.QuoteMax = IntProp(quote, "MaxFontSize");
            state.QuoteMin = IntProp(quote, "MinFontSize");
            foreach (string name in FontSizeNames) quote.AddThemeFontSizeOverride(name, QuoteFont);
            SetFontLimits(quote, QuoteFont, QuoteFont);
            Action fit = () => FitQuote(state);
            state.QuoteFit = fit;
            quote.MinimumSizeChanged += fit;
        }
        float width = MathF.Max(1f, state.BannerSize.X - QuoteWidthTrim);
        quote.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        quote.Position = new Vector2(QuoteLeft, QuoteTop);
        quote.Size = new Vector2(width, MathF.Max(QuoteFloor, quote.Size.Y));
        quote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        FitQuote(state);
        if (_logged.Add(quote.GetInstanceId()))
            GD.Print($"Portrait game over: the death quote wraps at {width:F0} units of the banner.");
    }

    /// <summary>The quote holds its lines and remembers where it ends, so the summary can start clear of it.</summary>
    private static void FitQuote(State state)
    {
        Control? quote = Live(state.Quote);
        FitHeight(quote, QuoteFloor);
        if (quote != null) state.QuoteBottom = quote.GetGlobalRect().End.Y;
    }

    /// <summary>
    /// The victory message is one centred full-screen block whose text is sized to the desktop screen; in portrait it is
    /// redrawn under the banner at the phone's 12 points across the screen's width, and grows to hold what it says.
    /// </summary>
    private void PlaceVictory(State state, Control screen)
    {
        if (state.BannerSize == Vector2.Zero) return;
        if (screen.GetNodeOrNull<RichTextLabel>("%VictoryDamageLabel") is not { } victory) return;
        if (state.Victory == 0)
        {
            state.Victory = victory.GetInstanceId();
            Capture(victory);
            state.VictoryFitContent = victory.FitContent;
            state.VictoryAlign = victory.VerticalAlignment;
            RecordThemeFontSizes(state.VictoryFonts, victory);
            state.VictoryMax = IntProp(victory, "MaxFontSize");
            state.VictoryMin = IntProp(victory, "MinFontSize");
            foreach (string name in FontSizeNames) victory.AddThemeFontSizeOverride(name, VictoryFont);
            SetFontLimits(victory, VictoryFont, VictoryFont);
            Action fit = () => FitHeight(Live(state.Victory), VictoryHeight);
            state.VictoryFit = fit;
            victory.MinimumSizeChanged += fit;
        }
        victory.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        victory.Position = new Vector2(VictorySide, MathF.Round(state.BannerBottom + VictoryTopGap));
        victory.Size = new Vector2(MathF.Round(state.Canvas.X - VictoryWidthTrim), MathF.Max(VictoryHeight, victory.Size.Y));
        victory.VerticalAlignment = VerticalAlignment.Top;
        victory.FitContent = true;
        FitHeight(victory, VictoryHeight);
        if (_logged.Add(victory.GetInstanceId()))
            GD.Print($"Portrait game over: the victory text is {VictoryFont} units over {victory.Size.X:F0} wide at " +
                     $"{victory.Position.X:F0},{victory.Position.Y:F0}.");
    }

    // ---- the four buttons ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Continue, Leaderboard and Main Menu share one 260 x 58 unit spot at the bottom middle of the game's screen, with View
    /// Run beside it: 20 points tall on the phone. Each is 560 x 128 units (44 points) at the footer here, View Run above the
    /// others. Each slides to a show position it keeps from when it was made (<c>_showPosition</c>) and grows on hover by its
    /// scale, so the size and that position are set, not a scale.
    /// </summary>
    private void PlaceButtons(Control screen)
    {
        foreach ((string name, int row) in ButtonNames)
        {
            if (screen.GetNodeOrNull<Control>("%" + name) is not { } button || button.GetParent() is not Control parent)
                continue;
            if (button.GetType().GetField("_showPosition", InstAll) is not { } field ||
                field.GetValue(button) is not Vector2 show)
                continue;
            ulong id = button.GetInstanceId();
            Control? label = button.GetNodeOrNull<Control>("Label");
            if (!_buttons.TryGetValue(id, out ButtonState? known))
            {
                int max = label?.Get("MaxFontSize").VariantType == Variant.Type.Int
                    ? label.Get("MaxFontSize").AsInt32()
                    : 0;
                int min = label?.Get("MinFontSize").VariantType == Variant.Type.Int
                    ? label.Get("MinFontSize").AsInt32()
                    : 0;
                known = new ButtonState(show, button.Size, button.PivotOffset, max, min,
                    label?.OffsetLeft ?? 0f, label?.OffsetRight ?? 0f);
                _buttons[id] = known;
            }
            var size = new Vector2(ButtonWidth, ButtonHeight);
            var target = new Vector2(
                MathF.Round((parent.Size.X - size.X) * 0.5f),
                MathF.Round(parent.Size.Y - ButtonBottom - size.Y - row * (size.Y + ButtonGap)));
            if (show == target && button.Size == size) continue;
            // Hidden buttons sit below or beside their show position: keep that offset.
            Vector2 away = button.Position - show;
            field.SetValue(button, target);
            button.Size = size;
            button.Position = target + away;
            button.PivotOffset = size / 2f;
            if (label != null)
            {
                if (known.MaxFont > 0) SetFontLimits(label, ButtonFont, ButtonMinFont);
                label.OffsetLeft = ButtonLabelInset;
                label.OffsetRight = -ButtonLabelInset;
                if (known.MaxFont > 0 && label.IsInsideTree())
                    label.GetType().GetMethod("AdjustFontSize", InstAll, Type.EmptyTypes)?.Invoke(label, null);
            }
            if (_logged.Add(id))
                GD.Print($"Portrait game over: {name} is {size.X:F0}x{size.Y:F0} at {target.X:F0},{target.Y:F0}.");
        }
    }

    // ---- the summary in place --------------------------------------------------------------------------------------------

    /// <summary>
    /// The game's summary is a 1200 unit wide centred column (250 unit margins around a 700 unit score bar), four times the
    /// phone wide and taller than what is left of the screen. It is scrolled where the game keeps it, with nothing taken out
    /// of the tree: the screen's own CenterContainer becomes the window (clipped, drawn at up to 1.337 times the game's
    /// size) and the column inside it is offset by the drag. Its margins shrink to 8 so the body is 720 wide, and its
    /// scores, badges and discoveries are the game's own and keep animating into it.
    /// </summary>
    private void EnsureSummary(State state, Control screen)
    {
        if (state.Built) return;
        if (Live(state.Shake) is not Control shake) return;
        if (shake.GetNodeOrNull<Control>("CenterContainer") is not { } center) return;
        if (center.GetNodeOrNull<Control>("RunSummaryContainer") is not { } summary) return;
        Capture(center);
        state.Center = center.GetInstanceId();
        state.Summary = summary.GetInstanceId();
        state.CenterClip = center.ClipContents;
        state.CenterFilter = center.MouseFilter;
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        center.PivotOffset = Vector2.Zero;
        center.GrowHorizontal = Control.GrowDirection.Begin;
        center.GrowVertical = Control.GrowDirection.Begin;
        center.ClipContents = true;
        center.MouseFilter = Control.MouseFilterEnum.Ignore;

        Capture(summary);
        summary.AddThemeConstantOverride("margin_left", (int)SummaryMargin);
        summary.AddThemeConstantOverride("margin_right", (int)SummaryMargin);

        // Nothing is moved: the window's clip is given to the CenterContainer's own canvas item. Godot raises a Control's
        // size to its combined minimum size (Control::set_size and Control::_size_changed), so the container grows with the
        // column and its own clip rect would grow with it. Control's NOTIFICATION_DRAW writes that rect and the clip, and
        // the draw signal is emitted after it (CanvasItem::_notification_draw: notification(NOTIFICATION_DRAW) then
        // emit_signal(draw)), so the handler below writes the window rect last, every draw. The renderer clips a clipped
        // item's children to that rect (RendererCanvasCull::_cull_canvas_item: final_clip_rect = rect of the item).
        if (state.CenterDraw == null)
        {
            Action drawn = () => ClipSummaryWindow(state);
            state.CenterDraw = drawn;
            center.Draw += drawn;
        }
        // The summary's tint is a full-screen ColorRect with MouseFilter=Stop; the game never needs it to take a press (its
        // Ui above holds the buttons), but it wins the hit test over Ui/MainMenuButton here, so it is made inert while the
        // screen is in portrait (restored in landscape).
        if (!state.BackstopFilterSet && Live(state.SummaryBackstop) is ColorRect tint)
        {
            state.BackstopFilterSet = true;
            state.BackstopFilter = tint.MouseFilter;
            tint.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        state.SummaryScale = MathF.Min(SummaryMaxScale,
            MathF.Max(0.2f, (state.Canvas.X - 2f * SummarySide) / SummaryContentWidth));
        center.Scale = new Vector2(state.SummaryScale, state.SummaryScale);
        // Its own size changes as its score lines, badges and discoveries arrive, at every level between the score grid and
        // the window: each of them asks for the measurement again, so the scroll range is never left behind.
        Action refit = () => RefitSummary(state);
        state.Refit = refit;
        state.RefitNodes.Clear();
        foreach (Control? hook in new[] { center, summary, center.GetNodeOrNull<Control>("%ScoreLineContainer") })
        {
            if (hook == null || !GodotObject.IsInstanceValid(hook)) continue;
            hook.MinimumSizeChanged += refit;
            state.RefitNodes.Add(hook.GetInstanceId());
        }
        // The game's own CenterContainer places its child every time it sorts: the column's offset is put back after it.
        Action sort = () => RefitSummary(state);
        state.CenterSort = sort;
        if (center is Container box) box.SortChildren += sort;
        state.Built = true;
        RefitSummary(state);
        center.QueueRedraw();
        if (screen.GetWindow() is { } window) AttachSummaryDrag(window);
        GD.Print($"Portrait game over: the run summary scrolls in place at {state.SummaryScale:F3} of the game's size.");
    }

    /// <summary>
    /// The window's clip, written after the engine has written the container's own (and grown) rect for this draw: the
    /// canvas item is clipped to the window in its local units, so the rows only draw inside it whatever the container's
    /// size. Runs once per draw of the container; only a change of the window needs a redraw to take effect.
    /// </summary>
    private static void ClipSummaryWindow(State state)
    {
        if (Live(state.Center) is not Control center) return;
        if (state.SummaryWindow.Size.X < 1f || state.SummaryWindow.Size.Y < 1f) return;
        // The custom rect is in the item's own space (RendererCanvasCull transforms it by the item's final transform), so
        // the canvas window is mapped back through this item's real canvas transform instead of being assumed.
        Transform2D toLocal = center.GetGlobalTransformWithCanvas().AffineInverse();
        var local = new Rect2(toLocal * state.SummaryWindow.Position, toLocal.BasisXform(state.SummaryWindow.Size));
        RenderingServer.CanvasItemSetCustomRect(center.GetCanvasItem(), true, local);
        RenderingServer.CanvasItemSetClip(center.GetCanvasItem(), true);
        center.SetMeta(SummaryClipMeta, state.SummaryWindow);
    }

    /// <summary>
    /// The game's summary grows as its score lines, badges and discoveries arrive: the window's range and the column's
    /// offset follow it. The window's height is the one the screen set (a container cannot be made smaller than its child,
    /// so its own size is not what is on screen), and the column's height is its own minimum.
    /// </summary>
    private void RefitSummary(State state)
    {
        if (state.RefitBusy) return;
        if (Live(state.Center) is not Control center) return;
        if (Live(state.Summary) is not Control summary) return;
        state.RefitBusy = true;
        try
        {
            if (state.SummaryView <= 0f) state.SummaryView = center.Size.Y;
            Vector2 minimum = summary.GetCombinedMinimumSize();
            state.SummaryContent = ContentBottom(summary, MathF.Max(minimum.Y, summary.Size.Y));
            state.SummaryColumnWidth = MathF.Max(1f, minimum.X);
            state.SummaryMax = MathF.Max(0f, state.SummaryContent - state.SummaryView);
            state.SummaryScroll = Math.Clamp(state.SummaryScroll, 0f, state.SummaryMax);
            ApplySummaryScroll(state);
            PadSummaryChips(center);
            IgnoreSummaryRows(center);
        }
        finally
        {
            state.RefitBusy = false;
        }
    }

    /// <summary>
    /// How far the column's content really reaches: the bottom of its last block that is shown. The game's column keeps
    /// the badges, the score bar and the discovery section in its minimum height even when they hold nothing, and scrolling
    /// to a range that included them left the last score row an empty column above the window's bottom.
    /// </summary>
    private static float ContentBottom(Control column, float fallback)
    {
        if (column.GetChildCount() == 1 && column.GetChild(0) is Control blocks)
        {
            float bottom = 0f;
            foreach (Node child in blocks.GetChildren())
            {
                if (child is not Control block || !GodotObject.IsInstanceValid(block)) continue;
                if (!block.Visible || block.Modulate.A < 0.05f || block.Size.Y < 1f) continue;
                bottom = MathF.Max(bottom, block.Position.Y + block.Size.Y);
            }
            if (bottom > 1f) return bottom;
        }
        return MathF.Max(1f, fallback);
    }

    /// <summary>
    /// The column keeps its own size and is placed inside the clip: centred when it fits, top-aligned and moved up by the
    /// scroll when it does not. The window's two metas are written here too, in canvas units, for the proof runs.
    /// </summary>
    private static void ApplySummaryScroll(State state)
    {
        if (Live(state.Summary) is not Control summary) return;
        if (Live(state.Center) is not Control center) return;
        var size = new Vector2(state.SummaryColumnWidth, state.SummaryContent);
        if (!summary.Size.IsEqualApprox(size)) summary.Size = size;
        float centred = MathF.Max(0f, (state.SummaryView - state.SummaryContent) * 0.5f);
        var at = new Vector2(
            MathF.Max(0f, (state.SummaryViewWidth - size.X) * 0.5f),
            centred - state.SummaryScroll);
        if (!summary.Position.IsEqualApprox(at)) summary.Position = at;
        center.SetMeta(SummaryScrollMeta, (int)MathF.Round(state.SummaryScroll * state.SummaryScale));
        center.SetMeta(SummaryMaxScrollMeta, (int)MathF.Round(state.SummaryMax * state.SummaryScale));
        PinClip(state);
    }

    // ---- the summary's drag ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A drag scrolls the summary. The game's buttons act on the release of a press, so the press under a drag is let go
    /// first (a swipe never presses a chip) while a tap inside the slop is left entirely to the game; the window's own wheel
    /// scrolls it too. Window input runs before the GUI, which is what lets the press be cleared in time. Nothing here runs
    /// in landscape.
    /// </summary>
    private void OnSummaryInput(InputEvent input)
    {
        State? state = _state;
        if (_dragWindow == null || state == null || !state.Built) return;
        try
        {
            if (!SafeAreaCanvas.IsPortrait)
            {
                DetachSummaryDrag();
                return;
            }
            Vector2 point = _dragWindow.GetFinalTransform().AffineInverse() * PointOf(input);
            switch (input)
            {
                case InputEventScreenTouch touch when touch.Index == 0:
                    if (touch.Pressed) BeginSummaryDrag(state, point);
                    else EndSummaryDrag(state, release: false);
                    return;
                case InputEventScreenDrag drag when drag.Index == 0:
                    MoveSummaryDrag(state, point);
                    return;
                case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                    if (button.Pressed) BeginSummaryDrag(state, point);
                    else EndSummaryDrag(state, release: false);
                    return;
                case InputEventMouseMotion:
                    if (_dragDown) MoveSummaryDrag(state, point);
                    return;
                case InputEventMouseButton wheel when wheel.Pressed &&
                    wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                    if (Live(state.Center) is not Control window || !window.GetGlobalRect().HasPoint(point) ||
                        state.SummaryMax <= 0.5f)
                        return;
                    float step = wheel.ButtonIndex == MouseButton.WheelUp ? -SummaryWheelStep : SummaryWheelStep;
                    state.SummaryScroll = Math.Clamp(state.SummaryScroll + step / state.SummaryScale, 0f, state.SummaryMax);
                    ApplySummaryScroll(state);
                    _dragWindow.SetInputAsHandled();
                    return;
                default:
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait game over: the summary's drag stopped: ", error.Message);
            EndSummaryDrag(state);
        }
    }

    private static Vector2 PointOf(InputEvent input) => input switch
    {
        InputEventMouse mouse => mouse.Position,
        InputEventScreenTouch touch => touch.Position,
        InputEventScreenDrag drag => drag.Position,
        _ => Vector2.Zero,
    };

    private void BeginSummaryDrag(State state, Vector2 point)
    {
        // The container's own Control rect grows with the long column (the engine raises it to its minimum size), so the
        // window is the rect the screen placed, never that.
        if (state.SummaryWindow.Size.X < 1f || !state.SummaryWindow.HasPoint(point)) return;
        EndSummaryDrag(state, release: false);
        if (Live(state.Center) is not Control window) return;
        if (Live(state.Summary) is not Control summary || !summary.IsVisibleInTree()) return;
        // The column may have grown since the last measurement (its own minimum changed): measure again before deciding
        // that there is nothing to scroll.
        RefitSummary(state);
        if (state.SummaryMax <= 0.5f) return;
        _dragDown = true;
        _dragMoving = false;
        _dragStartY = point.Y;
        _dragStartScroll = state.SummaryScroll;
    }

    private void MoveSummaryDrag(State state, Vector2 point)
    {
        if (!_dragDown) return;
        float moved = _dragStartY - point.Y;
        if (!_dragMoving)
        {
            if (MathF.Abs(moved) < SummaryDragDistance) return;
            _dragMoving = true;
            if (Live(state.Screen) is Control screen) PhoneSwipeRelease.Shared.Release(screen);
        }
        state.SummaryScroll = Math.Clamp(_dragStartScroll + moved / state.SummaryScale, 0f, state.SummaryMax);
        ApplySummaryScroll(state);
        _dragWindow?.SetInputAsHandled();
    }

    private void EndSummaryDrag(State state, bool release = true)
    {
        bool moved = release && _dragMoving;
        // Clear first: a stale flag must never take the next press (the Main Menu tap after a scroll).
        _dragDown = false;
        _dragMoving = false;
        if (moved && Live(state.Screen) is Control screen) PhoneSwipeRelease.Shared.Release(screen);
        if (moved) LogSummaryState(state);
    }

    private void AttachSummaryDrag(Window window)
    {
        if (_dragWindow == window) return;
        DetachSummaryDrag();
        _dragWindow = window;
        window.WindowInput += OnSummaryInput;
    }

    private void DetachSummaryDrag()
    {
        if (_dragWindow != null && GodotObject.IsInstanceValid(_dragWindow)) _dragWindow.WindowInput -= OnSummaryInput;
        _dragWindow = null;
        _dragDown = false;
        _dragMoving = false;
    }

    /// <summary>
    /// The summary's own chips are buttons and they are small: a badge is 64 units and a discovered item 160 x 64. Each
    /// gets an invisible 44 point touch area centred on it without its picture changing — a container chip (a discovered
    /// item is one) hangs it from a child the container fits, as the between layouts do for their rows. The chips are
    /// added as the summary animates in, so this runs whenever the column's size changes and only pads what is new.
    /// </summary>
    private void PadSummaryChips(Control root)
    {
        foreach (Node node in Descendants(root))
        {
            if (node is not Control chip || !GodotObject.IsInstanceValid(chip) || !chip.IsVisibleInTree() || !IsChip(chip))
                continue;
            if (!_padded.Add(chip.GetInstanceId())) continue;
            Rect2 rect = chip.GetGlobalRect();
            if (rect.Size.X >= PortraitBands.MinTarget && rect.Size.Y >= PortraitBands.MinTarget) continue;
            Vector2 scale = chip.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            float growX = MathF.Max(0f, PortraitBands.MinTarget - rect.Size.X) * 0.5f / scale.X;
            float growY = MathF.Max(0f, PortraitBands.MinTarget - rect.Size.Y) * 0.5f / scale.Y;
            if (!PhoneHitArea.IsContainer(chip))
            {
                PhoneHitArea.Grow(chip, growX, growY, growX, growY);
                continue;
            }
            Control? holder = chip.GetNodeOrNull<Control>(PhoneHitArea.HolderName);
            if (holder == null)
            {
                holder = new Control { Name = PhoneHitArea.HolderName, MouseFilter = Control.MouseFilterEnum.Ignore };
                var pad = new Control
                {
                    Name = PhoneHitArea.NodeName,
                    MouseFilter = Control.MouseFilterEnum.Pass,
                    FocusMode = Control.FocusModeEnum.None,
                };
                pad.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                holder.AddChild(pad);
                chip.AddChild(holder);
                _padHolders.Add(holder.GetInstanceId());
            }
            if (holder.GetNodeOrNull<Control>(PhoneHitArea.NodeName) is { } area)
            {
                area.OffsetLeft = -growX;
                area.OffsetTop = -growY;
                area.OffsetRight = growX;
                area.OffsetBottom = growY;
            }
        }
    }

    /// <summary>
    /// The score rows are read-only labels, but a Control still takes the pointer: a row drawn (or clipped) outside the
    /// window would swallow a tap meant for what is under it. Each row is made inert as it appears, and put back in
    /// landscape.
    /// </summary>
    private void IgnoreSummaryRows(Control center)
    {
        if (center.GetNodeOrNull<Control>("%ScoreLineContainer") is not { } rows) return;
        foreach (Node child in rows.GetChildren())
        {
            if (child is not Control row || !GodotObject.IsInstanceValid(row)) continue;
            ulong id = row.GetInstanceId();
            if (!_rowFilters.ContainsKey(id))
            {
                _rowFilters[id] = row.MouseFilter;
                row.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
    }

    /// <summary>Whether a control in the summary is one of the game's buttons, tickboxes, sliders or dropdowns.</summary>
    private static bool IsChip(Control control) =>
        control is NClickableControl or BaseButton or Slider or OptionButton || IsTypeNamed(control.GetType(), "NTickbox");

    private static bool IsTypeNamed(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.Name == name) return true;
        return false;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node grandchild in Descendants(child)) yield return grandchild;
        }
    }

    /// <summary>
    /// The summary's window: the screen's width between its side margins, from below the run bar (or the tableau's feet) to
    /// 183 above the footer. A tall tableau may not squeeze it below 289 units, which is what keeps it readable when the
    /// fight's bodies fill the screen.
    /// </summary>
    private void PlaceSummary(State state)
    {
        if (!state.Built) return;
        if (Live(state.Center) is not Control center) return;
        float width = MathF.Max(1f, state.Canvas.X - 2f * SummarySide);
        float footer = state.Canvas.Y - ButtonBottom - ButtonHeight;
        float bottom = footer - SummaryAboveButtons;
        float top = MathF.Max(PortraitBands.RelicBandBottom(state.Cap) + SummaryTopHeader,
            state.Canvas.Y * SummaryTopFraction);
        if (state.ActorsSeen) top = MathF.Max(top, state.ActorBottom + ActorGap);
        if (state.QuoteBottom > 0f) top = MathF.Max(top, state.QuoteBottom + SummaryQuoteGap);
        top = MathF.Min(top, footer - SummaryAboveButtons - SummaryMinHeight);
        float height = MathF.Max(SummaryFloorHeight, bottom - top);
        var at = new Vector2(SummarySide, MathF.Round(top));
        // The window is the CenterContainer itself: its rect is the window in its own (scaled) units, so the clip and the
        // column drawn at 1.337 are exactly the screen's width.
        var size = new Vector2(width / state.SummaryScale, MathF.Max(1f, height / state.SummaryScale));
        // The screen's own window height, not the container's: a container may be taller than the window it is told to be
        // once its column has grown past it, and the clip stays where it was placed.
        state.SummaryView = size.Y;
        state.SummaryViewWidth = size.X;
        // The window in canvas units; the container's own units for the clip are derived from its real transform.
        var canvasWindow = new Rect2(at, new Vector2(width, height));
        if (!state.SummaryWindow.IsEqualApprox(canvasWindow))
        {
            state.SummaryWindow = canvasWindow;
            if (Live(state.Center) is Control window) window.QueueRedraw();
            PinClip(state);
        }
        if (!center.Position.IsEqualApprox(at) || !center.Size.IsEqualApprox(size))
        {
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
            center.Position = at;
            center.Size = size;
            if (_logged.Add(center.GetInstanceId()))
                GD.Print($"Portrait game over: the summary window is {width:F0}x{height:F0} at {at.X:F0},{at.Y:F0}.");
        }
        RefitSummary(state);
    }

    // ---- the tableau, the fight's backdrop and the act's art -------------------------------------------------------------

    /// <summary>
    /// The bodies of the fight are moved into the screen after it opens, on their own ground line. Each is kept inside the
    /// canvas: it is eased in by the least it needs (a fallen player's pose lies wider than its hit box), never down or up.
    /// The summary's top follows their feet. Returns whether the summary's top changed.
    /// </summary>
    private bool PlaceTableau(State state, Control screen)
    {
        bool summaryMoved = false;
        // The bodies arrive when the screen opens, a frame or two after it is laid out; a screen that never gets one stops
        // being followed after this many frames, so an empty tableau cannot keep the pass alive.
        if (++state.Frames > TableauWaitFrames) state.ActorsSeen = true;
        if (Live(state.Creatures) is Control table)
        {
            int count = table.GetChildCount();
            if (count > 0 && (count != state.CorrectedActors.Count || !state.ActorScanDone))
            {
                state.ActorScanDone = true;
                foreach (Node child in table.GetChildren())
                {
                    if (child is not Node2D actor || !GodotObject.IsInstanceValid(actor)) continue;
                    if (!state.CorrectedActors.Add(actor.GetInstanceId())) continue;
                    state.ActorsSeen = true;
                    if (PlaceActor(state, screen, actor)) summaryMoved = true;
                }
            }
        }

        object? room = CombatRoom();
        if (room != null)
        {
            if (!state.BackstopSeen && Live(state.Backstop) is ColorRect backstop)
            {
                state.BackstopSeen = true;
                state.BackstopModulate = backstop.Modulate;
                backstop.Modulate = new Color(1f, 1f, 1f, BackstopAlpha);
            }
            if (!state.FightFaded) FadeFightUi(state, screen, room);
            state.ArtTried = true;
        }
        else
        {
            // No fight UI to fade: the act's art is the only backdrop to settle.
            state.FightFaded = true;
            if (!state.ArtTried)
            {
                if (PortraitBetweenLayout.ActRestTexture() is { } art)
                {
                    AddArt(state, screen, art);
                    state.ArtTried = true;
                }
                else if (++state.ArtTries > 120)
                {
                    state.ArtTried = true;
                }
            }
        }
        if (state.BackstopSeen || state.ArtAdded) ThinSummaryBackstop(state);
        return summaryMoved;
    }

    /// <summary>
    /// What the summary's window, its range and its rows are at the end of a drag (for the proof runs and for diagnosing a
    /// range that does not match what is drawn).
    /// </summary>
    private static void LogSummaryState(State state)
    {
        if (Live(state.Center) is not Control center) return;
        float lastBottom = 0f;
        int rows = 0;
        if (Live(state.Summary) is Control column &&
            column.GetNodeOrNull<Control>("%ScoreLineContainer") is { } grid)
        {
            foreach (Node child in grid.GetChildren())
            {
                if (child is not Control row || !GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree()) continue;
                rows++;
                lastBottom = MathF.Max(lastBottom, row.GetGlobalRect().End.Y);
            }
        }
        GD.Print($"Portrait game over: summary offset={state.SummaryScroll * state.SummaryScale:F0} " +
                 $"max={state.SummaryMax * state.SummaryScale:F0} content={state.SummaryContent:F0} view={state.SummaryView:F0} " +
                 $"window={state.SummaryWindow} clipMeta={(center.HasMeta(SummaryClipMeta) ? center.GetMeta(SummaryClipMeta).AsRect2().ToString() : "none")} " +
                 $"rows={rows} lastRowBottom={lastBottom:F0} centerRect={center.GetGlobalRect()}");
    }

    /// <summary>
    /// Pins the clip from values the layout owns, so it does not depend on a canvas transform read inside a draw callback:
    /// the pinned item is the CenterContainer, which never moves during a drag (the offset moves its child
    /// %RunSummaryContainer), its local origin is the window's top-left, and the window's size over the layout's own scale
    /// is the local window. Cheap enough for every drag frame (one struct, no allocation, no redraw).
    /// </summary>
    private static void PinClip(State state)
    {
        if (Live(state.Center) is not Control center) return;
        if (state.SummaryWindow.Size.X < 1f || state.SummaryScale < 0.01f) return;
        var local = new Rect2(Vector2.Zero, state.SummaryWindow.Size / state.SummaryScale);
        RenderingServer.CanvasItemSetCustomRect(center.GetCanvasItem(), true, local);
        RenderingServer.CanvasItemSetClip(center.GetCanvasItem(), true);
        center.SetMeta(SummaryClipMeta, state.SummaryWindow);
    }

    /// <summary>One body: read its own bounds and ease it in from the nearest edge by the least that clears it.</summary>
    private bool PlaceActor(State state, Control screen, Node2D actor)
    {
        if (Reflection.Value.VisualsBounds?.GetValue(actor) is not Control bounds) return false;
        Rect2 rect = bounds.GetGlobalRect();
        if (rect.Size.X < 1f) return false;
        bool fallen = IsFallenPlayer(actor);
        float left = ActorInset + (fallen ? rect.Size.X * FallenActorRoom : 0f);
        float right = state.Canvas.X - ActorInset - rect.Size.X;
        float targetX = left > right
            ? state.Canvas.X * 0.5f - rect.Size.X * 0.5f
            : Math.Clamp(rect.Position.X, left, MathF.Max(left, right));
        float dx = targetX - rect.Position.X;
        if (MathF.Abs(dx) > 1f)
        {
            state.ActorStart[actor.GetInstanceId()] = actor.GlobalPosition.X;
            Tween tween = screen.CreateTween().BindNode(actor);
            tween.TweenProperty(actor, "global_position:x", actor.GlobalPosition.X + dx, ActorEaseSeconds)
                .SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            state.ActorTweens[actor.GetInstanceId()] = tween;
            if (_logged.Add(actor.GetInstanceId()))
                GD.Print($"Portrait game over: {actor.Name} eased {MathF.Abs(dx):F0} units " +
                         $"{(dx < 0f ? "left" : "right")}{(fallen ? " (fallen)" : "")}.");
        }
        state.ActorBottom = MathF.Max(state.ActorBottom, rect.End.Y);
        return true;
    }

    /// <summary>Whether this body is a player's and is dead: its pose lies wider than its upright hit box does.</summary>
    private static bool IsFallenPlayer(Node2D actor)
    {
        if (Reflection.Value.RoomInstance?.GetValue(null) is not { } room) return false;
        if (Reflection.Value.RoomNodes?.GetValue(room) is not IEnumerable creatures) return false;
        PropertyInfo? visuals = Reflection.Value.CreatureVisuals, entityProp = Reflection.Value.CreatureEntity;
        if (visuals == null || entityProp == null) return false;
        foreach (object? creature in creatures)
        {
            if (creature == null || visuals.GetValue(creature) != actor) continue;
            object? entity = entityProp.GetValue(creature);
            return entity != null
                && Reflection.Value.EntityIsPlayer?.GetValue(entity) is true
                && Reflection.Value.EntityIsDead?.GetValue(entity) is true;
        }
        return false;
    }

    /// <summary>
    /// The fight's own UI (hand, rail, End Turn) stays over the death screen when the game skipped its fade (it only fades
    /// a still-active fight). It is faded out here instead, under the native death tint.
    /// </summary>
    private void FadeFightUi(State state, Control screen, object room)
    {
        state.FightFaded = true;
        if (Reflection.Value.RoomUi?.GetValue(room) is not CanvasItem ui || !GodotObject.IsInstanceValid(ui)) return;
        state.FightUi = ui.GetInstanceId();
        state.FightUiAlpha = ui.Modulate.A;
        if (state.FightUiAlpha <= 0.01f) return;
        screen.CreateTween().BindNode(ui).TweenProperty(ui, "modulate:a", 0f, FightUiFadeSeconds);
        GD.Print("Portrait game over: the fight's UI fades under the death screen.");
    }

    /// <summary>
    /// No fight behind the screen (a defeat outside one) leaves the native backstop's flat red: the act's own rest-site art
    /// stands in for the room, above the backstop and under everything the screen draws.
    /// </summary>
    private void AddArt(State state, Control screen, Texture2D texture)
    {
        var art = new TextureRect
        {
            Name = ArtName,
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = ArtTint,
        };
        screen.AddChild(art);
        if (Live(state.Shake) is Control shake) screen.MoveChild(art, shake.GetIndex());
        art.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        state.ArtAdded = true;
        GD.Print("Portrait game over: the act's art stands behind the summary.");
    }

    /// <summary>The summary's own tint is nearly opaque: over a fight or the act's art it thins so the tableau keeps showing.</summary>
    private void ThinSummaryBackstop(State state)
    {
        if (state.SummaryTinted || Live(state.SummaryBackstop) is not ColorRect tint) return;
        state.SummaryTinted = true;
        state.SummaryBackstopColor = tint.Color;
        Color color = tint.Color;
        color.A *= SummaryBackstopAlpha;
        tint.Color = color;
    }

    private object? CombatRoom()
    {
        try
        {
            return Reflection.Value.RoomInstance?.GetValue(null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- restore ---------------------------------------------------------------------------------------------------------

    private void RestoreState(State state)
    {
        DetachSummaryDrag();
        if (state.QuoteFit is { } quoteFit && Live(state.Quote) is RichTextLabel quote)
            quote.MinimumSizeChanged -= quoteFit;
        if (state.VictoryFit is { } victoryFit && Live(state.Victory) is RichTextLabel victory)
            victory.MinimumSizeChanged -= victoryFit;
        if (state.Refit is { } refit)
            foreach (ulong id in state.RefitNodes)
                if (GodotObject.InstanceFromId(id) is Control hooked && GodotObject.IsInstanceValid(hooked))
                    hooked.MinimumSizeChanged -= refit;

        foreach (Tween tween in state.ActorTweens.Values)
            if (GodotObject.IsInstanceValid(tween)) tween.Kill();
        foreach ((ulong id, float startX) in state.ActorStart)
            if (GodotObject.InstanceFromId(id) is Node2D actor && GodotObject.IsInstanceValid(actor))
                actor.GlobalPosition = new Vector2(startX, actor.GlobalPosition.Y);

        // The window goes back to the game's own: the clip handler and the sort hook are dropped (the engine's own draw
        // then gives the container its full rect again), and its clip and filter are the game's once more.
        if (state.CenterDraw is { } drawn && Live(state.Center) is Control drawnOn) drawnOn.Draw -= drawn;
        if (state.CenterSort is { } sort && Live(state.Center) is Container sorted) sorted.SortChildren -= sort;
        if (Live(state.Center) is Control center2)
        {
            center2.ClipContents = state.CenterClip;
            center2.MouseFilter = state.CenterFilter;
            center2.RemoveMeta(SummaryClipMeta);
            center2.QueueRedraw();
            if (center2 is Container again) again.QueueSort();
        }
        foreach ((ulong id, Control.MouseFilterEnum filter) in _rowFilters)
            if (GodotObject.InstanceFromId(id) is Control row && GodotObject.IsInstanceValid(row))
                row.MouseFilter = filter;
        _rowFilters.Clear();
        if (state.ArtAdded && Live(state.Screen) is Control owner)
            foreach (Node child in owner.GetChildren())
                if (child.Name == ArtName) child.QueueFree();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();

        foreach ((ulong id, TextServer.AutowrapMode mode) in _wrapModes)
            if (GodotObject.InstanceFromId(id) is RichTextLabel label && GodotObject.IsInstanceValid(label))
                label.AutowrapMode = mode;
        _wrapModes.Clear();

        if (Live(state.Quote) is RichTextLabel quote2)
        {
            foreach ((string name, bool had, int size) in state.QuoteFonts)
            {
                if (had) quote2.AddThemeFontSizeOverride(name, size);
                else quote2.RemoveThemeFontSizeOverride(name);
            }
            if (state.QuoteMax >= 0) quote2.Set("MaxFontSize", state.QuoteMax);
            if (state.QuoteMin >= 0) quote2.Set("MinFontSize", state.QuoteMin);
        }
        if (Live(state.Victory) is RichTextLabel victory2)
        {
            victory2.FitContent = state.VictoryFitContent;
            victory2.VerticalAlignment = state.VictoryAlign;
            foreach ((string name, bool had, int size) in state.VictoryFonts)
            {
                if (had) victory2.AddThemeFontSizeOverride(name, size);
                else victory2.RemoveThemeFontSizeOverride(name);
            }
            if (state.VictoryMax >= 0) victory2.Set("MaxFontSize", state.VictoryMax);
            if (state.VictoryMin >= 0) victory2.Set("MinFontSize", state.VictoryMin);
        }
        if (Live(state.Backstop) is ColorRect backstop && state.BackstopSeen) backstop.Modulate = state.BackstopModulate;
        if (state.BannerSlid && Live(state.Banner) is Control banner)
        {
            Reflection.Value.BannerShow?.SetValue(banner, state.BannerShow);
            Reflection.Value.BannerHide?.SetValue(banner, state.BannerHide);
            if (state.BannerRaised) banner.ZIndex = state.BannerZ;
        }
        if (Live(state.SummaryBackstop) is ColorRect tint2)
        {
            if (state.SummaryTinted) tint2.Color = state.SummaryBackstopColor;
            if (state.BackstopFilterSet) tint2.MouseFilter = state.BackstopFilter;
        }
        if (Live(state.FightUi) is CanvasItem ui && state.FightFaded)
            ui.Modulate = new Color(ui.Modulate, state.FightUiAlpha);

        RestoreButtons();
        // The chips' touch areas go back to the game's own.
        foreach (ulong id in _padded)
            if (GodotObject.InstanceFromId(id) is Control chip && GodotObject.IsInstanceValid(chip))
                PhoneHitArea.Remove(chip);
        _padded.Clear();
        foreach (ulong id in _padHolders)
        {
            if (GodotObject.InstanceFromId(id) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();
        }
        _padHolders.Clear();
        state.CorrectedActors.Clear();
        _buttons.Clear();
        _logged.Clear();
    }

    private void RestoreButtons()
    {
        foreach ((ulong id, ButtonState state) in _buttons)
        {
            if (GodotObject.InstanceFromId(id) is not Control button || !GodotObject.IsInstanceValid(button)) continue;
            if (button.GetType().GetField("_showPosition", InstAll) is not { } field ||
                field.GetValue(button) is not Vector2 show)
                continue;
            Vector2 away = button.Position - show;
            field.SetValue(button, state.Show);
            button.Size = state.Size;
            button.Position = state.Show + away;
            button.PivotOffset = state.Pivot;
            if (button.GetNodeOrNull<Control>("Label") is not { } label) continue;
            if (state.MaxFont > 0) SetFontLimits(label, state.MaxFont, state.MinFont);
            label.OffsetLeft = state.LabelLeft;
            label.OffsetRight = state.LabelRight;
            if (state.MaxFont > 0 && label.IsInsideTree())
                label.GetType().GetMethod("AdjustFontSize", InstAll, Type.EmptyTypes)?.Invoke(label, null);
        }
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------

    private static Control? Live(ulong id) =>
        id == 0 ? null : GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control) &&
        control.IsInsideTree() && !control.IsQueuedForDeletion() ? control : null;

    private static ulong Id(Node? node) => node != null && GodotObject.IsInstanceValid(node) ? node.GetInstanceId() : 0;

    private PortraitControlSnapshot Capture(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_snapshots.TryGetValue(id, out PortraitControlSnapshot? snapshot))
            _snapshots[id] = snapshot = new PortraitControlSnapshot(control, captureVisible: false);
        if (control is RichTextLabel rich && !_wrapModes.ContainsKey(id)) _wrapModes[id] = rich.AutowrapMode;
        return snapshot;
    }

    /// <summary>The game's own font sizes on the label, for landscape: a theme override it had, and the size it was.</summary>
    private static void RecordThemeFontSizes(List<(string Name, bool Had, int Size)> into, Control label)
    {
        foreach (string name in FontSizeNames)
            into.Add((name, label.HasThemeFontSizeOverride(name), label.GetThemeFontSize(name)));
    }

    /// <summary>A label that sizes itself to its text keeps that text inside its box, with no container to grow it.</summary>
    private static void FitHeight(Control? label, float floor)
    {
        if (label == null || !GodotObject.IsInstanceValid(label) || !label.IsInsideTree()) return;
        float height = label.GetCombinedMinimumSize().Y;
        if (label is RichTextLabel rich) height = MathF.Max(height, rich.GetContentHeight());
        height = MathF.Max(floor, height);
        if (MathF.Abs(label.Size.Y - height) < 0.5f) return;
        label.Size = new Vector2(label.Size.X, height);
    }

    /// <summary>The game's label fits its text between a smallest and a largest font size, whenever its size changes.</summary>
    private static void SetFontLimits(Control label, int max, int min)
    {
        if (label.Get("MaxFontSize").VariantType == Variant.Type.Int) label.Set("MaxFontSize", max);
        if (label.Get("MinFontSize").VariantType == Variant.Type.Int) label.Set("MinFontSize", min);
    }

    /// <summary>One of the label's own integer properties, or -1 when the script has none (it is the game's own class).</summary>
    private static int IntProp(Control label, string name) =>
        label.Get(name).VariantType == Variant.Type.Int ? label.Get(name).AsInt32() : -1;
}
