using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Tells the user when a newer version of the editor has been published. Once when the editor starts (unless switched off in Customize) it
/// asks the GitHub API for the repository's releases, falling back to its tags, and compares the newest with SC2EditorVersion.Current. A
/// newer one opens a window with the release notes: Download (opens the release page), Later, or Skip this version, which is not offered
/// again. The request runs in the background; a failure (no network, rate limit) is only logged. Polled and drawn from the editor's
/// OnGUI, like the other overlays (SC2CostsGuide).
/// </summary>
public static class SC2UpdateNotice
{
    private enum Phase { Releases, Tags }

    private static UnityWebRequest _request;
    private static Phase _phase;
    private static bool _started;
    private static bool _manual;

    private static bool _open;
    private static SC2ReleaseInfo _offer;
    private static string _infoMessage;
    private static Vector2 _scroll;

    public static bool IsOpen => _open;

    /// <summary>Where the answers come from; fields so a test can point them at a local server.</summary>
    public static string ReleasesUrl = SC2EditorVersion.ReleasesApiUrl;
    public static string TagsUrl = SC2EditorVersion.TagsApiUrl;

    /// <summary>Lets the automatic check run inside the Unity Editor too (it does not by default); for tests.</summary>
    public static bool CheckInEditor;

    public static string OfferedVersion => _offer?.Version;
    public static string InfoMessage => _infoMessage;

    /// <summary>Starts the check. Without <paramref name="manual"/> it runs once per launch, only when the user has not switched it off,
    /// and never inside the Unity Editor (a development build is rarely the newest tag); with it (Customize > Check now) it always runs and
    /// also answers when there is nothing new.</summary>
    public static void BeginCheck(bool manual = false)
    {
        if (_request != null) return;
        if (!manual)
        {
            if (_started) return;
            _started = true;
            if (!SC2CustomizationSettings.CheckForUpdates) return;
#if UNITY_EDITOR
            if (!CheckInEditor) return;
#endif
        }
        _manual = manual;
        Send(ReleasesUrl, Phase.Releases);
    }

    private static void Send(string url, Phase phase)
    {
        _phase = phase;
        _request = UnityWebRequest.Get(url);
        _request.timeout = 10;
        _request.SetRequestHeader("Accept", "application/vnd.github+json");
        _request.SetRequestHeader("User-Agent", "SupCom2-map-editor/" + SC2EditorVersion.Current);
        _request.SendWebRequest();
    }

    /// <summary>Takes the answer once it has arrived. Cheap while nothing is pending.</summary>
    public static void Tick()
    {
        if (_request == null || !_request.isDone) return;
        var request = _request;
        _request = null;
        string text = null;
        try
        {
            if (request.result == UnityWebRequest.Result.Success) text = request.downloadHandler.text;
            else Debug.Log($"[SC2Update] The check for a new version failed ({request.result}: {request.error}).");
        }
        finally { request.Dispose(); }

        if (text == null) { Finish(null, failed: true); return; }
        if (_phase == Phase.Releases)
        {
            var release = SC2UpdateFeed.PickLatestRelease(text);
            if (release == null) { Send(TagsUrl, Phase.Tags); return; }
            Finish(release, failed: false);
            return;
        }
        Finish(SC2UpdateFeed.PickLatestTag(text), failed: false);
    }

    private static void Finish(SC2ReleaseInfo latest, bool failed)
    {
        if (latest != null && SC2EditorVersion.IsNewer(latest.Version, SC2EditorVersion.Current))
        {
            if (_manual || latest.Version != SC2CustomizationSettings.SkippedUpdateVersion)
                Open(latest, null);
            return;
        }
        if (!_manual) return;
        Open(null, failed
            ? "The check for a new version failed. Check the internet connection and try again."
            : $"You have the latest version ({SC2EditorVersion.Current}).");
    }

    private static void Open(SC2ReleaseInfo offer, string info)
    {
        _offer = offer;
        _infoMessage = info;
        _scroll = Vector2.zero;
        _open = true;
    }

    public static void Close() => _open = false;

