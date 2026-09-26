#if !UNITY_EDITOR
using System;
using UnityEngine;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) {}
        public MenuItem(string itemName, bool isValidateFunction) {}
        public MenuItem(string itemName, bool isValidateFunction, int priority) {}
    }

    public enum MessageType { None, Info, Warning, Error }

    public static class EditorStyles
    {
        private static GUIStyle FallbackLabel => GUI.skin != null && GUI.skin.label != null ? GUI.skin.label : GUIStyle.none;
        private static GUIStyle FallbackTextField => GUI.skin != null && GUI.skin.textField != null ? GUI.skin.textField : FallbackLabel;
        private static GUIStyle FallbackTextArea => GUI.skin != null && GUI.skin.textArea != null ? GUI.skin.textArea : FallbackTextField;
        private static GUIStyle FallbackBox => GUI.skin != null && GUI.skin.box != null ? GUI.skin.box : FallbackLabel;
        private static GUIStyle FallbackButton => GUI.skin != null && GUI.skin.button != null ? GUI.skin.button : FallbackLabel;

        public static GUIStyle boldLabel => FallbackLabel;
        public static GUIStyle miniBoldLabel => FallbackLabel;
        public static GUIStyle miniLabel => FallbackLabel;
        public static GUIStyle centeredGreyMiniLabel => FallbackLabel;
        public static GUIStyle largeLabel => FallbackLabel;
        public static GUIStyle textField => FallbackTextField;
        public static GUIStyle textArea => FallbackTextArea;
        public static GUIStyle toolbar => FallbackBox;
        public static GUIStyle toolbarButton => FallbackButton;
        public static GUIStyle foldout => FallbackLabel;
        public static GUIStyle foldoutHeader => FallbackLabel;
    }

    public static class EditorGUILayout
    {
        // Per-control-type GetControlID hints — see the comment on kPopupHint's
        // first use in Popup() below for why these exist. Real
        // UnityEditor.EditorGUI keeps an equivalent internal per-type hash for
        // exactly this reason.
        private static readonly int kPopupHint = "SC2ShimPopup".GetHashCode();
        private static readonly int kColorFieldHint = "SC2ShimColorField".GetHashCode();
        private static readonly int kObjectFieldHint = "SC2ShimObjectField".GetHashCode();
        private static readonly int kNumericFieldHint = "SC2ShimNumericField".GetHashCode();

        private static string s_numericEditName;
        private static string s_numericEditText;

        /// <summary>Number text box that keeps what the user is typing while it
        /// has focus. Redrawing the text from the number every frame (the
        /// original behaviour) wiped any intermediate text that doesn't parse
        /// yet - a lone "-", "-." or "" - so a negative value could never be
        /// typed at all (the "-" vanished the instant it was entered). The
        /// number is only re-formatted from `value` when the field is NOT
        /// focused, so external changes (Undo, gizmo drags) still show up.</summary>
        private static float NumericTextField(float value, params GUILayoutOption[] options)
        {
            var name = "SC2ShimNumeric_" + GUIUtility.GetControlID(kNumericFieldHint, FocusType.Passive);
            var focused = GUI.GetNameOfFocusedControl() == name;
            var shown = focused && s_numericEditName == name ? s_numericEditText : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            GUI.SetNextControlName(name);
            var text = GUILayout.TextField(shown, options);
            if (GUI.GetNameOfFocusedControl() == name)
            {
                s_numericEditName = name;
                s_numericEditText = text;
            }
            else if (s_numericEditName == name)
            {
                s_numericEditName = null;
                s_numericEditText = null;
            }
            return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : value;
        }

        public sealed class VerticalScope : IDisposable
        {
            public VerticalScope(params GUILayoutOption[] options) { GUILayout.BeginVertical(options); }
            public VerticalScope(GUIStyle style, params GUILayoutOption[] options) { GUILayout.BeginVertical(style, options); }
            public void Dispose() { GUILayout.EndVertical(); }
        }

        public sealed class HorizontalScope : IDisposable
        {
            public HorizontalScope(params GUILayoutOption[] options) { GUILayout.BeginHorizontal(options); }
            public HorizontalScope(GUIStyle style, params GUILayoutOption[] options) { GUILayout.BeginHorizontal(style, options); }
            public void Dispose() { GUILayout.EndHorizontal(); }
        }

        public static void Space() => GUILayout.Space(6f);
        public static void Space(float pixels) => GUILayout.Space(pixels);
        // Real UnityEditor.EditorGUILayout.BeginHorizontal/BeginVertical return
        // the group's own rect synchronously via Unity's internal
        // GUILayoutUtility.BeginLayoutGroup, which isn't reachable through any
        // public API. Calling GUILayoutUtility.GetLastRect() immediately after
        // a plain GUILayout.Begin*() (as this shim used to) is exactly Unity's
        // documented misuse case — "You cannot call GetLast immediately after
        // beginning a group" — and fired on literally every call, every frame,
        // for every one of this shim's ~40 call sites across the project
        // (confirmed via a real Build's Player.log: >192,000 repeats in one
        // session). Returning Rect.zero avoids the error entirely; every real
        // call site in this project already discards this return value except
        // SC2MaterialsPairsMode.DrawRow's row-highlight rect, which was moved
        // to read GetLastRect() right after EndHorizontal() instead (valid
        // there, since the just-closed group is genuinely the last completed
        // layout entry at that point).
        public static Rect BeginHorizontal(params GUILayoutOption[] options)
        {
            GUILayout.BeginHorizontal(options);
            return Rect.zero;
        }
        public static Rect BeginHorizontal(GUIStyle style, params GUILayoutOption[] options)
        {
            GUILayout.BeginHorizontal(style, options);
            return Rect.zero;
        }
        public static void EndHorizontal() => GUILayout.EndHorizontal();
        public static Rect BeginVertical(params GUILayoutOption[] options)
        {
            GUILayout.BeginVertical(options);
            return Rect.zero;
        }
        public static Rect BeginVertical(GUIStyle style, params GUILayoutOption[] options)
        {
            GUILayout.BeginVertical(style, options);
            return Rect.zero;
        }
        public static void EndVertical() => GUILayout.EndVertical();
        public static void LabelField(string label, params GUILayoutOption[] options) => GUILayout.Label(label, options);
        public static void LabelField(string label, GUIStyle style, params GUILayoutOption[] options) => GUILayout.Label(label, style, options);
        public static void LabelField(string label, string label2, params GUILayoutOption[] options) => GUILayout.Label(label + " " + label2, options);
        public static string TextField(string label, string text, params GUILayoutOption[] options)
        {
            BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(92f)));
            var result = GUILayout.TextField(text ?? "", options);
            EndHorizontal();
            return result;
        }
        public static string TextField(string text, params GUILayoutOption[] options) => GUILayout.TextField(text ?? "", options);
        public static string TextArea(string text, params GUILayoutOption[] options) => GUILayout.TextArea(text ?? "", options);
        public static string TextArea(string text, GUIStyle style, params GUILayoutOption[] options) => GUILayout.TextArea(text ?? "", style, options);
        public static float FloatField(string label, float value, params GUILayoutOption[] options)
        {
            BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(92f)));
            var result = NumericTextField(value, options);
            EndHorizontal();
            return result;
        }
        public static bool Toggle(string label, bool value, params GUILayoutOption[] options) => GUILayout.Toggle(value, label, options);
        public static bool Toggle(bool value, params GUILayoutOption[] options) => GUILayout.Toggle(value, GUIContent.none, options);
        public static bool ToggleLeft(string label, bool value, params GUILayoutOption[] options) => GUILayout.Toggle(value, label, options);
        public static int Popup(int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) =>
            Popup(selectedIndex, displayedOptions, null, options);

        public static int Popup(int selectedIndex, string[] displayedOptions, GUIStyle style, params GUILayoutOption[] options)
        {
            var transientKey = "transient-popup:" + GUIUtility.GetControlID(kPopupHint, FocusType.Passive);
            return Popup(transientKey, selectedIndex, displayedOptions, style, options);
        }

        // Persistent data fields must supply a key that does not depend on
        // IMGUI's frame-specific control allocation.
        public static int Popup(string fieldKey, int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) =>
            Popup(fieldKey, selectedIndex, displayedOptions, null, options);

        public static int Popup(string fieldKey, int selectedIndex, string[] displayedOptions, GUIStyle style, params GUILayoutOption[] options)
        {
            if (displayedOptions == null || displayedOptions.Length == 0)
                return selectedIndex;

            // Was "< Label >": the prev/next buttons added ~48px on top of
            // whatever width the caller requested for the whole control, so
            // the ">" (and sometimes the label) overflowed past the panel's
            // fixed width and got clipped. Now a single button that opens a
            // floating list overlay (see SC2RuntimeDropdown), sized exactly
            // to the width the caller asked for.
            selectedIndex = Mathf.Clamp(selectedIndex, 0, displayedOptions.Length - 1);
            // Explicit per-control-type hint (real UnityEditor.EditorGUI uses the
            // same pattern internally, e.g. its own hashed constants for color
            // fields vs popups). GetControlID(FocusType.Passive) with NO hint
            // pulls from one hash bucket shared by EVERY control in the app that
            // omits a hint (ColorField/ObjectField here included) — disambiguated
            // only by a per-frame call counter. If an unrelated control elsewhere
            // changes how many hint-less GetControlID calls happen before this
            // one between frames (a different tab's layout, a popup opening/
            // closing, this file's own ColorField/ObjectField), this Popup's ID
            // can coincide with a DIFFERENT control's ID that frame — and since
            // SC2RuntimeDropdown/SC2RuntimeColorPalette track "which control is
            // open"/"which control has a pending result" via that raw int ID in
            // static fields shared across the whole app, a collision delivers a
            // pending value picked for ONE control to a completely different one.
            var popupKey = "popup:" + fieldKey;
            var isOpen = SC2RuntimeDropdown.IsOpen(popupKey);
            var label = displayedOptions[selectedIndex] + (isOpen ? "  ▴" : "  ▾");
            var effectiveStyle = style ?? (GUI.skin != null ? GUI.skin.button : GUIStyle.none);
            if (GUILayout.Button(label, effectiveStyle, options))
                SC2RuntimeDropdown.Toggle(popupKey, displayedOptions, GUIToScreenRect(GUILayoutUtility.GetLastRect()));
            else if (isOpen && Event.current.type == EventType.Repaint)
                SC2RuntimeDropdown.UpdateAnchor(popupKey, GUIToScreenRect(GUILayoutUtility.GetLastRect()));

            return SC2RuntimeDropdown.ConsumePendingResult(popupKey, selectedIndex);
        }

        private static Rect GUIToScreenRect(Rect rect)
        {
            var topLeft = GUIUtility.GUIToScreenPoint(new Vector2(rect.x, rect.y));
            return new Rect(topLeft.x, topLeft.y, rect.width, rect.height);
        }
        public static Vector3 Vector3Field(string label, Vector3 value, params GUILayoutOption[] options)
        {
            // `options` (the caller's requested total row width — e.g. the
            // panel's content width) used to be dropped entirely: the outer
            // BeginHorizontal() had no width cap, and each X/Y/Z axis was
            // drawn via the public FloatField(), whose own 92px label (sized
            // for a real field label like "Height Scale", not a 1-letter
            // axis) plus an unconstrained value box meant three of them side
            // by side reliably overflowed a narrow left panel out into the
            // viewport. BeginHorizontal(options) now caps the whole row to
            // the caller's width, and each axis gets a compact label + a
            // value box that shares the remaining space via ExpandWidth.
            LabelField(label, EditorStyles.miniLabel);
            BeginHorizontal(options);
            value.x = CompactAxisField("X", value.x);
            value.y = CompactAxisField("Y", value.y);
            value.z = CompactAxisField("Z", value.z);
            EndHorizontal();
            return value;
        }

        private static float CompactAxisField(string axisLabel, float value)
        {
            GUILayout.Label(axisLabel, GUILayout.Width(SC2UiScale.Pxf(14f)));
            return NumericTextField(value, GUILayout.ExpandWidth(true));
        }
        public static Vector2 Vector2Field(string label, Vector2 value, params GUILayoutOption[] options)
        {
            LabelField(label, EditorStyles.miniLabel);
            BeginHorizontal(options);
            value.x = CompactAxisField("X", value.x);
            value.y = CompactAxisField("Y", value.y);
            EndHorizontal();
            return value;
        }
        public static Vector4 Vector4Field(string label, Vector4 value, params GUILayoutOption[] options)
        {
            LabelField(label, EditorStyles.miniLabel);
            BeginHorizontal(options);
            value.x = CompactAxisField("X", value.x);
            value.y = CompactAxisField("Y", value.y);
            value.z = CompactAxisField("Z", value.z);
            value.w = CompactAxisField("W", value.w);
            EndHorizontal();
            return value;
        }
        public static float Slider(string label, float value, float leftValue, float rightValue, params GUILayoutOption[] options)
        {
            BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(92f)));
            value = GUILayout.HorizontalSlider(value, leftValue, rightValue, GUILayout.ExpandWidth(true));
            var text = GUILayout.TextField(value.ToString("0.##"), GUILayout.Width(SC2UiScale.Pxf(50f)));
            if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                value = parsed;
            EndHorizontal();
            return value;
        }
        public static int IntField(string label, int value, params GUILayoutOption[] options)
        {
            BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(92f)));
            var text = GUILayout.TextField(value.ToString(), options);
            EndHorizontal();
            return int.TryParse(text, out var parsed) ? parsed : value;
        }
        public static Color ColorField(string label, Color value, params GUILayoutOption[] options) =>
            ColorField(new GUIContent(label), value, true, true, false, options);
        public static Color ColorField(GUIContent label, Color value, bool showEyedropper, bool showAlpha, bool hdr, params GUILayoutOption[] options)
        {
            // Was three bare 0-1 sliders crammed into whatever tiny swatch rect
            // the caller sized (often ~42x20px) — unusable for picking a color.
            // Now a swatch button that opens a click-to-pick palette overlay
            // (see SC2RuntimeColorPalette), sized exactly to the space the
            // caller asked for.
            // Do not use IMGUI's generated control ID as the picker identity.
            // Its value depends on the current layout and changes when the user
            // switches a terrain tab. A palette result then could be consumed by
            // a different material swatch. The caller supplies a stable tooltip
            // key (for example "material-color-2") for that reason.
            var labelKey = label != null
                ? (!string.IsNullOrEmpty(label.tooltip) ? label.tooltip : label.text)
                : null;
            var fieldKey = !string.IsNullOrEmpty(labelKey)
                ? "color-field:" + labelKey
                : "color-field:" + GUIUtility.GetControlID(kColorFieldHint, FocusType.Passive);
            var isOpen = SC2RuntimeColorPalette.IsOpen(fieldKey);
            var clicked = GUILayout.Button(GUIContent.none, options);
            if (Event.current.type == EventType.Repaint)
            {
                var swatchRect = GUILayoutUtility.GetLastRect();
                var previousColor = GUI.color;
                GUI.color = value;
                GUI.DrawTexture(new Rect(swatchRect.x + 2f, swatchRect.y + 2f, Mathf.Max(1f, swatchRect.width - 4f), Mathf.Max(1f, swatchRect.height - 4f)), Texture2D.whiteTexture);
                GUI.color = previousColor;
                if (isOpen)
                    SC2RuntimeColorPalette.UpdateAnchor(fieldKey, GUIToScreenRect(swatchRect));
            }
            if (clicked)
                SC2RuntimeColorPalette.Toggle(fieldKey, GUIToScreenRect(GUILayoutUtility.GetLastRect()));

            return SC2RuntimeColorPalette.ConsumePendingResult(fieldKey, value);
        }
        public static UnityEngine.Object ObjectField(string label, UnityEngine.Object obj, Type objType, bool allowSceneObjects, params GUILayoutOption[] options)
        {
            // Was a plain Label — permanently read-only, since there's no
            // Project window/AssetDatabase to pick a new object from at
            // runtime. That's why fields like "Terrain Data" could never be
            // switched to another instance in a build. Now a button that
            // opens a picker overlay listing whatever matching-type objects
            // are currently loaded in memory (see SC2RuntimeObjectPicker).
            //
            // `options` is the width for the WHOLE field (label + control),
            // matching real EditorGUILayout.ObjectField's contract — it must
            // go on the horizontal group, not also on the inner button. The
            // previous version applied it to the button alone, on top of an
            // already-92px-wide label with no shared width cap, so the total
            // rendered width was 92px + the caller's full requested width —
            // overflowing past the panel edge (e.g. the "Terrain Data" field
            // in the Geometry tab).
            BeginHorizontal(options);
            if (!string.IsNullOrEmpty(label))
                GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(92f)));
            // See the comment on kPopupHint in Popup() above for why this needs
            // its own hint distinct from every other hint-less GetControlID call.
            var controlId = GUIUtility.GetControlID(kObjectFieldHint, FocusType.Passive);
            var isOpen = SC2RuntimeObjectPicker.IsOpen(controlId);
            var clicked = GUILayout.Button(obj != null ? obj.name : "None", GUI.skin.box, GUILayout.ExpandWidth(true));
            if (isOpen && Event.current.type == EventType.Repaint)
                SC2RuntimeObjectPicker.UpdateAnchor(controlId, GUIToScreenRect(GUILayoutUtility.GetLastRect()));
            if (clicked)
                SC2RuntimeObjectPicker.Toggle(controlId, objType, GUIToScreenRect(GUILayoutUtility.GetLastRect()));
            EndHorizontal();
            var result = SC2RuntimeObjectPicker.ConsumePendingResult(controlId, obj);
            // Real EditorGUILayout.ObjectField sets GUI.changed whenever the
            // value actually changes — including picking "None" (null).
            // Without this, EditorGUI.BeginChangeCheck()/EndChangeCheck()
            // (which this shim implements purely in terms of GUI.changed,
            // see below) never reported a change for a picker-driven
            // ObjectField, so call sites gated on EndChangeCheck() — e.g.
            // "Terrain Data"'s SetData(nextData) — silently never ran, and
            // picking "None" (or any other asset) appeared to do nothing.
            if (!ReferenceEquals(result, obj))
                GUI.changed = true;
            return result;
        }
        // Real EditorGUILayout.ObjectField also has a label-less overload
        // (used by call sites that draw their own label separately, e.g.
        // SC2ExportPanel's "RawData" row) — without this, those call sites
        // had no shim overload to bind to and fell back to a hand-written
        // read-only label in their own #else branch, permanently unable to
        // pick a different asset in a build.
        public static UnityEngine.Object ObjectField(UnityEngine.Object obj, Type objType, bool allowSceneObjects, params GUILayoutOption[] options) =>
            ObjectField(null, obj, objType, allowSceneObjects, options);
        public static Rect GetControlRect(params GUILayoutOption[] options) => GUILayoutUtility.GetRect(1f, EditorGUIUtility.singleLineHeight, options);
        public static Rect GetControlRect(bool hasLabel, float height, params GUILayoutOption[] options) => GUILayoutUtility.GetRect(1f, height, options);
        public static void HelpBox(string message, MessageType type) => GUILayout.Label(message, GUI.skin.box);

        // Simple click-to-toggle collapsible header — real EditorGUILayout.Foldout
        // draws a triangle glyph + expand-state click handling that has no
        // simple GUILayout equivalent, so this approximates it with a
        // prefixed arrow glyph on an ordinary button-like label.
        public static bool Foldout(bool foldout, string content, bool toggleOnLabelClick, GUIStyle style)
        {
            var label = (foldout ? "▾ " : "▸ ") + content;
            if (GUILayout.Button(label, style ?? GUI.skin.label))
                foldout = !foldout;
            return foldout;
        }
    }

    public static class EditorGUI
    {
        public static int indentLevel;
        private static bool _change;
        public static void BeginChangeCheck() { _change = GUI.changed; GUI.changed = false; }
        public static bool EndChangeCheck() { var changed = GUI.changed; GUI.changed |= _change; return changed; }
        // Matches real UnityEditor.EditorGUI semantics: nested groups compose and
        // End restores the *previous* enabled state instead of force-enabling.
        // The old "GUI.enabled = true" version re-enabled panels the host had
        // deliberately disabled (e.g. while a dropdown menu is open).
        private static readonly System.Collections.Generic.Stack<bool> _enabledStack = new System.Collections.Generic.Stack<bool>();
        public static void BeginDisabledGroup(bool disabled)
        {
            _enabledStack.Push(GUI.enabled);
            GUI.enabled = !disabled && GUI.enabled;
        }
        public static void EndDisabledGroup()
        {
            GUI.enabled = _enabledStack.Count > 0 ? _enabledStack.Pop() : true;
        }
        public static void DrawRect(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }
        public static void DrawPreviewTexture(Rect rect, Texture texture) => GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
        public static void DrawPreviewTexture(Rect rect, Texture texture, Material mat, ScaleMode scaleMode) => GUI.DrawTexture(rect, texture, scaleMode, false);
        public static void LabelField(Rect rect, string label, GUIStyle style) => GUI.Label(rect, label, style);
        public static float Slider(float value, float leftValue, float rightValue, params GUILayoutOption[] options) => GUILayout.HorizontalSlider(value, leftValue, rightValue, options);
    }

    public static class EditorGUIUtility
    {
        public static float currentViewWidth => Screen.width;
        public static float singleLineHeight => 18f;
        public static float labelWidth;
        // Was hardcoded false — meaning in a standalone Build & Run player,
        // every hotkey handler gated on "editingTextField ? skip : run" always
        // saw "not editing", so typing e.g. "s" into any text/numeric field
        // also fired the Scale hotkey. UnityEditor.EditorGUIUtility.editingTextField
        // itself is Editor-only, but GUIUtility.keyboardControl (plain
        // UnityEngine, works in a build) is nonzero exactly when some IMGUI
        // control currently owns keyboard focus — the closest runtime-safe
        // proxy without a bespoke per-field focus tracker.
        public static bool editingTextField => GUIUtility.keyboardControl != 0;
        public static void ShowObjectPicker<T>(UnityEngine.Object obj, bool allowSceneObjects, string searchFilter, int controlID) where T : UnityEngine.Object {}
        public static int GetObjectPickerControlID() => -1;
        public static UnityEngine.Object GetObjectPickerObject() => null;
    }

    public static class EditorUtility
    {
        public static void SetDirty(UnityEngine.Object target) {}
        public static bool DisplayDialog(string title, string message, string ok) => true;
        public static bool DisplayDialog(string title, string message, string ok, string cancel) => true;
        public static string SaveFilePanelInProject(string title, string defaultName, string extension, string message) => "";
        public static string OpenFolderPanel(string title, string folder, string defaultName) => "";
        public static void DisplayProgressBar(string title, string info, float progress) {}
        public static void ClearProgressBar() {}
        public static void OpenWithDefaultApp(string path) {}
        public static void RevealInFinder(string path) {}
    }

    public static class AssetDatabase
    {
        public static void CreateAsset(UnityEngine.Object asset, string path) {}
        public static void SaveAssets() {}
        public static void Refresh() {}
        public static string GetAssetPath(UnityEngine.Object asset) => "";
        public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => null;
    }

    public static class Undo
    {
        public static void RecordObject(UnityEngine.Object obj, string name) {}
        public static void RegisterCreatedObjectUndo(UnityEngine.Object obj, string name) {}
    }

    public static class Selection
    {
        private static GameObject _activeGameObject;

        public static GameObject activeGameObject
        {
            get => _activeGameObject;
            set
            {
                _activeGameObject = value;
                activeObject = value;
            }
        }

        public static UnityEngine.Object activeObject { get; set; }
        public static Transform activeTransform => _activeGameObject != null ? _activeGameObject.transform : null;
    }

    public static class EditorApplication
    {
        public static event Action update { add {} remove {} }
        public static event Action quitting { add {} remove {} }
        public static double timeSinceStartup => Time.realtimeSinceStartup;
        public static void QueuePlayerLoopUpdate() {}
        public static bool ExecuteMenuItem(string menuItemPath) => false;
    }

    public sealed class SceneView
    {
        public Camera camera;
        public static void RepaintAll() {}
    }

    public static class Handles
    {
        public static Color color;
        public static void DrawWireDisc(Vector3 center, Vector3 normal, float radius) {}
        public static void DrawSolidDisc(Vector3 center, Vector3 normal, float radius) {}
        public static void Label(Vector3 position, string text) {}
    }

    public static class HandleUtility
    {
        public static Ray GUIPointToWorldRay(Vector2 position)
        {
            var camera = Camera.main;
            if (camera == null)
                return new Ray(Vector3.zero, Vector3.down);

            return SC2TopViewMirror.ScreenPointToRay(camera, position);
        }
    }
}

