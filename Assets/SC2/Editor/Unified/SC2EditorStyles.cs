#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared theme-aware styles for SC2 Editor UI components.
/// Built from GUI.skin primitives so they adapt to light/dark editor themes.
/// </summary>
public static class SC2EditorStyles
{
    private static bool _ready;
    private static readonly GUIStyle[] _buttonBase = new GUIStyle[2];
    private static readonly GUIStyle[] _miniButton = new GUIStyle[2];
    private static readonly GUIStyle[] _labelBase = new GUIStyle[2];
    private static readonly GUIStyle[] _boxBase = new GUIStyle[2];
    private static readonly GUIStyle[] _toggle = new GUIStyle[2];
    private static readonly GUIStyle[] _toolbar = new GUIStyle[2];

    private static GUIStyle _dividerStyle;
    private static GUIStyle _sectionHeaderStyle;
    private static GUIStyle _miniStatusStyle;
    private static GUIStyle _statusPillStyle;
    private static GUIStyle _statusPillActiveStyle;
    private static GUIStyle _headerBoxStyle;
    private static GUIStyle _panelBoxStyle;
    private static GUIStyle _activeTabStyle;
    private static GUIStyle _tabButtonStyle;
    private static GUIStyle _helpBoxInfo;
    private static GUIStyle _helpBoxWarning;

    // ---- Theme-aware color helpers ----

    private static Color DarkHeaderBg => new(0.12f, 0.12f, 0.15f, 0.95f);
    private static Color LightHeaderBg => new(0.88f, 0.90f, 0.94f, 0.95f);

    private static Color DarkHighlight => new(0.22f, 0.45f, 0.80f, 0.35f);
    private static Color LightHighlight => new(0.30f, 0.55f, 0.95f, 0.25f);

    private static Color DarkBorder => new(0.25f, 0.25f, 0.30f, 0.8f);
    private static Color LightBorder => new(0.55f, 0.55f, 0.60f, 0.8f);

    private static Color DarkActiveText => new(1f, 1f, 1f, 0.95f);
    private static Color LightActiveText => new(0.1f, 0.1f, 0.15f, 0.95f);

    private static Color DarkDimText => new(0.55f, 0.55f, 0.60f, 0.85f);
    private static Color LightDimText => new(0.45f, 0.45f, 0.50f, 0.85f);

    // ---- Public accessor ----

    public static bool IsDark => EditorGUIUtility.isProSkin;

    public static Color HeaderBg => IsDark ? DarkHeaderBg : LightHeaderBg;
    public static Color Highlight => IsDark ? DarkHighlight : LightHighlight;
    public static Color Border => IsDark ? DarkBorder : LightBorder;
    public static Color ActiveText => IsDark ? DarkActiveText : LightActiveText;
    public static Color DimText => IsDark ? DarkDimText : LightDimText;

    // ---- Style accessors ----

    public static GUIStyle Divider => _dividerStyle;
    public static GUIStyle SectionHeader => _sectionHeaderStyle;
    public static GUIStyle MiniStatus => _miniStatusStyle;
    public static GUIStyle StatusPill => _statusPillStyle;
    public static GUIStyle StatusPillActive => _statusPillActiveStyle;
    public static GUIStyle HeaderBox => _headerBoxStyle;
    public static GUIStyle PanelBox => _panelBoxStyle;
    public static GUIStyle ActiveTab => _activeTabStyle;
    public static GUIStyle TabButton => _tabButtonStyle;
    public static GUIStyle HelpBoxInfo => _helpBoxInfo;
    public static GUIStyle HelpBoxWarning => _helpBoxWarning;

    // ---- Helpers ----

