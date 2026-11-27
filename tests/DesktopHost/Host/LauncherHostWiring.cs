using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// The launcher's in-game portrait wiring without <c>PhoneGraphics</c>: what <c>PhoneGraphics._Ready</c>, <c>OnTouchFrame</c>,
/// <c>OnWindowInput</c>, <c>OnNodeAdded</c> and <c>GameStarted</c> do for portrait, in the same order, and nothing else. No settings
/// file, no <c>PhoneGraphicsSettings</c>, no MSAA write, no <c>SaveManager.SaveSettings</c>, no frame cap, no settings rows.
/// Portrait is decided by the caller at attach (<c>initialPortrait</c>): on when the window is taller than wide, else the
/// launcher's landscape (the game's own layout, with the touch handlers' landscape behaviour). A scenario may switch it
/// live through <see cref="ApplyOrientation"/>, which is the launcher's <c>PortraitLayout.Apply</c> the Graphics row runs.
/// </summary>
internal sealed class LauncherHostWiring
{
    private readonly PortraitLayout _portraitLayout = new();
    private readonly PhoneTouchCards _touchCards = new();
    private readonly PhoneTouchScroll _touchScroll = new();
    private readonly PhoneHoverTips _hoverTips = new();
    private readonly PhoneSelectConfirm _selectConfirm = new();
    private readonly PhoneCardRewardChoice _cardRewardChoice = new();
    private readonly PhoneRewardTapGuard _rewardTapGuard = new();
    private readonly PhoneChoiceConfirm _choiceConfirm = new();
    private readonly TaskCompletionSource _gameStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SceneTree? _tree;
    private bool _gameRunning;

    /// <summary>The wiring the running launcher-host test installed, for scenarios that drive a live switch.</summary>
    internal static LauncherHostWiring? Active { get; private set; }

    /// <summary>Completes once the game's main menu is up and portrait has been applied (<c>PhoneGraphics.GameStarted</c>).</summary>
    public Task GameStarted => _gameStarted.Task;

    /// <summary>
    /// Turns Portrait Mode off (or back on) live, the call the launcher's Graphics row makes
    /// (<c>PortraitLayout.Apply</c>): the canvas is recomputed for the current window, landscape is restored or portrait
    /// re-applied. The host window itself does not rotate (the native orientation request is only recorded), so the
    /// canvas change is the live switch a device gets from the OS rotation.
    /// </summary>
    public void ApplyOrientation(bool portrait)
    {
        if (_tree == null) throw new InvalidOperationException("The launcher host wiring is not attached.");
        _portraitLayout.Apply(_tree, portrait);
        GD.Print($"[TEST] portrait mode {(portrait ? "on" : "off")} live: canvas {_tree.Root.ContentScaleSize} " +
                 $"portrait={SafeAreaCanvas.IsPortrait}");
    }

    /// <summary>The <c>PhoneGraphics._Ready</c> attachments, then the wait for the main menu.</summary>
    public void Start(SceneTree tree, bool portrait)
    {
        _tree = tree;
        Active = this;
        try
        {
            tree.Root.WindowInput += OnWindowInput;
            tree.NodeAdded += OnNodeAdded;
            _portraitLayout.Attach(tree, initialPortrait: portrait);
            _touchCards.Attach(tree);
            _touchScroll.Attach(tree);
            tree.ProcessFrame += OnTouchFrame;
        }
        catch (Exception error)
        {
            Disable(error);
            throw;
        }
        MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(WaitForMainMenuAsync(tree));
    }

    /// <summary>The <c>PhoneGraphics._ExitTree</c> detaches (plus the two handlers PhoneGraphics left connected).</summary>
    public void Detach()
    {
        if (ReferenceEquals(Active, this)) Active = null;
        if (_tree == null) return;
        _gameRunning = false;
        _portraitLayout.Detach();
        _touchCards.Detach();
        _touchScroll.Detach();
        if (GodotObject.IsInstanceValid(_tree.Root)) _tree.Root.WindowInput -= OnWindowInput;
        _tree.NodeAdded -= OnNodeAdded;
        _tree.ProcessFrame -= OnTouchFrame;
        _tree = null;
    }

    // GameEntry.StartAsync awaits GameStartupComplete and requires game.MainMenu; then PhoneGraphics.GameStarted runs.
    private async Task WaitForMainMenuAsync(SceneTree tree)
    {
        try
        {
            NGame? found;
            while ((found = NGame.Instance) == null || !GodotObject.IsInstanceValid(found))
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            NGame game = found;
            await game.GameStartupComplete;
            for (int frame = 0; frame < 1800 && (game.MainMenu == null || !GodotObject.IsInstanceValid(game.MainMenu)); frame++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (game.MainMenu == null) throw new InvalidOperationException("The game did not reach its main menu.");
            GameStartedCore();
            _gameStarted.TrySetResult();
        }
        catch (Exception error)
        {
            Disable(error);
            _gameStarted.TrySetException(error);
        }
    }

    // PhoneGraphics.GameStarted without the MSAA write, the settings save and the PhoneGraphicsSettings file.
    private void GameStartedCore()
    {
        _gameRunning = true;
        _portraitLayout.ApplyImmediate();
    }

    // Hand cards under a finger and hover tips kept on screen; cards, relics and shop items taken only after a confirm. Portrait
    // only; the three screen passes still run in landscape (PhoneGraphics.OnTouchFrame).
    private void OnTouchFrame()
    {
        if (!_gameRunning || _tree == null) return;
        if (SafeAreaCanvas.IsPortrait)
        {
            _touchCards.OnProcessFrame(_tree);
            _hoverTips.OnProcessFrame(_tree);
        }
        _cardRewardChoice.OnProcessFrame();
        _rewardTapGuard.OnProcessFrame();
        _selectConfirm.OnProcessFrame();
        _choiceConfirm.OnProcessFrame();
    }

    // PhoneGraphics.OnWindowInput without the battery bookkeeping on touches.
    private void OnWindowInput(InputEvent input)
    {
        PhoneTouchHover.OnWindowInput(input);
        _selectConfirm.OnWindowInput(input);
    }

    // Choice screens, reward lists and shop slots get the tap-then-confirm behaviour only in portrait.
    private void OnNodeAdded(Node node)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _cardRewardChoice.OnNodeAdded(node);
        _rewardTapGuard.OnNodeAdded(node);
        _selectConfirm.OnNodeAdded(node);
        _choiceConfirm.OnNodeAdded(node);
    }

    private void Disable(Exception error)
    {
        GD.PushError("Launcher host wiring disabled: " + error.GetType().Name + ": " + error.Message);
        _gameRunning = false;
    }
}
