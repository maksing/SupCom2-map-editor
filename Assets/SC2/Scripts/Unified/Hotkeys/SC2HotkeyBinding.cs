using System;
using UnityEngine;

/// <summary>One remappable key combination. Key == KeyCode.None marks a
/// modifier-only binding (e.g. "hold Ctrl while scrolling") that has no key
/// of its own — everything else about persistence/UI treats both shapes
/// identically instead of needing a second parallel type.</summary>
[Serializable]
public struct SC2HotkeyBinding
{
    public KeyCode Key;
    public bool Ctrl;
    public bool Shift;
    public bool Alt;

    public SC2HotkeyBinding(KeyCode key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public bool IsModifierOnly => Key == KeyCode.None;

    /// <summary>A mouse button binding: Key is KeyCode.Mouse0..Mouse6 (left, right, middle, then the side buttons).</summary>
    public bool IsMouse => Key >= KeyCode.Mouse0 && Key <= KeyCode.Mouse6;

    /// <summary>The Event.button number of a mouse binding (0 left, 1 right, 2 middle), -1 for any other binding.</summary>
    public int MouseButton => IsMouse ? (int)(Key - KeyCode.Mouse0) : -1;

    /// <summary>Does this MouseDown event press the binding: its button with exactly its modifiers? The modifiers are read from the
    /// event and from Input too, like the camera always did, since the event's own flags can lag behind in a player.</summary>
    public bool MatchesMouseDown(Event e) =>
        e != null && e.type == EventType.MouseDown && MatchesMouseButton(e, e.button);

    /// <summary>Same test for a button that is already held (a MouseDrag): the modifiers held right now against the binding's.</summary>
    public bool MatchesMouseButton(Event e, int button)
    {
        if (e == null || !IsMouse || MouseButton != button) return false;
        var ctrl = e.control || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        var shift = e.shift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var alt = e.alt || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        return ctrl == Ctrl && shift == Shift && alt == Alt;
    }

    /// <summary>For a keyed binding: does this KeyDown event match exactly
    /// (key + all three modifier flags)? An empty alternative slot (no key) matches nothing: a character-only KeyDown
    /// carries keyCode None and would otherwise match it.</summary>
    public bool MatchesEvent(Event e) =>
        e != null && Key != KeyCode.None && e.keyCode == Key && e.control == Ctrl && e.shift == Shift && e.alt == Alt;

    /// <summary>For a modifier-only binding: are exactly these modifiers
    /// held right now, regardless of e.keyCode?</summary>
    public bool ModifiersHeld(Event e) =>
        e != null && e.control == Ctrl && e.shift == Shift && e.alt == Alt;

    public bool CollidesWith(SC2HotkeyBinding other) =>
        Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    public override string ToString()
    {
        if (IsModifierOnly)
        {
            if (!Ctrl && !Shift && !Alt) return "(none)";
            return ModifierLabel();
        }
        var keyLabel = KeyLabel(Key);
        return string.IsNullOrEmpty(ModifierLabel()) ? keyLabel : ModifierLabel() + "+" + keyLabel;
    }

    private string ModifierLabel()
    {
        var s = "";
        if (Ctrl) s += "Ctrl+";
        if (Shift) s += "Shift+";
        if (Alt) s += "Alt+";
        return s.Length > 0 ? s.Substring(0, s.Length - 1) : "";
    }

    private static string KeyLabel(KeyCode key) => key switch
    {
        KeyCode.None => "",
        KeyCode.Return => "Enter",
        KeyCode.KeypadEnter => "Numpad Enter",
        KeyCode.Mouse0 => "Left Mouse", KeyCode.Mouse1 => "Right Mouse", KeyCode.Mouse2 => "Middle Mouse",
        KeyCode.Mouse3 => "Mouse 4", KeyCode.Mouse4 => "Mouse 5", KeyCode.Mouse5 => "Mouse 6", KeyCode.Mouse6 => "Mouse 7",
        KeyCode.Alpha0 => "0", KeyCode.Alpha1 => "1", KeyCode.Alpha2 => "2", KeyCode.Alpha3 => "3",
        KeyCode.Alpha4 => "4", KeyCode.Alpha5 => "5", KeyCode.Alpha6 => "6", KeyCode.Alpha7 => "7",
        KeyCode.Alpha8 => "8", KeyCode.Alpha9 => "9",
        _ => key.ToString(),
    };
}
