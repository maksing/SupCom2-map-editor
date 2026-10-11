#if !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime (Build &amp; Run) replacements for two Editor-only widgets that the
/// UnityEditorRuntimeShim previously faked with controls that don't hold up in
/// the unified panel's narrow, fixed-width layout:
///   - EditorGUILayout.Popup used to render as "&lt; Label &gt;" prev/next buttons,
///     where the "&lt;"/"&gt;" buttons added ~48px *on top of* the width the caller
///     requested, so the right-hand arrow (and often the label) fell outside the
///     panel and got clipped by its BeginArea.
///   - EditorGUILayout.ColorField used to render as three bare 0-1 sliders
///     squeezed into whatever tiny swatch rect the caller sized (e.g. 42x20px),
///     which is unusable for picking a color.
///
/// Both are replaced with a single-button trigger (showing the current label or
/// color) that opens a floating overlay list/palette, drawn once per frame from
/// SC2GameViewEditorUI.OnGUI via DrawOverlays() — mirroring the existing
/// SC2TerrainShell.DrawRuntimeTexturePickerOverlay pattern so the popup renders
/// on top of, and unclipped by, the panel that triggered it.
/// </summary>
internal static class SC2RuntimeDropdown
{
    private static string _openKey;
    private static string[] _openOptions;
    private static Rect _anchorRect;
    private static Vector2 _scroll;

    private static string _pendingKey;
    private static int _pendingResult = -1;
    private static string[] _pendingOptions;

    public static bool IsOpen(string key) => _openKey == key;
    public static bool IsOpen(int controlId) => IsOpen(ActionKey(controlId));
    public static bool IsAnyOpen => !string.IsNullOrEmpty(_openKey);

    public static void Toggle(string key, string[] options, Rect anchorScreenRect)
    {
        if (_openKey == key)
        {
            _openKey = null;
            return;
        }

        _openKey = key;
        _openOptions = options;
        _anchorRect = anchorScreenRect;
        _scroll = Vector2.zero;
    }

    public static void Toggle(int controlId, string[] options, Rect anchorScreenRect) =>
        Toggle(ActionKey(controlId), options, anchorScreenRect);

    public static void UpdateAnchor(string key, Rect anchorScreenRect)
    {
        if (_openKey == key)
            _anchorRect = anchorScreenRect;
    }

    public static void UpdateAnchor(int controlId, Rect anchorScreenRect) =>
        UpdateAnchor(ActionKey(controlId), anchorScreenRect);

    public static int ConsumePendingResult(string key, int fallback)
    {
        if (_pendingKey != key)
            return fallback;

        _pendingKey = null;
        return _pendingResult;
    }

    public static int ConsumePendingResult(int controlId, int fallback) =>
        ConsumePendingResult(ActionKey(controlId), fallback);

    /// <summary>
    /// For one-off "action" dropdowns not bound to a persistent selected-index
    /// variable (e.g. a "Load Terrain..." menu item) — returns the label text
    /// of whatever option was clicked, using the exact options array that was
    /// open at click time (so it can't drift if the caller's list changes
    /// between opening the dropdown and consuming the result).
    /// </summary>
    public static bool TryConsumeLabel(int controlId, out string label)
    {
        label = null;
        if (_pendingKey != ActionKey(controlId))
            return false;

        var index = _pendingResult;
        _pendingKey = null;
        if (_pendingOptions != null && index >= 0 && index < _pendingOptions.Length)
        {
            label = _pendingOptions[index];
            return true;
        }
        return false;
    }

    public static void DrawOverlays()
    {
        if (string.IsNullOrEmpty(_openKey) || _openOptions == null || _openOptions.Length == 0)
            return;

        var rowHeight = SC2UiScale.Pxf(20f);
        var maxListHeight = SC2UiScale.Pxf(220f);
        var width = Mathf.Clamp(_anchorRect.width, SC2UiScale.Pxf(120f), Mathf.Max(SC2UiScale.Pxf(120f), Screen.width - 20f));
        var height = Mathf.Min(maxListHeight, _openOptions.Length * rowHeight + 6f);

        var x = Mathf.Clamp(_anchorRect.x, 4f, Mathf.Max(4f, Screen.width - width - 4f));
        var y = _anchorRect.yMax + 2f;
        if (y + height > Screen.height - 4f)
            y = Mathf.Max(4f, _anchorRect.y - height - 2f);

        var listRect = new Rect(x, y, width, height);

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && !listRect.Contains(e.mousePosition))
        {
            _openKey = null;
            return;
        }