    private static string TrimNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var text = notes.Replace("\r\n", "\n").Trim();
        const int limit = 1500;
        return text.Length <= limit ? text : text.Substring(0, limit).TrimEnd() + "\n…";
    }

    public static void DrawOverlay()
    {
        if (!_open) return;

        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }

        var offer = _offer;
        var title = offer != null ? "A new version is available" : "Updates";
        var message = offer != null
            ? $"Version {offer.Version} has been released. You have {SC2EditorVersion.Current}."
            : _infoMessage;
        var notes = offer != null ? TrimNotes(offer.Notes) : "";

        var width = Mathf.Min(SC2UiScale.Pxf(440f), Screen.width - 24f);
        var pad = SC2UiScale.Pxf(14f);
        var gap = SC2UiScale.Pxf(8f);
        var inner = width - 2f * pad;
        var buttonHeight = SC2UiScale.Pxf(28f);
        var titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true };
        var textStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        var titleHeight = titleStyle.CalcHeight(new GUIContent(title), inner);
        var messageHeight = textStyle.CalcHeight(new GUIContent(message), inner);
        var notesContentHeight = notes.Length > 0 ? textStyle.CalcHeight(new GUIContent(notes), inner - SC2UiScale.Pxf(16f)) : 0f;
        var notesViewHeight = notes.Length > 0 ? Mathf.Min(notesContentHeight + gap, SC2UiScale.Pxf(190f)) : 0f;
        var notesLabelHeight = notes.Length > 0 ? GUI.skin.label.CalcHeight(new GUIContent("What is new"), inner) : 0f;
        var height = pad + titleHeight + gap + messageHeight + gap
                     + (notes.Length > 0 ? notesLabelHeight + notesViewHeight + gap : 0f)
                     + buttonHeight + pad;
        height = Mathf.Min(height, Screen.height - 24f);
        var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        var previousDepth = GUI.depth;
        GUI.depth = -3000;

        var previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUI.Box(rect, GUIContent.none, GUI.skin.box);
        var y = rect.y + pad;
        GUI.Label(new Rect(rect.x + pad, y, inner, titleHeight), title, titleStyle);
        y += titleHeight + gap;
        GUI.Label(new Rect(rect.x + pad, y, inner, messageHeight), message, textStyle);
        y += messageHeight + gap;
        if (notes.Length > 0)
        {
            GUI.Label(new Rect(rect.x + pad, y, inner, notesLabelHeight), "What is new", GUI.skin.label);
            y += notesLabelHeight;
            var viewRect = new Rect(rect.x + pad, y, inner, notesViewHeight);
            var contentRect = new Rect(0f, 0f, inner - SC2UiScale.Pxf(16f), notesContentHeight);
            _scroll = GUI.BeginScrollView(viewRect, _scroll, contentRect);
            GUI.Label(contentRect, notes, textStyle);
            GUI.EndScrollView();
        }

        var buttonY = rect.yMax - pad - buttonHeight;
        if (offer != null)
        {
            var downloadWidth = SC2UiScale.Pxf(100f);
            var laterWidth = SC2UiScale.Pxf(70f);
            var skipWidth = inner - downloadWidth - laterWidth - 2f * gap;
            var skipRect = new Rect(rect.x + pad, buttonY, skipWidth, buttonHeight);
            var laterRect = new Rect(skipRect.xMax + gap, buttonY, laterWidth, buttonHeight);
            var downloadRect = new Rect(laterRect.xMax + gap, buttonY, downloadWidth, buttonHeight);
            if (GUI.Button(skipRect, "Skip this version"))
            {
                SC2CustomizationSettings.SkippedUpdateVersion = offer.Version;
                Close();
            }
            if (GUI.Button(laterRect, "Later")) Close();
            if (GUI.Button(downloadRect, "Download"))
            {
                Application.OpenURL(offer.Url);
                Close();
            }
        }
        else if (GUI.Button(new Rect(rect.xMax - pad - SC2UiScale.Pxf(90f), buttonY, SC2UiScale.Pxf(90f), buttonHeight), "OK"))
        {
            Close();
        }

        GUI.depth = previousDepth;

        // Modal: nothing under the window sees a click or a key while it is open.
        e = Event.current;
        if (e != null && e.type != EventType.Layout && e.type != EventType.Repaint) e.Use();
    }
}
