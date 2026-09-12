using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Modal "Edit Hotkeys" panel, opened from the View menu. Modeled
/// directly on SC2TextureBrowser's DrawOverlay (raw IMGUI, dimmed
/// full-screen backdrop behind a centered card) so it behaves identically in
/// the Editor Game View and a real Build.</summary>
public static class SC2HotkeysPanel
{
    private static readonly KeyCode[] ModifierKeys =
    {
        KeyCode.LeftControl, KeyCode.RightControl,
        KeyCode.LeftShift, KeyCode.RightShift,
        KeyCode.LeftAlt, KeyCode.RightAlt,
        KeyCode.LeftCommand, KeyCode.RightCommand,
        KeyCode.LeftWindows, KeyCode.RightWindows,
    };

    private static bool _open;
    private static Vector2 _scroll;
    private static string _capturingActionId;
    private static int _capturingSlot;
    private static bool _confirmResetAll;
    private static List<(string ActionA, string ActionB)> _conflicts = new();

    public static bool IsOpen => _open;

    public static void Open()
    {
        _open = true;
        _capturingActionId = null;
        _confirmResetAll = false;
        RefreshConflicts();
    }

    private static void RefreshConflicts() => _conflicts = SC2Hotkeys.FindConflicts();

    public static void DrawOverlay()
    {
        if (!_open) return;
        var evt = Event.current;

        var screen = new Rect(0, 0, Screen.width, Screen.height);
        DrawRect(screen, new Color(0f, 0f, 0f, 0.72f));

        var width = Mathf.Clamp(Screen.width - 96f, 520f, 760f);
        var height = Mathf.Clamp(Screen.height - 96f, 420f, 720f);
        var window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        DrawRect(window, new Color(0.075f, 0.082f, 0.098f, 1f));
        DrawBorder(window, new Color(0.25f, 0.28f, 0.34f, 1f));

        const float headerHeight = 40f;
        const float footerHeight = 46f;
        var warnHeight = _conflicts.Count > 0 ? 20f + 16f * _conflicts.Count : 0f;

        DrawHeader(new Rect(window.x, window.y, window.width, headerHeight));
        var body = new Rect(window.x, window.y + headerHeight, window.width, window.height - headerHeight - footerHeight - warnHeight);
        DrawList(body);
        if (warnHeight > 0f) DrawConflictWarning(new Rect(window.x, body.yMax, window.width, warnHeight));
        DrawFooter(new Rect(window.x, window.yMax - footerHeight, window.width, footerHeight));

        // Escape closes the whole panel when nothing is being captured —
        // every other modal in this codebase (SC2TextureBrowser included)
        // closes on Escape, and without this the only way out is landing a
        // click exactly on the Close button; a modal that's hard to
        // dismiss silently blocks ALL viewport input underneath it
        // (anyModalOpen in SC2GameViewEditorUI), not just this panel.
        if (_capturingActionId == null && evt != null && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            _open = false;
            evt.Use();
            return;
        }

        HandleCapture(evt);

        if (evt != null && evt.type != EventType.Repaint && evt.type != EventType.Layout) evt.Use();
    }

    private static void DrawHeader(Rect rect)
    {
        DrawRect(rect, new Color(0.105f, 0.115f, 0.135f, 1f));
        GUI.Label(new Rect(rect.x + 14f, rect.y + 9f, 220f, 22f), "Edit Hotkeys", Bold());
        if (GUI.Button(new Rect(rect.xMax - 82f, rect.y + 7f, 68f, 26f), "Close")) _open = false;
    }

    private static void DrawList(Rect rect)
    {
        DrawRect(rect, new Color(0.065f, 0.07f, 0.083f, 1f));
        const float rowHeight = 30f;
        const float headerRowHeight = 24f;

        var contentHeight = 4f;
        SC2HotkeyCategory? lastCategory = null;
        foreach (var action in SC2HotkeyActions.All)
        {
            if (lastCategory != action.Category) contentHeight += headerRowHeight;
            lastCategory = action.Category;
            contentHeight += rowHeight;
        }

        var content = new Rect(0, 0, rect.width - 18f, contentHeight);
        _scroll = GUI.BeginScrollView(rect, _scroll, content);

        var y = 4f;
        lastCategory = null;
        foreach (var action in SC2HotkeyActions.All)
        {
            if (lastCategory != action.Category)
            {
                GUI.Label(new Rect(8f, y, content.width - 16f, headerRowHeight - 2f), CategoryLabel(action.Category), MiniBold());
                y += headerRowHeight;
            }
            lastCategory = action.Category;
            DrawRow(new Rect(4f, y, content.width - 8f, rowHeight - 3f), action);
            y += rowHeight;
        }

        GUI.EndScrollView();
    }

