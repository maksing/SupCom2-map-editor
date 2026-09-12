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

    /// <summary>For a keyed binding: does this KeyDown event match exactly
    /// (key + all three modifier flags)?</summary>
    public bool MatchesEvent(Event e) =>
        e != null && e.keyCode == Key && e.control == Ctrl && e.shift == Shift && e.alt == Alt;

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
        KeyCode.Alpha0 => "0", KeyCode.Alpha1 => "1", KeyCode.Alpha2 => "2", KeyCode.Alpha3 => "3",
        KeyCode.Alpha4 => "4", KeyCode.Alpha5 => "5", KeyCode.Alpha6 => "6", KeyCode.Alpha7 => "7",
        KeyCode.Alpha8 => "8", KeyCode.Alpha9 => "9",
        _ => key.ToString(),
    };
}