public static class EditorStyles
{
    public static GUIStyle boldLabel => UnityEditor.EditorStyles.boldLabel;
    public static GUIStyle miniBoldLabel => UnityEditor.EditorStyles.miniBoldLabel;
    public static GUIStyle miniLabel => UnityEditor.EditorStyles.miniLabel;
    public static GUIStyle centeredGreyMiniLabel => UnityEditor.EditorStyles.centeredGreyMiniLabel;
    public static GUIStyle largeLabel => UnityEditor.EditorStyles.largeLabel;
    public static GUIStyle textField => UnityEditor.EditorStyles.textField;
    public static GUIStyle textArea => UnityEditor.EditorStyles.textArea;
    public static GUIStyle toolbar => UnityEditor.EditorStyles.toolbar;
    public static GUIStyle toolbarButton => UnityEditor.EditorStyles.toolbarButton;
    public static GUIStyle foldout => UnityEditor.EditorStyles.foldout;
    public static GUIStyle foldoutHeader => UnityEditor.EditorStyles.foldoutHeader;
}

public static class EditorGUILayout
{
    public sealed class VerticalScope : IDisposable
    {
        private readonly UnityEditor.EditorGUILayout.VerticalScope _scope;
        public VerticalScope(params GUILayoutOption[] options) { _scope = new UnityEditor.EditorGUILayout.VerticalScope(options); }
        public VerticalScope(GUIStyle style, params GUILayoutOption[] options) { _scope = new UnityEditor.EditorGUILayout.VerticalScope(style, options); }
        public void Dispose() { _scope.Dispose(); }
    }

