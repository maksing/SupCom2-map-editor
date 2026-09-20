using System.Collections.Generic;
using UnityEngine;

/// <summary>Groups actions for the rebind panel's sectioned list — mirrors
/// the tabs/areas each action's hotkey actually lives in, not the file it
/// happens to be implemented in.</summary>
public enum SC2HotkeyCategory
{
    Global,
    Camera,
    Brush,
    Costs,
    Objects,
    Decals,
    TextureBrowser,
}

/// <summary>Static metadata for one remappable action: its stable id (used
/// as the PlayerPrefs/lookup key — never change an existing Id, only add
/// new ones, so a saved rebind isn't silently orphaned), its default
/// binding(s), and where it shows up in the rebind UI. An action with two
/// Defaults (e.g. Redo) is satisfied by either binding — see
/// SC2Hotkeys.Matches/ModifierHeld.</summary>
public sealed class SC2HotkeyAction
{
    public readonly string Id;
    public readonly string DisplayName;
    public readonly SC2HotkeyCategory Category;
    public readonly SC2HotkeyBinding[] Defaults;

    public bool IsModifierOnly => Defaults.Length > 0 && Defaults[0].IsModifierOnly;

    public SC2HotkeyAction(string id, string displayName, SC2HotkeyCategory category, params SC2HotkeyBinding[] defaults)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        Defaults = defaults;
    }
}

public static class SC2HotkeyActions
{
    private static SC2HotkeyBinding Key(KeyCode k, bool ctrl = false, bool shift = false, bool alt = false) => new SC2HotkeyBinding(k, ctrl, shift, alt);
    private static SC2HotkeyBinding Modifier(bool ctrl = false, bool shift = false, bool alt = false) => new SC2HotkeyBinding(KeyCode.None, ctrl, shift, alt);