        var previousDepth = GUI.depth;
        GUI.depth = -2000;

        GUI.Box(listRect, GUIContent.none, GUI.skin.box);
        GUILayout.BeginArea(listRect);
        _scroll = GUILayout.BeginScrollView(_scroll);
        var rowStyle = SC2UiScale.PopupStyle();
        for (var i = 0; i < _openOptions.Length; i++)
        {
            if (GUILayout.Button(_openOptions[i], rowStyle, GUILayout.Height(rowHeight - 2f)))
            {
                _pendingKey = _openKey;
                _pendingResult = i;
                _pendingOptions = _openOptions;
                _openKey = null;
            }
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();

        GUI.depth = previousDepth;

        if (e != null && e.type != EventType.Layout && e.type != EventType.Repaint)
            e.Use();
    }

    private static string ActionKey(int controlId) => "action-dropdown:" + controlId;
}

/// <summary>
/// Runtime replacement for the Editor's native color-picker window: a small
/// full picker: the hue and saturation wheel with a value bar, a hex field, red, green and blue sliders and a row of preset
/// swatches (SC2EnvFieldGui.DrawPickerBody). The field takes the colour live while it is dragged or typed.
/// </summary>
internal static class SC2RuntimeColorPalette
{
    // A broad-enough fixed palette (grayscale ramp + a spread of hues at two
    // saturation/value levels) to distinguish materials/pairs by color without
    // needing full HSV sliders.
    private static readonly Color[] Palette = BuildPalette();

    private static Color[] BuildPalette()
    {
        var colors = new List<Color>
        {
            Color.white,
            new Color(0.82f, 0.82f, 0.82f), new Color(0.62f, 0.62f, 0.62f),
            new Color(0.42f, 0.42f, 0.42f), new Color(0.22f, 0.22f, 0.22f), Color.black
        };

        for (var i = 0; i < 12; i++)
        {
            var hue = i / 12f;
            colors.Add(Color.HSVToRGB(hue, 0.65f, 0.95f));
        }
        for (var i = 0; i < 12; i++)
        {
            var hue = i / 12f;
            colors.Add(Color.HSVToRGB(hue, 0.85f, 0.55f));
        }

        return colors.ToArray();
    }

    private static string _openFieldKey;
    private static Rect _anchorRect;
    private static Color _pendingResult;
    private static string _pendingFieldKey;
    private static readonly SC2EnvFieldGui.PickerState _state = new SC2EnvFieldGui.PickerState();

    /// <summary>Where the open picker is drawn (empty when none is), so what is painted after the overlays (the viewport's overlay
    /// lines) can leave that area alone.</summary>
    public static Rect PanelRect { get; private set; }

    public static bool IsOpen(string fieldKey) => _openFieldKey == fieldKey;
    public static bool IsAnyOpen => !string.IsNullOrEmpty(_openFieldKey);

    /// <summary>Opens (or closes) the picker of a field, starting from the field's current colour.</summary>
    public static void Toggle(string fieldKey, Rect anchorScreenRect, Color current)
    {
        _openFieldKey = _openFieldKey == fieldKey ? null : fieldKey;
        _anchorRect = anchorScreenRect;
        if (_openFieldKey == fieldKey)
        {
            _state.Set(current);
            _state.HexText = "#" + ColorUtility.ToHtmlStringRGB(current);
        }
    }

    public static void UpdateAnchor(string fieldKey, Rect anchorScreenRect)
    {
        if (_openFieldKey == fieldKey)
            _anchorRect = anchorScreenRect;
    }

    public static Color ConsumePendingResult(string fieldKey, Color fallback)
    {
        if (_pendingFieldKey != fieldKey)
            return fallback;

        _pendingFieldKey = null;
        return _pendingResult;
    }

