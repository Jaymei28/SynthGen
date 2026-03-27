using Silk.NET.Input;
using System.Numerics;

namespace SynthGen.App;

/// <summary>
/// Abstracts mouse/keyboard input from Silk.NET for camera controls and UI.
/// </summary>
public class InputManager
{
    private readonly IInputContext _input;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;

    // Mouse state
    public Vector2 MousePosition { get; private set; }
    public Vector2 MouseDelta { get; private set; }
    public float ScrollDelta { get; private set; }
    public bool MiddleMouseDown { get; private set; }
    public bool LeftMouseDown { get; private set; }
    public bool RightMouseDown { get; private set; }
    public bool ShiftHeld { get; private set; }
    public bool CtrlHeld { get; private set; }

    private Vector2 _lastMouse;
    private float _scrollAccum;
    
    // Key press tracking (event-based, bypasses ImGui focus)
    private readonly HashSet<Key> _keysJustPressed = new();

    public InputManager(IInputContext input)
    {
        _input = input;
        if (_input.Mice.Count > 0)
        {
            _mouse = _input.Mice[0];
            _mouse.Scroll += (_, wheel) => _scrollAccum += wheel.Y;
        }
        if (_input.Keyboards.Count > 0)
        {
            _keyboard = _input.Keyboards[0];
            _keyboard.KeyDown += (_, key, _) => _keysJustPressed.Add(key);
        }
    }

    public void Update()
    {
        if (_mouse != null)
        {
            var pos = new Vector2(_mouse.Position.X, _mouse.Position.Y);
            MouseDelta = pos - _lastMouse;
            _lastMouse = pos;
            MousePosition = pos;

            LeftMouseDown = _mouse.IsButtonPressed(MouseButton.Left);
            MiddleMouseDown = _mouse.IsButtonPressed(MouseButton.Middle);
            RightMouseDown = _mouse.IsButtonPressed(MouseButton.Right);

            ScrollDelta = _scrollAccum;
            _scrollAccum = 0;
        }

        if (_keyboard != null)
        {
            ShiftHeld = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
            CtrlHeld = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight);
        }
    }

    /// <summary>Returns true if the key was pressed this frame (event-based, works even when ImGui has focus).</summary>
    public bool WasKeyJustPressed(Key key) => _keysJustPressed.Contains(key);
    
    /// <summary>Call at end of frame to clear single-press state.</summary>
    public void EndFrame() => _keysJustPressed.Clear();

    public bool IsKeyPressed(Key key)
    {
        return _keyboard?.IsKeyPressed(key) ?? false;
    }
}
