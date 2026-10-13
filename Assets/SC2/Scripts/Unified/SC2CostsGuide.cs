using UnityEngine;

/// <summary>
/// The guide of the Costs tab: a short text on what costs are and in which order to edit them, with the costs grids of two original
/// maps as small pictures. A picture opens full screen, where it can be zoomed with the wheel and moved by dragging.
/// </summary>
public static class SC2CostsGuide
{
    private sealed class Picture
    {
        public readonly string Resource;
        public readonly string Caption;
        public Texture2D Texture;
        public bool Missing;

        public Picture(string resource, string caption)
        {
            Resource = resource;
            Caption = caption;
        }

        public Texture2D Load()
        {
            if (Texture == null && !Missing)
            {
                Texture = Resources.Load<Texture2D>("CostsGuide/" + Resource);
                Missing = Texture == null;
                if (Texture != null) Texture.wrapMode = TextureWrapMode.Clamp;
            }
            return Texture;
        }
    }

    private sealed class Group
    {
        public readonly string Title;
        public readonly Picture[] Pictures;

        public Group(string title, params Picture[] pictures)
        {
            Title = title;
            Pictures = pictures;
        }
    }

    private static readonly Group[] Groups =
    {
        new Group("Seton's Clutch, 5 blocks (water)",
            new Picture("SC2_MP_001_b0", "Tanks"),
            new Picture("SC2_MP_001_b1", "Buildings"),
            new Picture("SC2_MP_001_b2", "Megaliths"),
            new Picture("SC2_MP_001_b3", "ACU, Fatboy"),
            new Picture("SC2_MP_001_b4", "Aeon tanks")),
        new Group("Open Palms, 3 blocks (land)",
            new Picture("SC2_MP_002_b0", "Tanks"),
            new Picture("SC2_MP_002_b1", "Buildings and engineers"),
            new Picture("SC2_MP_002_b2", "ACU and experimentals")),
    };

    private const int ThumbnailColumns = 3;
    private const float MaxZoom = 32f;

    private static int _viewerGroup = -1;
    private static int _viewerIndex;
    /// <summary>How far the picture is zoomed beyond the size that fits the window (1 = fits).</summary>
    private static float _zoom = 1f;
    /// <summary>Where the centre of the picture lies relative to the centre of the picture area, in screen pixels.</summary>
    private static Vector2 _pan;
    private static bool _dragging;
    private static GUISkin _styleSkin;
    private static GUIStyle _captionStyle;
    private static GUIStyle _titleStyle;
    private static GUIStyle _hintStyle;

    /// <summary>True while a picture is shown full screen: the panels and the viewport take no input then.</summary>
    public static bool IsViewerOpen => _viewerGroup >= 0;

    public static void CloseViewer()
    {
        _viewerGroup = -1;
        _dragging = false;
    }

    private static void OpenViewer(int group, int index)
    {
        _viewerGroup = group;
        _viewerIndex = index;
        ResetView();
    }

    private static void ResetView()
    {
        _zoom = 1f;
        _pan = Vector2.zero;
        _dragging = false;
    }

    // ==================================================================
    // The guide inside the Costs panel
    // ==================================================================

    /// <summary>The guide follows the map's block set the first time it is shown; the reader can then switch between the two paths.</summary>
    private static int _path = -1;