    public sealed class HorizontalScope : IDisposable
    {
        private readonly UnityEditor.EditorGUILayout.HorizontalScope _scope;
        public HorizontalScope(params GUILayoutOption[] options) { _scope = new UnityEditor.EditorGUILayout.HorizontalScope(options); }
        public HorizontalScope(GUIStyle style, params GUILayoutOption[] options) { _scope = new UnityEditor.EditorGUILayout.HorizontalScope(style, options); }
        public void Dispose() { _scope.Dispose(); }
    }

    public static void Space() => UnityEditor.EditorGUILayout.Space();
    public static void Space(float pixels) => UnityEditor.EditorGUILayout.Space(pixels);
    public static Rect BeginHorizontal(params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.BeginHorizontal(options);
    public static Rect BeginHorizontal(GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.BeginHorizontal(style, options);
    public static void EndHorizontal() => UnityEditor.EditorGUILayout.EndHorizontal();
    public static Rect BeginVertical(params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.BeginVertical(options);
    public static Rect BeginVertical(GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.BeginVertical(style, options);
    public static void EndVertical() => UnityEditor.EditorGUILayout.EndVertical();
    public static void LabelField(string label, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.LabelField(label, options);
    public static void LabelField(string label, GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.LabelField(label, style, options);
    public static void LabelField(string label, string label2, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.LabelField(label, label2, options);
    public static string TextField(string label, string text, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.TextField(label, text, options);
    public static string TextField(string text, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.TextField(text, options);
    public static string TextArea(string text, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.TextArea(text, options);
    public static string TextArea(string text, GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.TextArea(text, style, options);
    public static float FloatField(string label, float value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.FloatField(label, value, options);
    public static bool Toggle(string label, bool value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Toggle(label, value, options);
    public static bool Toggle(bool value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Toggle(value, options);
    public static bool ToggleLeft(string label, bool value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.ToggleLeft(label, value, options);
    public static int Popup(int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Popup(selectedIndex, displayedOptions, options);
    public static int Popup(int selectedIndex, string[] displayedOptions, GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Popup(selectedIndex, displayedOptions, style, options);
    public static int Popup(string stableKey, int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Popup(selectedIndex, displayedOptions, options);
    public static int Popup(string stableKey, int selectedIndex, string[] displayedOptions, GUIStyle style, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Popup(selectedIndex, displayedOptions, style, options);
    public static Vector3 Vector3Field(string label, Vector3 value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Vector3Field(label, value, options);
    public static Vector2 Vector2Field(string label, Vector2 value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Vector2Field(label, value, options);
    public static Vector4 Vector4Field(string label, Vector4 value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Vector4Field(label, value, options);
    public static float Slider(string label, float value, float leftValue, float rightValue, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.Slider(label, value, leftValue, rightValue, options);
    public static int IntField(string label, int value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.IntField(label, value, options);
    public static Color ColorField(string label, Color value, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.ColorField(label, value, options);
    public static Color ColorField(GUIContent label, Color value, bool showEyedropper, bool showAlpha, bool hdr, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.ColorField(label, value, showEyedropper, showAlpha, hdr, options);
    public static UnityEngine.Object ObjectField(string label, UnityEngine.Object obj, Type objType, bool allowSceneObjects, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.ObjectField(label, obj, objType, allowSceneObjects, options);
    // Call sites in the global namespace (no `using UnityEditor;` needed to
    // reach this class — see its own comment above) resolve a bare
    // "EditorGUILayout" to THIS class first, before even considering a
    // `using UnityEditor;` import — C# always prefers a type already in the
    // caller's own (here: global) namespace over one pulled in via `using`.
    // SC2ExportPanel.cs DOES have `using UnityEditor;`, but is itself a
    // global-namespace class, so it still resolved here — meaning the
    // label-less overload added to the namespaced UnityEditor.EditorGUILayout
    // above was invisible to it, and its call to the 3-arg ObjectField
    // (no label) matched this class's 4-arg labeled overload instead,
    // shifting every argument by one (CS1503 on all five).
    public static UnityEngine.Object ObjectField(UnityEngine.Object obj, Type objType, bool allowSceneObjects, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.ObjectField(obj, objType, allowSceneObjects, options);
    public static Rect GetControlRect(params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.GetControlRect(options);
    public static Rect GetControlRect(bool hasLabel, float height, params GUILayoutOption[] options) => UnityEditor.EditorGUILayout.GetControlRect(hasLabel, height, options);
    public static void HelpBox(string message, UnityEditor.MessageType type) => UnityEditor.EditorGUILayout.HelpBox(message, type);
    public static void HelpBox(string message, MessageType type) => UnityEditor.EditorGUILayout.HelpBox(message, (UnityEditor.MessageType)(int)type);
    public static bool Foldout(bool foldout, string content, bool toggleOnLabelClick, GUIStyle style) => UnityEditor.EditorGUILayout.Foldout(foldout, content, toggleOnLabelClick, style);
}

public static class EditorGUI
{
    public static int indentLevel { get => UnityEditor.EditorGUI.indentLevel; set => UnityEditor.EditorGUI.indentLevel = value; }
    public static void BeginChangeCheck() => UnityEditor.EditorGUI.BeginChangeCheck();
    public static bool EndChangeCheck() => UnityEditor.EditorGUI.EndChangeCheck();
    public static void BeginDisabledGroup(bool disabled) => UnityEditor.EditorGUI.BeginDisabledGroup(disabled);
    public static void EndDisabledGroup() => UnityEditor.EditorGUI.EndDisabledGroup();
    public static void DrawRect(Rect rect, Color color) => UnityEditor.EditorGUI.DrawRect(rect, color);
    public static void DrawPreviewTexture(Rect rect, Texture texture) => UnityEditor.EditorGUI.DrawPreviewTexture(rect, texture);
    public static void DrawPreviewTexture(Rect rect, Texture texture, Material mat, ScaleMode scaleMode) => UnityEditor.EditorGUI.DrawPreviewTexture(rect, texture, mat, scaleMode);
    public static void LabelField(Rect rect, string label, GUIStyle style) => UnityEditor.EditorGUI.LabelField(rect, label, style);
    public static float Slider(float value, float leftValue, float rightValue, params GUILayoutOption[] options) => UnityEditor.EditorGUI.Slider(value, leftValue, rightValue, options);
}

public static class EditorGUIUtility
{
    public static float currentViewWidth => UnityEditor.EditorGUIUtility.currentViewWidth;
    public static float singleLineHeight => UnityEditor.EditorGUIUtility.singleLineHeight;
    public static float labelWidth { get => UnityEditor.EditorGUIUtility.labelWidth; set => UnityEditor.EditorGUIUtility.labelWidth = value; }
    public static bool editingTextField => UnityEditor.EditorGUIUtility.editingTextField;
    public static void ShowObjectPicker<T>(UnityEngine.Object obj, bool allowSceneObjects, string searchFilter, int controlID) where T : UnityEngine.Object => UnityEditor.EditorGUIUtility.ShowObjectPicker<T>(obj, allowSceneObjects, searchFilter, controlID);
    public static int GetObjectPickerControlID() => UnityEditor.EditorGUIUtility.GetObjectPickerControlID();
    public static UnityEngine.Object GetObjectPickerObject() => UnityEditor.EditorGUIUtility.GetObjectPickerObject();
}

public static class EditorUtility
{
    public static void SetDirty(UnityEngine.Object target) => UnityEditor.EditorUtility.SetDirty(target);
    public static bool DisplayDialog(string title, string message, string ok) => UnityEditor.EditorUtility.DisplayDialog(title, message, ok);
    public static bool DisplayDialog(string title, string message, string ok, string cancel) => UnityEditor.EditorUtility.DisplayDialog(title, message, ok, cancel);
    public static string SaveFilePanelInProject(string title, string defaultName, string extension, string message) => UnityEditor.EditorUtility.SaveFilePanelInProject(title, defaultName, extension, message);
    public static string OpenFolderPanel(string title, string folder, string defaultName) => UnityEditor.EditorUtility.OpenFolderPanel(title, folder, defaultName);
    public static void DisplayProgressBar(string title, string info, float progress) => UnityEditor.EditorUtility.DisplayProgressBar(title, info, progress);
    public static void ClearProgressBar() => UnityEditor.EditorUtility.ClearProgressBar();
    public static void OpenWithDefaultApp(string path) => UnityEditor.EditorUtility.OpenWithDefaultApp(path);
    public static void RevealInFinder(string path) => UnityEditor.EditorUtility.RevealInFinder(path);
}

public static class AssetDatabase
{
    public static void CreateAsset(UnityEngine.Object asset, string path) => UnityEditor.AssetDatabase.CreateAsset(asset, path);
    public static void SaveAssets() => UnityEditor.AssetDatabase.SaveAssets();
    public static void Refresh() => UnityEditor.AssetDatabase.Refresh();
    public static string GetAssetPath(UnityEngine.Object asset) => UnityEditor.AssetDatabase.GetAssetPath(asset);
    public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => UnityEditor.AssetDatabase.LoadAssetAtPath<T>(assetPath);
}

public static class Undo
{
    public static void RecordObject(UnityEngine.Object obj, string name) => UnityEditor.Undo.RecordObject(obj, name);
    public static void RegisterCreatedObjectUndo(UnityEngine.Object obj, string name) => UnityEditor.Undo.RegisterCreatedObjectUndo(obj, name);
}

public static class EditorApplication
{
    public static event Action update { add { UnityEditor.EditorApplication.update += value; } remove { UnityEditor.EditorApplication.update -= value; } }
    public static event Action quitting { add { UnityEditor.EditorApplication.quitting += value; } remove { UnityEditor.EditorApplication.quitting -= value; } }
    public static double timeSinceStartup => UnityEditor.EditorApplication.timeSinceStartup;
    public static void QueuePlayerLoopUpdate() => UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
    public static bool ExecuteMenuItem(string menuItemPath) => UnityEditor.EditorApplication.ExecuteMenuItem(menuItemPath);
}

public static class Selection
{
    public static GameObject activeGameObject { get => UnityEditor.Selection.activeGameObject; set => UnityEditor.Selection.activeGameObject = value; }
    public static UnityEngine.Object activeObject { get => UnityEditor.Selection.activeObject; set => UnityEditor.Selection.activeObject = value; }
    public static Transform activeTransform => UnityEditor.Selection.activeTransform;
}

public enum MessageType
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MenuItem : Attribute
{
    public MenuItem(string itemName) {}
    public MenuItem(string itemName, bool isValidateFunction) {}
    public MenuItem(string itemName, bool isValidateFunction, int priority) {}
}

public static class Handles
{
    public static Color color { get => UnityEditor.Handles.color; set => UnityEditor.Handles.color = value; }
    public static void DrawWireDisc(Vector3 center, Vector3 normal, float radius) => UnityEditor.Handles.DrawWireDisc(center, normal, radius);
    public static void DrawSolidDisc(Vector3 center, Vector3 normal, float radius) => UnityEditor.Handles.DrawSolidDisc(center, normal, radius);
    public static void Label(Vector3 position, string text) => UnityEditor.Handles.Label(position, text);
}

public static class HandleUtility
{
    public static Ray GUIPointToWorldRay(Vector2 position) => UnityEditor.HandleUtility.GUIPointToWorldRay(position);
}
#endif
