using UnityEngine;

/// <summary>
/// The "Interface Colors" window of the Customize menu: every colour of the interface palette (SC2Theme) with its own picker, so the
/// whole editor can be re-coloured. Colours apply while they are dragged and are kept for the next start. Drawn as an overlay like
/// the hotkeys window, but docked to the right edge and barely dimmed, so the interface behind it stays visible.
/// </summary>
public static class SC2ThemePanel
{
    private const string DecalMarkerKey = "theme:decal-marker";

    private static bool _open;
    private static bool _confirmReset;
    private static Vector2 _scroll;
    private static GUISkin _styleSkin;
    private static GUIStyle _titleStyle;
    private static GUIStyle _noteStyle;
    private static GUIStyle _hintStyle;

    public static bool IsOpen => _open;

    public static void Open()
    {
        _open = true;
        _confirmReset = false;
    }

    public static void Close() => _open = false;

    public static void DrawOverlay()
    {
        if (!_open) return;
        EnsureStyles();
        var evt = Event.current;

        DrawRect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.16f));

        var margin = SC2UiScale.Pxf(16f);
        var width = Mathf.Min(Screen.width - margin * 2f, SC2UiScale.Pxf(440f));
        var height = Mathf.Max(SC2UiScale.Pxf(320f), Screen.height - margin * 2f);
        var window = new Rect(Screen.width - width - margin, margin, width, Mathf.Min(height, Screen.height - margin * 2f));
        DrawRect(window, SC2Theme.Bg2);
        DrawBorder(window, SC2Theme.LineStrong);

        var headerHeight = SC2UiScale.Pxf(42f);
        var footerHeight = SC2UiScale.Pxf(50f);
        var pad = SC2UiScale.Pxf(12f);

        var header = new Rect(window.x, window.y, window.width, headerHeight);
        DrawRect(header, SC2Theme.Bg1);
        DrawRect(new Rect(header.x, header.yMax - 1f, header.width, 1f), SC2Theme.Line);
        GUI.Label(new Rect(header.x + pad, header.y, header.width - pad * 2f - SC2UiScale.Pxf(80f), header.height), "Interface Colors", _titleStyle);
        if (GUI.Button(new Rect(header.xMax - pad - SC2UiScale.Pxf(70f), header.y + SC2UiScale.Pxf(7f), SC2UiScale.Pxf(70f), headerHeight - SC2UiScale.Pxf(14f)), "Close"))
            _open = false;

        var body = new Rect(window.x + pad, header.yMax + pad * 0.5f, window.width - pad * 2f, window.height - headerHeight - footerHeight - pad);
        GUILayout.BeginArea(body);
        _scroll = GUILayout.BeginScrollView(_scroll);
        DrawEntries();
        GUILayout.EndScrollView();
        GUILayout.EndArea();

        var footer = new Rect(window.x, window.yMax - footerHeight, window.width, footerHeight);
        DrawRect(footer, SC2Theme.Bg1);
        DrawRect(new Rect(footer.x, footer.y, footer.width, 1f), SC2Theme.Line);
        DrawFooter(footer, pad);

        if (evt != null && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            _open = false;
            evt.Use();
            return;
        }

        // Nothing outside this window takes a click or the wheel while it is open.
        if (evt != null && evt.type != EventType.Repaint && evt.type != EventType.Layout && evt.type != EventType.Used
            && (evt.isMouse || evt.type == EventType.ScrollWheel) && !window.Contains(evt.mousePosition))
            evt.Use();
    }

    private static void DrawEntries()
    {
        GUILayout.Label("Pick a preset, or click a colour to open its picker. The editor changes as you drag, and the colours are kept for the next start.", _noteStyle);
        GUILayout.Space(SC2UiScale.Pxf(6f));
        SC2ForgeUi.Section("Preset");
        GUILayout.Space(SC2UiScale.Pxf(3f));
        var currentPreset = SC2Theme.CurrentPresetName();
        GUILayout.BeginHorizontal();
        foreach (var preset in SC2Theme.Presets)
        {
            if (GUILayout.Toggle(currentPreset == preset.Name, preset.Name, SC2UnifiedSkin.SelectRowStyle, GUILayout.Height(SC2UiScale.Pxf(28f))) && currentPreset != preset.Name)
                SC2Theme.ApplyPreset(preset);
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(currentPreset == null ? "Custom: some colours were changed by hand." : "Using the " + currentPreset + " colours.", _hintStyle);
        GUILayout.Space(SC2UiScale.Pxf(4f));

        string group = null;
        foreach (var entry in SC2Theme.Entries)
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                GUILayout.Space(SC2UiScale.Pxf(8f));
                SC2ForgeUi.Section(group);
                GUILayout.Space(SC2UiScale.Pxf(3f));
            }
            DrawEntry(entry);
        }

        GUILayout.Space(SC2UiScale.Pxf(8f));
        SC2ForgeUi.Section("Map markers");
        GUILayout.Space(SC2UiScale.Pxf(3f));
        var marker = SC2CustomizationSettings.DecalMarkerColor;
        var nextMarker = SC2EnvFieldGui.ColorField("Decal marker   #" + ColorUtility.ToHtmlStringRGB(marker), marker, DecalMarkerKey, false, false, false);
        if (nextMarker != marker) SC2CustomizationSettings.DecalMarkerColor = nextMarker;
        DrawHintRow("The pin drawn at each decal in the viewport", marker != SC2CustomizationSettings.DefaultDecalMarkerColor,
            () => SC2CustomizationSettings.DecalMarkerColor = SC2CustomizationSettings.DefaultDecalMarkerColor);
        GUILayout.Space(SC2UiScale.Pxf(8f));
    }

    private static void DrawEntry(SC2Theme.Entry entry)
    {
        var current = SC2Theme.Get(entry.Id);
        var next = SC2EnvFieldGui.ColorField(entry.Label + "   #" + ColorUtility.ToHtmlStringRGB(current), current, "theme:" + entry.Id, false, false, false);
        if (next != current) SC2Theme.Set(entry.Id, next);
        DrawHintRow(entry.Hint, SC2Theme.IsCustom(entry.Id), () => SC2Theme.Reset(entry.Id));
    }

    private static void DrawHintRow(string hint, bool canReset, System.Action reset)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(SC2UiScale.Pxf(30f));
        GUILayout.Label(hint, _hintStyle);
        if (canReset && GUILayout.Button("Reset", GUILayout.Width(SC2UiScale.Pxf(60f)), GUILayout.Height(SC2UiScale.Pxf(22f))))
            reset();
        GUILayout.EndHorizontal();
        GUILayout.Space(SC2UiScale.Pxf(6f));
    }

    private static void DrawFooter(Rect rect, float pad)
    {
        var buttonHeight = rect.height - SC2UiScale.Pxf(16f);
        var y = rect.y + SC2UiScale.Pxf(8f);
        var anyCustom = SC2Theme.AnyCustom() || SC2CustomizationSettings.DecalMarkerColor != SC2CustomizationSettings.DefaultDecalMarkerColor;
        var wasEnabled = GUI.enabled;
        if (!_confirmReset)
        {
            GUI.enabled = wasEnabled && anyCustom;
            if (GUI.Button(new Rect(rect.x + pad, y, SC2UiScale.Pxf(190f), buttonHeight), "Reset all colors"))
                _confirmReset = true;
        }
        else
        {
            GUI.Label(new Rect(rect.x + pad, y, SC2UiScale.Pxf(130f), buttonHeight), "Reset everything?", _hintStyle);
            if (GUI.Button(new Rect(rect.x + pad + SC2UiScale.Pxf(136f), y, SC2UiScale.Pxf(60f), buttonHeight), "Yes"))
            {
                SC2Theme.ResetAll();
                SC2CustomizationSettings.DecalMarkerColor = SC2CustomizationSettings.DefaultDecalMarkerColor;
                _confirmReset = false;
            }
            if (GUI.Button(new Rect(rect.x + pad + SC2UiScale.Pxf(202f), y, SC2UiScale.Pxf(76f), buttonHeight), "Cancel"))
                _confirmReset = false;
        }
        GUI.enabled = wasEnabled;
    }

    private static void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _titleStyle != null) return;
        _styleSkin = GUI.skin;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Nav, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _titleStyle.normal.textColor = SC2Theme.TextBright;
        _noteStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Caption, wordWrap = true };
        _noteStyle.normal.textColor = SC2Theme.TextSecondary;
        _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Caption, wordWrap = true, alignment = TextAnchor.MiddleLeft };
        _hintStyle.normal.textColor = SC2Theme.TextSecondary;
    }

    private static void DrawBorder(Rect r, Color c)
    {
        DrawRect(new Rect(r.x, r.y, r.width, 1f), c);
        DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
        DrawRect(new Rect(r.x, r.y, 1f, r.height), c);
        DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
    }

    private static void DrawRect(Rect rect, Color color)
    {
        var old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
