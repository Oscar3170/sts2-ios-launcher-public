using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// A touch press reaches Godot as an <see cref="InputEventScreenTouch"/>, and with
/// <c>input_devices/pointing/emulate_mouse_from_touch</c> the engine synthesizes only an
/// <see cref="InputEventMouseButton"/> for it (Godot 4.5.1 <c>Input::_parse_input_event_impl</c>; no mouse motion).
/// Godot's GUI updates its mouse-over hierarchy only for <see cref="InputEventMouseMotion"/>, and the game's
/// <c>NClickableControl.HandleMousePress</c> ignores a press while <c>IsFocused</c> is false. On a phone, the first
/// tap on a native button therefore had no hovered/focused control and was dropped.
/// <para>
/// The window's <c>WindowInput</c> signal is emitted before <c>Viewport::push_input</c> delivers the event to the GUI
/// (<c>scene/main/window.cpp</c> <c>Window::_window_input</c>), so before the emulated button reaches the control, send
/// the motion that a touch-to-mouse path should have produced at the same point. The control under the finger then
/// becomes hovered (and the game's focus refreshed) before the press arrives. Only emulated left-button presses are
/// touched, both in portrait and landscape; real mouse input already has its motion.
/// </para>
/// </summary>
internal static class PhoneTouchHover
{
    private static bool _sending;

    public static void OnWindowInput(InputEvent input)
    {
        if (_sending) return;
        if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } button) return;
        if (button.Device != (int)InputEvent.DeviceIdEmulation) return;
        _sending = true;
        try
        {
            Input.ParseInputEvent(new InputEventMouseMotion
            {
                Device = (int)InputEvent.DeviceIdEmulation,
                Position = button.Position,
                GlobalPosition = button.GlobalPosition,
                Relative = Vector2.Zero,
            });
        }
        finally
        {
            _sending = false;
        }
    }
}
