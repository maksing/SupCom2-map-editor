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

        // Every size below is in design pixels like the fonts (SC2Type), so the panel grows with the UI Scale instead of the
        // text outgrowing fixed-size buttons and rows.
        var width = Mathf.Min(Screen.width - 24f, Mathf.Clamp(Screen.width - 96f, 520f * S, 760f * S));
        var height = Mathf.Min(Screen.height - 24f, Mathf.Clamp(Screen.height - 96f, 420f * S, 720f * S));
        var window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        DrawRect(window, SC2Theme.Bg2);
        DrawBorder(window, SC2Theme.LineStrong);

        var headerHeight = 40f * S;
        var footerHeight = 46f * S;
        // The conflict box is as tall as its wrapped lines, up to a third of the window; beyond that it scrolls.
        var warnContentHeight = ConflictContentHeight(window.width);
        var warnHeight = Mathf.Min(warnContentHeight, window.height * 0.33f);

        DrawHeader(new Rect(window.x, window.y, window.width, headerHeight));
        var body = new Rect(window.x, window.y + headerHeight, window.width, window.height - headerHeight - footerHeight - warnHeight);
        DrawList(body);
        if (warnHeight > 0f) DrawConflictWarning(new Rect(window.x, body.yMax, window.width, warnHeight), warnContentHeight);
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
        DrawRect(rect, SC2Theme.Bg1);
        GUI.Label(new Rect(rect.x + 14f * S, rect.y + 5f * S, rect.width - 150f * S, rect.height - 10f * S), "Edit Hotkeys", Bold());
        if (GUI.Button(new Rect(rect.xMax - 96f * S, rect.y + 6f * S, 84f * S, rect.height - 12f * S), "Close", FlatButton())) _open = false;
    }

    private static void DrawList(Rect rect)
    {
        DrawRect(rect, SC2Theme.Bg0);
        var rowHeight = 30f * S;
        var headerRowHeight = 24f * S;

        var contentHeight = 4f * S;
        SC2HotkeyCategory? lastCategory = null;
        foreach (var action in SC2HotkeyActions.All)
        {
            if (lastCategory != action.Category) contentHeight += headerRowHeight;
            lastCategory = action.Category;
            contentHeight += rowHeight;
        }

        var content = new Rect(0, 0, rect.width - 18f, contentHeight);
        _scroll = GUI.BeginScrollView(rect, _scroll, content);

        var y = 4f * S;
        lastCategory = null;
        foreach (var action in SC2HotkeyActions.All)
        {
            if (lastCategory != action.Category)
            {
                GUI.Label(new Rect(8f * S, y, content.width - 16f * S, headerRowHeight - 2f), CategoryLabel(action.Category), MiniBold());
                y += headerRowHeight;
            }
            lastCategory = action.Category;
            DrawRow(new Rect(4f * S, y, content.width - 8f * S, rowHeight - 3f * S), action);
            y += rowHeight;
        }

        GUI.EndScrollView();
    }

    private static string CategoryLabel(SC2HotkeyCategory category) => category switch
    {
        SC2HotkeyCategory.TextureBrowser => "TEXTURE BROWSER",
        SC2HotkeyCategory.ImportedTerrain => "IMPORTED TERRAIN",
        _ => category.ToString().ToUpperInvariant(),
    };

    private static void DrawRow(Rect rect, SC2HotkeyAction action)
    {
        DrawRect(rect, new Color(0.11f, 0.12f, 0.145f, 1f));

        var resetWidth = 56f * S;
        var slotWidth = 172f * S;
        var gap = 6f * S;
        var pad = 3f * S;

        var resetRect = new Rect(rect.xMax - resetWidth - 6f * S, rect.y + pad, resetWidth, rect.height - 2f * pad);
        if (GUI.Button(resetRect, "Reset", FlatButton()))
        {
            SC2Hotkeys.ResetToDefault(action.Id);
            RefreshConflicts();
        }

        var bindings = SC2Hotkeys.GetBindings(action.Id);
        float controlsX;

        if (action.IsModifierOnly)
        {
            var toggleSet = 3 * 52f * S + 2 * 4f * S;
            controlsX = resetRect.x - gap - toggleSet;
            DrawModifierToggles(new Rect(controlsX, rect.y + pad, toggleSet, rect.height - 2f * pad), action, bindings);
        }
        else
        {
            var slots = Mathf.Max(1, bindings.Length);
            var bindingsWidth = slots * slotWidth + (slots - 1) * gap;
            controlsX = resetRect.x - gap - bindingsWidth;
            for (var slot = 0; slot < slots; slot++)
            {
                var slotRect = new Rect(controlsX + slot * (slotWidth + gap), rect.y + pad, slotWidth, rect.height - 2f * pad);
                var binding = slot < bindings.Length ? bindings[slot] : new SC2HotkeyBinding(KeyCode.None);
                DrawBindingButton(slotRect, action, slot, binding);
            }
        }

        GUI.Label(new Rect(rect.x + 10f * S, rect.y + 2f * S, Mathf.Max(20f, controlsX - rect.x - 16f * S), rect.height - 4f * S), action.DisplayName, RowLabel());
    }

    private static void DrawBindingButton(Rect rect, SC2HotkeyAction action, int slot, SC2HotkeyBinding binding)
    {
        var capturing = _capturingActionId == action.Id && _capturingSlot == slot;
        var label = capturing ? (action.IsMouseAction ? "Press a mouse button…" : "Press a key…") : binding.Key == KeyCode.None ? "(unbound)" : binding.ToString();
        if (GUI.Button(rect, label, capturing ? ActiveButton() : FlatButton()))
        {
            _capturingActionId = action.Id;
            _capturingSlot = slot;
        }
    }

    private static void DrawModifierToggles(Rect rect, SC2HotkeyAction action, SC2HotkeyBinding[] bindings)
    {
        var gap = 4f * S;
        var w = (rect.width - 2 * gap) / 3f;
        DrawModifierToggle(new Rect(rect.x, rect.y, w, rect.height), "Ctrl", HasModifierBinding(bindings, true, false, false),
            v => ToggleModifierBinding(action, true, false, false, v));
        DrawModifierToggle(new Rect(rect.x + w + gap, rect.y, w, rect.height), "Shift", HasModifierBinding(bindings, false, true, false),
            v => ToggleModifierBinding(action, false, true, false, v));
        DrawModifierToggle(new Rect(rect.x + 2 * (w + gap), rect.y, w, rect.height), "Alt", HasModifierBinding(bindings, false, false, true),
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
        if (_capturingActionId == null || evt == null) return;

        // The camera actions are mouse buttons: the next press of one (with the modifiers held) is the binding. A bare left button is
        // never taken, it is the click that works the panel itself; Escape cancels like for a key.
        var mouseAction = SC2HotkeyActions.Find(_capturingActionId)?.IsMouseAction ?? false;
        if (mouseAction)
        {
            if (evt.type == EventType.MouseDown)
            {
                if (evt.button == 0 && !evt.control && !evt.shift && !evt.alt) return;
                if (evt.button > 6) return;
                var mouseBindings = new List<SC2HotkeyBinding>(SC2Hotkeys.GetBindings(_capturingActionId));
                while (mouseBindings.Count <= _capturingSlot) mouseBindings.Add(new SC2HotkeyBinding(KeyCode.None));
                mouseBindings[_capturingSlot] = new SC2HotkeyBinding(KeyCode.Mouse0 + evt.button, evt.control, evt.shift, evt.alt);
                SC2Hotkeys.SetBindings(_capturingActionId, mouseBindings.ToArray());
                RefreshConflicts();
                _capturingActionId = null;
                evt.Use();
                return;
            }
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            {
                _capturingActionId = null;
                evt.Use();
            }
            return;
        }

        if (evt.type != EventType.KeyDown) return;

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

    private const string ConflictTitle = "Same key used by more than one action (harmless if they're never active at the same time):";

    private static string ConflictLine((string ActionA, string ActionB) conflict)
    {
        var an = SC2HotkeyActions.Find(conflict.ActionA)?.DisplayName ?? conflict.ActionA;
        var bn = SC2HotkeyActions.Find(conflict.ActionB)?.DisplayName ?? conflict.ActionB;
        return "• " + an + "   ↔   " + bn;
    }

    /// <summary>The height of the conflict box with every line wrapped to the window's width (0 when there is none).</summary>
    private static float ConflictContentHeight(float windowWidth)
    {
        if (_conflicts.Count == 0) return 0f;
        EnsureStyles();
        var titleWidth = windowWidth - 20f * S;
        var lineWidth = windowWidth - 28f * S - 18f;
        var height = 6f * S + _warnTitleStyle.CalcHeight(new GUIContent(ConflictTitle), titleWidth) + 2f * S;
        foreach (var conflict in _conflicts)
            height += _warnLineStyle.CalcHeight(new GUIContent(ConflictLine(conflict)), lineWidth);
        return height + 4f * S;
    }

    private static Vector2 _conflictScroll;

    private static void DrawConflictWarning(Rect rect, float contentHeight)
    {
        EnsureStyles();
        DrawRect(rect, new Color(0.32f, 0.22f, 0.07f, 0.95f));
        var scrolls = contentHeight > rect.height + 0.5f;
        var innerWidth = rect.width - (scrolls ? 18f : 0f);
        var content = new Rect(0f, 0f, innerWidth, contentHeight);
        _conflictScroll = GUI.BeginScrollView(rect, _conflictScroll, content, false, false);
        var y = 3f * S;
        var titleHeight = _warnTitleStyle.CalcHeight(new GUIContent(ConflictTitle), rect.width - 20f * S);
        GUI.Label(new Rect(10f * S, y, innerWidth - 20f * S, titleHeight), ConflictTitle, _warnTitleStyle);
        y += titleHeight + 2f * S;
        foreach (var conflict in _conflicts)
        {
            var line = ConflictLine(conflict);
            var lineHeight = _warnLineStyle.CalcHeight(new GUIContent(line), innerWidth - 28f * S);
            GUI.Label(new Rect(14f * S, y, innerWidth - 28f * S, lineHeight), line, _warnLineStyle);
            y += lineHeight;
        }
        GUI.EndScrollView();
    }

    private static void DrawFooter(Rect rect)
    {
        DrawRect(rect, SC2Theme.Bg1);

        if (!_confirmResetAll)
        {
            if (GUI.Button(new Rect(rect.x + 12f * S, rect.y + 7f * S, 200f * S, rect.height - 14f * S), "Reset All to Defaults", FlatButton()))
                _confirmResetAll = true;
        }
        else
        {
            GUI.Label(new Rect(rect.x + 12f * S, rect.y + 7f * S, 160f * S, rect.height - 14f * S), "Reset everything?", MiniBold());
            if (GUI.Button(new Rect(rect.x + 176f * S, rect.y + 7f * S, 70f * S, rect.height - 14f * S), "Yes", FlatButton()))
            {
                SC2Hotkeys.ResetAllToDefaults();
                RefreshConflicts();
                _confirmResetAll = false;
            }
            if (GUI.Button(new Rect(rect.x + 252f * S, rect.y + 7f * S, 84f * S, rect.height - 14f * S), "Cancel", FlatButton()))
                _confirmResetAll = false;
        }

        if (GUI.Button(new Rect(rect.xMax - 100f * S, rect.y + 7f * S, 88f * S, rect.height - 14f * S), "Close", FlatButton())) _open = false;
    }

    // ---- styles (mirrors SC2TextureBrowser's own EnsureStyles pattern) ----

    private static GUISkin _styleSkin;
    private static GUIStyle _boldStyle;
    private static GUIStyle _miniBoldStyle;
    private static GUIStyle _rowLabelStyle;
    private static GUIStyle _flatButtonStyle;
    private static GUIStyle _activeButtonStyle;
    private static GUIStyle _warnTitleStyle;
    private static GUIStyle _warnLineStyle;
    private static int _styleFontSize = -1;

    /// <summary>The UI Scale, applied to every size of the panel: the fonts follow it (SC2Type), so the rects have to as well.</summary>
    private static float S => SC2UiScale.Scale;

    private static void EnsureStyles()
    {
        // The fonts change with the UI Scale, so the styles are built again when it does.
        if (_styleSkin == GUI.skin && _boldStyle != null && _styleFontSize == SC2Type.Body) return;
        _styleFontSize = SC2Type.Body;
        _styleSkin = GUI.skin;
        _boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = SC2Type.Nav, alignment = TextAnchor.MiddleLeft };
        _boldStyle.normal.textColor = SC2Theme.TextBright;
        _miniBoldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = SC2Type.Caption };
        _miniBoldStyle.normal.textColor = SC2Theme.TextSecondary;
        _rowLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Body, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _rowLabelStyle.normal.textColor = SC2Theme.TextPrimary;
        _flatButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = SC2Type.Body, alignment = TextAnchor.MiddleCenter };
        _warnTitleStyle = new GUIStyle(_miniBoldStyle) { wordWrap = true, clipping = TextClipping.Overflow };
        _warnLineStyle = new GUIStyle(_rowLabelStyle) { wordWrap = true, clipping = TextClipping.Overflow, alignment = TextAnchor.UpperLeft };
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