    private static string CategoryLabel(SC2HotkeyCategory category) => category switch
    {
        SC2HotkeyCategory.TextureBrowser => "TEXTURE BROWSER",
        _ => category.ToString().ToUpperInvariant(),
    };

    private static void DrawRow(Rect rect, SC2HotkeyAction action)
    {
        DrawRect(rect, new Color(0.11f, 0.12f, 0.145f, 1f));

        const float resetWidth = 46f;
        const float slotWidth = 90f;
        const float gap = 6f;

        var resetRect = new Rect(rect.xMax - resetWidth - 6f, rect.y + 3f, resetWidth, rect.height - 6f);
        if (GUI.Button(resetRect, "Reset", FlatButton()))
        {
            SC2Hotkeys.ResetToDefault(action.Id);
            RefreshConflicts();
        }

        var bindings = SC2Hotkeys.GetBindings(action.Id);
        float controlsX;

        if (action.IsModifierOnly)
        {
            const float toggleSet = 3 * 40f + 2 * 4f;
            controlsX = resetRect.x - gap - toggleSet;
            DrawModifierToggles(new Rect(controlsX, rect.y + 3f, toggleSet, rect.height - 6f), action, bindings);
        }
        else
        {
            var slots = Mathf.Max(1, bindings.Length);
            var bindingsWidth = slots * slotWidth + (slots - 1) * gap;
            controlsX = resetRect.x - gap - bindingsWidth;
            for (var slot = 0; slot < slots; slot++)
            {
                var slotRect = new Rect(controlsX + slot * (slotWidth + gap), rect.y + 3f, slotWidth, rect.height - 6f);
                var binding = slot < bindings.Length ? bindings[slot] : new SC2HotkeyBinding(KeyCode.None);
                DrawBindingButton(slotRect, action, slot, binding);
            }
        }

        GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, controlsX - rect.x - 16f, rect.height - 12f), action.DisplayName, RowLabel());
    }

    private static void DrawBindingButton(Rect rect, SC2HotkeyAction action, int slot, SC2HotkeyBinding binding)
    {
        var capturing = _capturingActionId == action.Id && _capturingSlot == slot;
        var label = capturing ? "Press a key…" : binding.Key == KeyCode.None ? "(unbound)" : binding.ToString();
        if (GUI.Button(rect, label, capturing ? ActiveButton() : FlatButton()))
        {
            _capturingActionId = action.Id;
            _capturingSlot = slot;
        }
    }

    private static void DrawModifierToggles(Rect rect, SC2HotkeyAction action, SC2HotkeyBinding[] bindings)
    {
        var w = (rect.width - 2 * 4f) / 3f;
        DrawModifierToggle(new Rect(rect.x, rect.y, w, rect.height), "Ctrl", HasModifierBinding(bindings, true, false, false),
            v => ToggleModifierBinding(action, true, false, false, v));
        DrawModifierToggle(new Rect(rect.x + w + 4f, rect.y, w, rect.height), "Shift", HasModifierBinding(bindings, false, true, false),
            v => ToggleModifierBinding(action, false, true, false, v));
        DrawModifierToggle(new Rect(rect.x + 2 * (w + 4f), rect.y, w, rect.height), "Alt", HasModifierBinding(bindings, false, false, true),
            v => ToggleModifierBinding(action, false, false, true, v));
    }

    private static void DrawModifierToggle(Rect rect, string label, bool active, Action<bool> onChange)
    {
        var next = GUI.Toggle(rect, active, label, active ? ActiveButton() : FlatButton());
        if (next != active) onChange(next);
    }

    private static bool HasModifierBinding(SC2HotkeyBinding[] bindings, bool ctrl, bool shift, bool alt)
    {
        foreach (var b in bindings)
            if (b.IsModifierOnly && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt) return true;
        return false;
    }

    private static void ToggleModifierBinding(SC2HotkeyAction action, bool ctrl, bool shift, bool alt, bool enable)
    {
        var bindings = new List<SC2HotkeyBinding>(SC2Hotkeys.GetBindings(action.Id));
        if (enable)
        {
            if (!HasModifierBinding(bindings.ToArray(), ctrl, shift, alt))
                bindings.Add(new SC2HotkeyBinding(KeyCode.None, ctrl, shift, alt));
        }
        else
        {
            bindings.RemoveAll(b => b.IsModifierOnly && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt);
        }
        SC2Hotkeys.SetBindings(action.Id, bindings.ToArray());
        RefreshConflicts();
    }

    private static void HandleCapture(Event evt)
    {
        if (_capturingActionId == null || evt == null || evt.type != EventType.KeyDown) return;

        if (evt.keyCode == KeyCode.Escape)
        {
            _capturingActionId = null;
            evt.Use();
            return;
        }

        // A bare modifier press (Ctrl/Shift/Alt on its own) isn't a valid key
        // to bind for a keyed action — keep waiting for the actual key.
        if (evt.keyCode == KeyCode.None || Array.IndexOf(ModifierKeys, evt.keyCode) >= 0)
        {
            evt.Use();
            return;
        }

        var action = SC2HotkeyActions.Find(_capturingActionId);
        if (action == null) { _capturingActionId = null; return; }

        var bindings = new List<SC2HotkeyBinding>(SC2Hotkeys.GetBindings(action.Id));
        while (bindings.Count <= _capturingSlot) bindings.Add(new SC2HotkeyBinding(KeyCode.None));
        bindings[_capturingSlot] = new SC2HotkeyBinding(evt.keyCode, evt.control, evt.shift, evt.alt);
        SC2Hotkeys.SetBindings(action.Id, bindings.ToArray());
        RefreshConflicts();

        _capturingActionId = null;
        evt.Use();
    }

    private static void DrawConflictWarning(Rect rect)
    {
        DrawRect(rect, new Color(0.32f, 0.22f, 0.07f, 0.95f));
        var y = rect.y + 3f;
        GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 16f),
            "Same key used by more than one action (harmless if they're never active at the same time):", MiniBold());
        y += 18f;
        foreach (var (a, b) in _conflicts)
        {
            var an = SC2HotkeyActions.Find(a)?.DisplayName ?? a;
            var bn = SC2HotkeyActions.Find(b)?.DisplayName ?? b;
            GUI.Label(new Rect(rect.x + 14f, y, rect.width - 28f, 16f), "• " + an + "   ↔   " + bn, RowLabel());
            y += 16f;
        }
    }

    private static void DrawFooter(Rect rect)
    {
        DrawRect(rect, new Color(0.105f, 0.115f, 0.135f, 1f));

        if (!_confirmResetAll)
        {
            if (GUI.Button(new Rect(rect.x + 12f, rect.y + 8f, 170f, 28f), "Reset All to Defaults", FlatButton()))
                _confirmResetAll = true;
        }
        else
        {
            GUI.Label(new Rect(rect.x + 12f, rect.y + 13f, 130f, 20f), "Reset everything?", MiniBold());
            if (GUI.Button(new Rect(rect.x + 146f, rect.y + 8f, 58f, 28f), "Yes", FlatButton()))
            {
                SC2Hotkeys.ResetAllToDefaults();
                RefreshConflicts();
                _confirmResetAll = false;
            }
            if (GUI.Button(new Rect(rect.x + 210f, rect.y + 8f, 70f, 28f), "Cancel", FlatButton()))
                _confirmResetAll = false;
        }

        if (GUI.Button(new Rect(rect.xMax - 88f, rect.y + 8f, 76f, 28f), "Close", FlatButton())) _open = false;
    }

    // ---- styles (mirrors SC2TextureBrowser's own EnsureStyles pattern) ----

    private static GUISkin _styleSkin;
    private static GUIStyle _boldStyle;
    private static GUIStyle _miniBoldStyle;
    private static GUIStyle _rowLabelStyle;
    private static GUIStyle _flatButtonStyle;
    private static GUIStyle _activeButtonStyle;

    private static void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _boldStyle != null) return;
        _styleSkin = GUI.skin;
        _boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
        _boldStyle.normal.textColor = Color.white;
        _miniBoldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 10 };
        _miniBoldStyle.normal.textColor = new Color(.62f, .68f, .78f);
        _rowLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _rowLabelStyle.normal.textColor = new Color(.88f, .9f, .94f);
        _flatButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
        _activeButtonStyle = new GUIStyle(_flatButtonStyle);
        _activeButtonStyle.normal.textColor = Color.white;
        _activeButtonStyle.hover.textColor = Color.white;
    }

    private static GUIStyle Bold() { EnsureStyles(); return _boldStyle; }
    private static GUIStyle MiniBold() { EnsureStyles(); return _miniBoldStyle; }
    private static GUIStyle RowLabel() { EnsureStyles(); return _rowLabelStyle; }
    private static GUIStyle FlatButton() { EnsureStyles(); return _flatButtonStyle; }
    private static GUIStyle ActiveButton() { EnsureStyles(); return _activeButtonStyle; }

    private static void DrawBorder(Rect r, Color c)
    {
        DrawRect(new Rect(r.x, r.y, r.width, 1), c);
        DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), c);
        DrawRect(new Rect(r.x, r.y, 1, r.height), c);
        DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), c);
    }

    private static void DrawRect(Rect rect, Color color)
    {
        var old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