    public static void DrawContent(float width, bool playableWater)
    {
        EnsureStyles();
        if (_path < 0) _path = playableWater ? 1 : 0;

        SC2TerrainShell.DrawUnifiedSectionLabel("What it is");
        SC2TerrainShell.DrawUnifiedNote(
            "Costs tell the game where units can walk and sail. The game does not use the terrain heights for this, it reads a separate grid " +
            "(one cell per world unit). Without correct costs units get stuck or climb cliffs.\n\n" +
            "Each block is the grid for one kind of unit, and bigger units get fatter walls. Only block 0 is drawn by hand: every other block " +
            "is computed from it and updates after each change.");

        SC2TerrainShell.DrawUnifiedSectionLabel("Does the map have water that units use?");
        _path = SC2TerrainShell.DrawUnifiedSegmentGrid(_path, new[] { "No water (3 blocks)", "Water (5 blocks)" }, 2);
        GUILayout.Space(SC2UiScale.Pxf(6f));

        var erase = SC2Hotkeys.Label("Costs.EraseModifier");
        if (_path == 0)
        {
            SC2TerrainShell.DrawUnifiedNote("Blocks: Tanks, Buildings and engineers, ACU and experimentals.");
            DrawExamples(width, 1);
            SC2TerrainShell.DrawUnifiedSectionLabel("Steps");
            SC2TerrainShell.DrawUnifiedNote(
                "1. Finish the terrain first.\n" +
                "2. Pick 3 Blocks (Land) at the top of the Costs panel. Switching later discards painted costs.\n" +
                "3. On block 0 set Max slope and Min island size, then press Auto-generate from height.\n" +
                "4. Fix block 0 with the Impassable and Passable brushes. Hold " + erase + " to paint the opposite.\n" +
                "5. Look at the other two blocks. Outline opens outline cells you do not need.");
        }
        else
        {
            SC2TerrainShell.DrawUnifiedNote(
                "Blocks: Tanks, Buildings, Megaliths, ACU and Fatboy, Aeon tanks. Buildings treat all water as impassable; the other " +
                "blocks only get a wall strip along the shore.");
            DrawExamples(width, 0);
            SC2TerrainShell.DrawUnifiedSectionLabel("Steps");
            SC2TerrainShell.DrawUnifiedNote(
                "1. Finish the terrain and set the water level first. The shore walls follow it.\n" +
                "2. Pick 5 Blocks (+ Water) at the top of the Costs panel. Switching later discards painted costs.\n" +
                "3. On block 0 set Max slope, Min island size and Shoreline border thickness, then press Auto-generate from height.\n" +
                "4. Fix block 0 with the Impassable and Passable brushes. Hold " + erase + " to paint the opposite.\n" +
                "5. On the Aeon tanks block use Shore to keep single shore cells passable.\n" +
                "6. On the ACU and Fatboy block use Blocked to forbid cells for that block only.\n" +
                "7. Look at the other blocks. Outline opens outline cells you do not need.");
        }

        SC2TerrainShell.DrawUnifiedSectionLabel("Good to know");
        SC2TerrainShell.DrawUnifiedNote(
            "Derived blocks cannot be painted. To draw one by hand, turn on its Manual mode.\n" +
            "Auto-generate overwrites block 0. If you change the terrain later, run it again.\n" +
            "Hold " + SC2Hotkeys.Label("Brush.RadiusResizeGesture") + " and move the mouse to resize the brush. Symmetry from the Terrain tab applies.");
    }

    private static void DrawExamples(float width, int groupIndex)
    {
        SC2TerrainShell.DrawUnifiedSectionLabel("Example from the original game");
        SC2TerrainShell.DrawUnifiedNote("White is impassable, black is passable. Click a picture to open it full screen: the wheel zooms, dragging moves it.");
        DrawThumbnails(width, groupIndex);
    }