    public static readonly SC2HotkeyAction[] All =
    {
        new SC2HotkeyAction("Global.Undo", "Undo", SC2HotkeyCategory.Global, Key(KeyCode.Z, ctrl: true)),
        new SC2HotkeyAction("Global.Redo", "Redo", SC2HotkeyCategory.Global, Key(KeyCode.Y, ctrl: true), Key(KeyCode.Z, ctrl: true, shift: true)),
        new SC2HotkeyAction("Global.Save", "Save", SC2HotkeyCategory.Global, Key(KeyCode.S, ctrl: true)),
        new SC2HotkeyAction("Global.Export", "Export map", SC2HotkeyCategory.Global, Key(KeyCode.E, ctrl: true)),
        new SC2HotkeyAction("Global.MeasureTool", "Switch to Measure tool", SC2HotkeyCategory.Global, Key(KeyCode.M)),
        new SC2HotkeyAction("Global.FocusCamera", "Focus camera on selection/map", SC2HotkeyCategory.Global, Key(KeyCode.F)),
        new SC2HotkeyAction("Global.ResetView", "Reset camera view", SC2HotkeyCategory.Global, Key(KeyCode.Home)),
        new SC2HotkeyAction("Global.CycleMainTab", "Cycle main tab", SC2HotkeyCategory.Global, Key(KeyCode.Tab)),

        new SC2HotkeyAction("Camera.MoveForward", "Fly camera forward", SC2HotkeyCategory.Camera, Key(KeyCode.W)),
        new SC2HotkeyAction("Camera.MoveBack", "Fly camera back", SC2HotkeyCategory.Camera, Key(KeyCode.S)),
        new SC2HotkeyAction("Camera.MoveLeft", "Fly camera left", SC2HotkeyCategory.Camera, Key(KeyCode.A)),
        new SC2HotkeyAction("Camera.MoveRight", "Fly camera right", SC2HotkeyCategory.Camera, Key(KeyCode.D)),
        new SC2HotkeyAction("Camera.MoveUp", "Fly camera up", SC2HotkeyCategory.Camera, Key(KeyCode.E)),
        new SC2HotkeyAction("Camera.MoveDown", "Fly camera down", SC2HotkeyCategory.Camera, Key(KeyCode.Q)),

        new SC2HotkeyAction("Brush.StrengthScrollModifier", "Scroll adjusts brush strength", SC2HotkeyCategory.Brush, Modifier(ctrl: true)),
        new SC2HotkeyAction("Brush.HardnessScrollModifier", "Scroll adjusts brush hardness (Geometry only)", SC2HotkeyCategory.Brush, Modifier(alt: true)),
        new SC2HotkeyAction("Brush.RadiusResizeGesture", "Hold + move mouse to resize brush", SC2HotkeyCategory.Brush, Key(KeyCode.F)),

        new SC2HotkeyAction("Costs.EraseModifier", "Hold to erase while painting", SC2HotkeyCategory.Costs, Modifier(ctrl: true)),

        new SC2HotkeyAction("Objects.ConnectMarkers", "Connect selected markers", SC2HotkeyCategory.Objects, Key(KeyCode.R, ctrl: true)),
        new SC2HotkeyAction("Objects.DisconnectMarkers", "Disconnect selected markers", SC2HotkeyCategory.Objects, Key(KeyCode.R, alt: true)),
        new SC2HotkeyAction("Objects.RotateGesture", "Start rotate gesture", SC2HotkeyCategory.Objects, Key(KeyCode.R)),
        new SC2HotkeyAction("Objects.ScaleGesture", "Start scale gesture", SC2HotkeyCategory.Objects, Key(KeyCode.S)),
        new SC2HotkeyAction("Objects.LiveRotateWhilePlacing", "Hold to rotate while placing", SC2HotkeyCategory.Objects, Key(KeyCode.E)),
        new SC2HotkeyAction("Objects.DeleteSelection", "Delete selection", SC2HotkeyCategory.Objects, Key(KeyCode.Delete), Key(KeyCode.Backspace)),
        new SC2HotkeyAction("Objects.Copy", "Copy selection", SC2HotkeyCategory.Objects, Key(KeyCode.C, ctrl: true)),
        new SC2HotkeyAction("Objects.Paste", "Paste", SC2HotkeyCategory.Objects, Key(KeyCode.V, ctrl: true)),
        new SC2HotkeyAction("Objects.BoxSelectAdditiveModifier", "Hold for additive box-select", SC2HotkeyCategory.Objects, Modifier(ctrl: true), Modifier(shift: true)),
        new SC2HotkeyAction("Objects.SingleSelectAdditiveModifier", "Hold for additive single select", SC2HotkeyCategory.Objects, Modifier(ctrl: true)),
        new SC2HotkeyAction("Objects.PropsBrushEraseModifier", "Hold to erase with Props brush", SC2HotkeyCategory.Objects, Modifier(ctrl: true)),

        new SC2HotkeyAction("Decals.DeleteSelection", "Delete selected decal", SC2HotkeyCategory.Decals, Key(KeyCode.Delete)),
        new SC2HotkeyAction("Decals.MoveGesture", "Start move gesture", SC2HotkeyCategory.Decals, Key(KeyCode.G)),
        new SC2HotkeyAction("Decals.RotateGesture", "Start rotate gesture", SC2HotkeyCategory.Decals, Key(KeyCode.R)),
        new SC2HotkeyAction("Decals.ScaleGesture", "Start scale gesture", SC2HotkeyCategory.Decals, Key(KeyCode.S)),

        new SC2HotkeyAction("TextureBrowser.FocusSearch", "Focus search field", SC2HotkeyCategory.TextureBrowser, Key(KeyCode.F, ctrl: true)),
    };

    private static Dictionary<string, SC2HotkeyAction> _byId;

    public static SC2HotkeyAction Find(string id)
    {
        if (_byId == null)
        {
            _byId = new Dictionary<string, SC2HotkeyAction>(All.Length);
            foreach (var a in All) _byId[a.Id] = a;
        }
        _byId.TryGetValue(id, out var action);
        return action;
    }
}