    public static void EnsureReady()
    {
        if (_ready) return;
        _ready = true;

        // Base styles from skin (adapt to theme)
        var boxSkin = GUI.skin.box;
        var labelSkin = GUI.skin.label;
        var buttonSkin = GUI.skin.button;
        var miniButtonSkin = EditorStyles.miniButton;
        var toggleSkin = EditorStyles.toggle;
        var toolbarSkin = EditorStyles.toolbar;

        for (var skin = 0; skin < 2; skin++)
        {
            var dark = skin == 0;
            _buttonBase[skin] = new GUIStyle(buttonSkin);
            _buttonBase[skin].padding = new RectOffset(6, 6, 3, 3);
            _buttonBase[skin].margin = new RectOffset(1, 1, 0, 0);

            _miniButton[skin] = new GUIStyle(miniButtonSkin);
            _miniButton[skin].padding = new RectOffset(4, 4, 2, 2);
            _miniButton[skin].margin = new RectOffset(1, 1, 0, 0);

            _labelBase[skin] = new GUIStyle(labelSkin);
            _labelBase[skin].fontSize = _labelBase[skin].fontSize;
            _labelBase[skin].wordWrap = true;

            _boxBase[skin] = new GUIStyle(boxSkin);

            _toggle[skin] = new GUIStyle(toggleSkin);
            _toolbar[skin] = new GUIStyle(toolbarSkin);
        }

        // Divider — thin horizontal line
        _dividerStyle = new GUIStyle
        {
            fixedHeight = 1f,
            margin = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
        };

        // Section header
        _sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 4, 2),
            margin = new RectOffset(0, 0, 0, 0),
        };

        // Mini status
        _miniStatusStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            padding = new RectOffset(2, 2, 0, 0),
        };

        // Status pill (header context/status indicators)
        _statusPillStyle = new GUIStyle(_buttonBase[0])
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 10,
            padding = new RectOffset(6, 6, 2, 2),
            margin = new RectOffset(0, 0, 0, 0),
            fixedHeight = 18f,
        };

        _statusPillActiveStyle = new GUIStyle(_statusPillStyle);
        _statusPillActiveStyle.normal.background = MakeSolidTexture(Highlight);
        _statusPillActiveStyle.normal.textColor = ActiveText;

        // Header box
        _headerBoxStyle = new GUIStyle(EditorStyles.helpBox)
        {
            padding = new RectOffset(6, 6, 4, 4),
            margin = new RectOffset(0, 0, 0, 0),
        };

        // Panel box (left sidebar panels)
        _panelBoxStyle = new GUIStyle(EditorStyles.helpBox)
        {
            padding = new RectOffset(6, 6, 6, 6),
            margin = new RectOffset(0, 0, 2, 2),
        };

        // Tab button (inactive)
        _tabButtonStyle = new GUIStyle(EditorStyles.toolbarButton)
        {
            fontSize = 11,
            fontStyle = FontStyle.Normal,
            padding = new RectOffset(6, 6, 2, 2),
            alignment = TextAnchor.MiddleCenter,
        };

        // Active tab button
        _activeTabStyle = new GUIStyle(_tabButtonStyle);
        _activeTabStyle.fontStyle = FontStyle.Bold;
        _activeTabStyle.normal.background = MakeSolidTexture(Highlight);
        _activeTabStyle.normal.textColor = ActiveText;

        // Help boxes
        _helpBoxInfo = new GUIStyle(EditorStyles.helpBox)
        {
            margin = new RectOffset(0, 0, 2, 2),
            padding = new RectOffset(6, 6, 4, 4),
        };

        _helpBoxWarning = new GUIStyle(EditorStyles.helpBox)
        {
            margin = new RectOffset(0, 0, 2, 2),
            padding = new RectOffset(6, 6, 4, 4),
        };
    }

    // ---- Drawing helpers ----

    public static void DrawDivider()
    {
        var r = EditorGUILayout.GetControlRect(false, 1f);
        r.xMin += 4f;
        r.xMax -= 4f;
        GUI.color = Border;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    public static void DrawToolbar(string[] labels, int selectedIndex)
    {
        EnsureReady();
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        for (var i = 0; i < labels.Length; i++)
        {
            var style = (i == selectedIndex) ? ActiveTab : TabButton;
            if (GUILayout.Button(labels[i], style, GUILayout.MaxWidth(labels[i].Length * 10f + 20f)))
                selectedIndex = i; // caller should assign
            GUILayout.Space(2f);
        }
        EditorGUILayout.EndHorizontal();
    }

    public static void DrawHeaderBox(System.Action body)
    {
        EnsureReady();
        EditorGUILayout.BeginVertical(HeaderBox, GUILayout.MinHeight(80f));
        body();
        EditorGUILayout.EndVertical();
    }

    public static void DrawPanelBox(System.Action body)
    {
        EnsureReady();
        EditorGUILayout.BeginVertical(PanelBox);
        body();
        EditorGUILayout.EndVertical();
    }

    // ---- Private helpers ----

    private static Texture2D MakeSolidTexture(Color color)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }
}
#endif