    private static void DrawThumbnails(float width, int groupIndex)
    {
        var group = Groups[groupIndex];
        GUILayout.Label(group.Title, _titleStyle, GUILayout.Width(width));
        var gap = SC2UiScale.Pxf(6f);
        var cell = Mathf.Floor((width - gap * (ThumbnailColumns - 1)) / ThumbnailColumns);
        for (var start = 0; start < group.Pictures.Length; start += ThumbnailColumns)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(width));
            for (var i = start; i < start + ThumbnailColumns && i < group.Pictures.Length; i++)
            {
                if (i > start) GUILayout.Space(gap);
                GUILayout.BeginVertical(GUILayout.Width(cell));
                var rect = GUILayoutUtility.GetRect(cell, cell, GUILayout.Width(cell), GUILayout.Height(cell));
                DrawThumbnail(rect, group.Pictures[i], groupIndex, i);
                GUILayout.Label(group.Pictures[i].Caption, _captionStyle, GUILayout.Width(cell));
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(gap);
        }
    }

    private static void DrawThumbnail(Rect rect, Picture picture, int groupIndex, int index)
    {
        var evt = Event.current;
        var texture = picture.Load();
        if (evt.type == EventType.Repaint)
        {
            DrawRect(rect, SC2Theme.Bg3);
            if (texture != null)
            {
                texture.filterMode = FilterMode.Trilinear;
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
            }
            DrawBorder(rect, rect.Contains(evt.mousePosition) ? SC2Theme.Accent : SC2Theme.Line);
        }
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none) && texture != null)
            OpenViewer(groupIndex, index);
    }

    // ==================================================================
    // Full-screen viewer
    // ==================================================================

    /// <summary>Draws the viewer over everything and handles its input; called last of the overlays, like the other modal windows.</summary>
    public static void DrawOverlay()
    {
        if (!IsViewerOpen) return;
        EnsureStyles();
        var evt = Event.current;
        var group = Groups[_viewerGroup];
        var picture = group.Pictures[_viewerIndex];
        var texture = picture.Load();

        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        DrawRect(screen, new Color(0.02f, 0.02f, 0.03f, 0.96f));

        var barHeight = SC2UiScale.Pxf(44f);
        var area = new Rect(0f, barHeight, screen.width, screen.height - barHeight);
        var imageRect = ImageRect(area, texture, out var scale);

        if (texture != null)
        {
            GUI.BeginGroup(area);
            texture.filterMode = scale > 1.01f ? FilterMode.Point : FilterMode.Trilinear;
            var local = new Rect(imageRect.x - area.x, imageRect.y - area.y, imageRect.width, imageRect.height);
            GUI.DrawTexture(local, texture, ScaleMode.StretchToFill, false);
            GUI.EndGroup();
            DrawBorder(Rect.MinMaxRect(Mathf.Max(imageRect.xMin, area.xMin), Mathf.Max(imageRect.yMin, area.yMin),
                Mathf.Min(imageRect.xMax, area.xMax), Mathf.Min(imageRect.yMax, area.yMax)), SC2Theme.LineStrong);
        }

        DrawBar(new Rect(0f, 0f, screen.width, barHeight), group, scale, texture);

        // What the bar left alone belongs to the picture: zoom, drag, keys.
        if (evt != null && evt.type != EventType.Used)
            HandleInput(evt, area, imageRect, group);

        // Nothing under the viewer takes a click, a drag, the wheel or a key while it is open.
        if (IsViewerOpen && evt != null && evt.type != EventType.Repaint && evt.type != EventType.Layout && evt.type != EventType.Used
            && (evt.isMouse || evt.type == EventType.ScrollWheel || evt.type == EventType.KeyDown))
            evt.Use();
    }

    private static Rect ImageRect(Rect area, Texture2D texture, out float scale)
    {
        if (texture == null)
        {
            scale = 1f;
            return new Rect(area.center.x, area.center.y, 0f, 0f);
        }
        var margin = SC2UiScale.Pxf(24f);
        var fit = Mathf.Min((area.width - margin * 2f) / texture.width, (area.height - margin * 2f) / texture.height);
        scale = Mathf.Max(0.01f, fit) * _zoom;
        var size = new Vector2(texture.width * scale, texture.height * scale);
        var center = area.center + _pan;
        return new Rect(center.x - size.x * 0.5f, center.y - size.y * 0.5f, size.x, size.y);
    }

    private static void DrawBar(Rect bar, Group group, float scale, Texture2D texture)
    {
        DrawRect(bar, SC2Theme.Bg1);
        DrawRect(new Rect(bar.x, bar.yMax - 1f, bar.width, 1f), SC2Theme.Line);
        var pad = SC2UiScale.Pxf(12f);
        var buttonHeight = bar.height - SC2UiScale.Pxf(14f);
        var y = bar.y + SC2UiScale.Pxf(7f);

        var closeWidth = SC2UiScale.Pxf(70f);
        var x = bar.xMax - pad - closeWidth;
        if (GUI.Button(new Rect(x, y, closeWidth, buttonHeight), "Close"))
        {
            CloseViewer();
            return;
        }

        var smallWidth = SC2UiScale.Pxf(60f);
        var gap = SC2UiScale.Pxf(6f);
        x -= gap + smallWidth;
        if (GUI.Button(new Rect(x, y, smallWidth, buttonHeight), "100%") && texture != null)
            ZoomToActualSize(scale);
        x -= gap + smallWidth;
        if (GUI.Button(new Rect(x, y, smallWidth, buttonHeight), "Fit"))
            ResetView();
        var percent = SC2UiScale.Pxf(64f);
        x -= gap + percent;
        GUI.Label(new Rect(x, bar.y, percent, bar.height), Mathf.RoundToInt(scale * 100f) + "%", _hintStyle);

        var arrowWidth = SC2UiScale.Pxf(34f);
        x -= gap * 2f + arrowWidth;
        var many = group.Pictures.Length > 1;
        var wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && many;
        if (GUI.Button(new Rect(x, y, arrowWidth, buttonHeight), ">")) Step(group, 1);
        x -= gap + arrowWidth;
        if (GUI.Button(new Rect(x, y, arrowWidth, buttonHeight), "<")) Step(group, -1);
        GUI.enabled = wasEnabled;

        var label = group.Title + ": " + group.Pictures[_viewerIndex].Caption;
        GUI.Label(new Rect(bar.x + pad, bar.y, Mathf.Max(0f, x - pad * 2f), bar.height), label, _titleStyle);
    }

    private static void Step(Group group, int direction)
    {
        _viewerIndex = (_viewerIndex + direction + group.Pictures.Length) % group.Pictures.Length;
        ResetView();
    }

    /// <summary>Zooms so one texel is one screen pixel, keeping the centre of the window where it is.</summary>
    private static void ZoomToActualSize(float currentScale)
    {
        if (currentScale <= 0f) return;
        var newZoom = Mathf.Clamp(_zoom / currentScale, 1f, MaxZoom);
        _pan *= newZoom / _zoom;
        _zoom = newZoom;
    }

    private static void HandleInput(Event evt, Rect area, Rect imageRect, Group group)
    {
        switch (evt.type)
        {
            case EventType.ScrollWheel:
                if (area.Contains(evt.mousePosition))
                    ZoomAt(evt.mousePosition, area, imageRect, Mathf.Pow(1.25f, -Mathf.Sign(evt.delta.y)));
                evt.Use();
                break;
            case EventType.MouseDown:
                if (!area.Contains(evt.mousePosition)) break;
                if (evt.button == 0 && evt.clickCount >= 2)
                {
                    if (_zoom > 1.01f) ResetView();
                    else ZoomAt(evt.mousePosition, area, imageRect, 4f);
                }
                else if (evt.button == 0 || evt.button == 2)
                    _dragging = true;
                evt.Use();
                break;
            case EventType.MouseDrag:
                if (_dragging)
                {
                    _pan += evt.delta;
                    evt.Use();
                }
                break;
            case EventType.MouseUp:
                _dragging = false;
                evt.Use();
                break;
            case EventType.KeyDown:
                if (evt.keyCode == KeyCode.Escape) CloseViewer();
                else if (evt.keyCode == KeyCode.LeftArrow) Step(group, -1);
                else if (evt.keyCode == KeyCode.RightArrow) Step(group, 1);
                else if (evt.keyCode == KeyCode.Home) ResetView();
                evt.Use();
                break;
        }
    }

    /// <summary>Zooms by <paramref name="factor"/> around the mouse position, so the point under the cursor stays under it.</summary>
    private static void ZoomAt(Vector2 mouse, Rect area, Rect imageRect, float factor)
    {
        var newZoom = Mathf.Clamp(_zoom * factor, 1f, MaxZoom);
        var ratio = newZoom / _zoom;
        var newCenter = mouse - (mouse - imageRect.center) * ratio;
        _zoom = newZoom;
        _pan = Mathf.Approximately(newZoom, 1f) ? Vector2.zero : newCenter - area.center;
    }

    // ==================================================================
    // Drawing helpers
    // ==================================================================

    private static void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _titleStyle != null) return;
        _styleSkin = GUI.skin;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Body, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _titleStyle.normal.textColor = SC2Theme.TextBright;
        _captionStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Caption, wordWrap = true, alignment = TextAnchor.UpperCenter };
        _captionStyle.normal.textColor = SC2Theme.TextSecondary;
        _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2Type.Caption, alignment = TextAnchor.MiddleRight };
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