    public static void DrawOverlays()
    {
        if (string.IsNullOrEmpty(_openFieldKey))
        {
            PanelRect = Rect.zero;
            return;
        }

        var size = SC2EnvFieldGui.PickerSize(Palette.Length);
        var x = Mathf.Clamp(_anchorRect.x, 4f, Mathf.Max(4f, Screen.width - size.x - 4f));
        var y = _anchorRect.yMax + 2f;
        if (y + size.y > Screen.height - 4f)
            y = Mathf.Max(4f, _anchorRect.y - size.y - 2f);

        var panelRect = new Rect(x, y, size.x, size.y);
        PanelRect = panelRect;

        var e = Event.current;
        if (e != null && ((e.type == EventType.MouseDown && !panelRect.Contains(e.mousePosition))
                          || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)))
        {
            _openFieldKey = null;
            if (e.type == EventType.KeyDown) e.Use();
            return;
        }

        var previousDepth = GUI.depth;
        GUI.depth = -2000;

        GUI.Box(panelRect, GUIContent.none, GUI.skin.box);
        if (SC2EnvFieldGui.DrawPickerBody(panelRect, _state, Palette))
        {
            _pendingFieldKey = _openFieldKey;
            _pendingResult = _state.Color;
            GUI.changed = true;
        }

        GUI.depth = previousDepth;

        if (e != null && e.type != EventType.Layout && e.type != EventType.Repaint)
            e.Use();
    }
}

/// <summary>
/// Runtime confirm dialog for destructive actions (e.g. "Delete Saved Terrain").
///
/// UnityEditorRuntimeShim's EditorUtility.DisplayDialog(...) always returns
/// true outside the Editor (there is no OS-level modal dialog in a standalone
/// player) — every `if (button && EditorUtility.DisplayDialog(...))` call site
/// in the codebase therefore proceeds unconditionally in a build, with no way
/// to actually cancel. That's tolerable for the existing call sites (delete
/// material/pair, assign-all/clear-all — all recoverable via the new terrain
/// Undo stack), but a "delete this saved file from disk" action needs a real,
/// cancelable confirmation. Since a real dialog can't block the single IMGUI
/// thread, this is asynchronous: Open() registers a callback, the callback
/// runs on a later frame when the user actually clicks "Delete".
/// </summary>
internal static class SC2RuntimeConfirm
{
    private static bool _open;
    private static string _title;
    private static string _message;
    private static string _confirmLabel;
    private static System.Action _onConfirm;

    public static bool IsOpen => _open;

    public static void Open(string title, string message, string confirmLabel, System.Action onConfirm)
    {
        _open = true;
        _title = title;
        _message = message;
        _confirmLabel = confirmLabel;
        _onConfirm = onConfirm;
    }

    public static void DrawOverlays()
    {
        if (!_open)
            return;

        const float width = 320f;
        const float height = 140f;
        var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        var previousDepth = GUI.depth;
        GUI.depth = -3000;

        var scrim = new Rect(0f, 0f, Screen.width, Screen.height);
        var previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(scrim, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUI.Box(rect, GUIContent.none, GUI.skin.box);
        GUILayout.BeginArea(rect);
        GUILayout.Space(8f);
        GUILayout.Label(_title, GUI.skin.label);
        GUILayout.Space(4f);
        var messageStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        GUILayout.Label(_message, messageStyle, GUILayout.Height(SC2UiScale.Pxf(56f)));
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Cancel", GUILayout.Width(SC2UiScale.Pxf(90f)), GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            _open = false;
            _onConfirm = null;
        }
        if (GUILayout.Button(_confirmLabel, GUILayout.Width(SC2UiScale.Pxf(90f)), GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            _open = false;
            var callback = _onConfirm;
            _onConfirm = null;
            callback?.Invoke();
        }
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        GUI.depth = previousDepth;

        // Swallow all input while the dialog is open — it's modal.
        var e = Event.current;
        if (e != null && e.type != EventType.Layout && e.type != EventType.Repaint)
            e.Use();
    }
}
#endif
