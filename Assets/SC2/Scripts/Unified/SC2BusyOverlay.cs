using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// A plate in the middle of the screen naming the long job that is running (autosave backup, map export, lighting bake), and the
/// machinery that keeps the editor alive while it runs.
///
/// A job is split in three, so the frame loop never waits for the heavy part:
///   1. <c>startOnMain</c> runs on the main thread: it reads what only the main thread may read (GPU data, textures, PlayerPrefs)
///      and returns the heavy function;
///   2. that function runs on a worker thread and returns the action that stores its result;
///   3. the result action runs back on the main thread.
/// While it runs the window keeps repainting (the plate animates), and input is swallowed so nothing can change the data the
/// worker is reading. A worker that needs the main thread for a moment (a texture, a dialog) calls <see cref="OnMain{T}"/>.
///
/// If a worker trips over an API that only the main thread may call, the heavy function is run again on the main thread: the job
/// takes as long as before and the editor stalls as before, but it still finishes.
///
/// The host calls <see cref="Tick"/> every frame from its update and <see cref="Draw"/> last in OnGUI. The plate is shown before
/// anything starts (see <see cref="SC2BusyGate"/>), so the main-thread part never runs before the plate is on screen.
/// </summary>
public static class SC2BusyOverlay
{
    /// <summary>How long a worker waits for the main thread to run a request before giving up on it.</summary>
    private const int MainThreadWaitMilliseconds = 300_000;

    private static readonly SC2BusyGate Gate = new SC2BusyGate();
    private static readonly ConcurrentQueue<Action> MainQueue = new ConcurrentQueue<Action>();
    private static readonly object StageLock = new object();

    // First touched on the main thread: a job can only be started from there.
    private static readonly int MainThreadId = Environment.CurrentManagedThreadId;

    private static Func<Func<Action>> _start;
    private static Action<Exception> _onError;
    private static Func<Action> _compute;
    private static Task<Action> _task;
    private static int _jobId;
    private static string _stage = "";
    private static float _progress = -1f;

    public static bool IsActive => Gate.IsActive;

    public static bool IsOnMainThread => Environment.CurrentManagedThreadId == MainThreadId;

    /// <summary>Shows the plate, then runs the job (see the class comment for its three parts). False when another job already owns
    /// the plate (nothing is queued). <paramref name="startOnMain"/> may return null when there turns out to be nothing to do.
    /// <paramref name="onError"/> runs on the main thread if the job throws.</summary>
    public static bool RunInBackground(string title, string detail, Func<Func<Action>> startOnMain, Action<Exception> onError = null)
    {
        if (startOnMain == null || !IsOnMainThread || !Gate.Begin(title, detail, Now())) return false;
        _start = startOnMain;
        _onError = onError;
        _jobId++;
        SetStage("", -1f);
        return true;
    }

    /// <summary>Names the step the job is on, with its progress 0..1 (or negative when unknown). Safe from any thread.</summary>
    public static void Report(string stage, float progress = -1f) => SetStage(stage, progress);

    /// <summary>Runs <paramref name="work"/> on the main thread and waits for it. Called on the main thread it simply runs. For the
    /// few steps of a worker that only the main thread may do.</summary>
    public static T OnMain<T>(Func<T> work, int timeoutMilliseconds = MainThreadWaitMilliseconds)
    {
        if (IsOnMainThread) return work();
        var result = default(T);
        Exception error = null;
        var done = new ManualResetEventSlim(false);
        MainQueue.Enqueue(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMilliseconds))
            throw new TimeoutException("The main thread did not run a request from the background job in time.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public static void OnMain(Action work, int timeoutMilliseconds = MainThreadWaitMilliseconds) =>
        OnMain<object>(() => { work(); return null; }, timeoutMilliseconds);

    /// <summary>Runs <paramref name="start"/> on the main thread and waits until it calls the <c>done</c> callback it is given, which
    /// may happen on a later frame (a minimap capture, for instance). Called on the main thread it only starts.</summary>
    public static void OnMainAsync(Action<Action> start, int timeoutMilliseconds = MainThreadWaitMilliseconds)
    {
        if (IsOnMainThread) { start(() => { }); return; }
        Exception error = null;
        var done = new ManualResetEventSlim(false);
        MainQueue.Enqueue(() =>
        {
            try { start(() => done.Set()); }
            catch (Exception ex) { error = ex; done.Set(); }
        });
        if (!done.Wait(timeoutMilliseconds))
            throw new TimeoutException("The main thread did not finish a request from the background job in time.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Called every frame, from both the player loop and the editor update: serves the worker's requests, starts a queued
    /// job once its plate has been presented, and finishes it when the worker is done.</summary>
    public static void Tick(Action repaint)
    {
        while (MainQueue.TryDequeue(out var request))
        {
            try { request(); }
            catch (Exception ex) { SC2Log.LogException(ex); }
        }
        if (!Gate.IsActive) return;

        var now = Now();
        if (Gate.State == SC2BusyGate.Phase.Showing)
        {
            if (Gate.ReadyToStart(now)) Begin(now);
            else repaint?.Invoke(); // the plate waits for frames to be drawn in
            return;
        }

        if (_task == null) return;
        if (_task.IsCompleted) Finish();
        else repaint?.Invoke(); // keeps the plate animating
    }

    private static void Begin(double now)
    {
        Gate.MarkRunning(now);
        var start = _start;
        _start = null;
        var id = _jobId;
        Func<Action> compute;
        try { compute = start(); }
        catch (Exception ex)
        {
            Fail(ex);
            End(id);
            return;
        }
        if (compute == null)
        {
            End(id);
            return;
        }
        _compute = compute;
        _task = Task.Run(compute);
    }

    private static void Finish()
    {
        var task = _task;
        var compute = _compute;
        var id = _jobId;
        _task = null;
        _compute = null;

        Action apply = null;
        Exception error = null;
        if (task.IsFaulted) error = task.Exception.GetBaseException();
        else if (task.IsCanceled) error = new OperationCanceledException();
        else apply = task.Result;

        if (error != null && IsMainThreadOnlyError(error))
        {
            // Something the worker touched only works on the main thread: do the job there, as it always was.
            SC2Log.LogWarning($"[SC2BusyOverlay] '{Gate.Title}' needs the main thread ({error.Message}); running it there instead.");
            try { apply = compute(); error = null; }
            catch (Exception ex) { error = ex; }
        }

        if (error != null) Fail(error);
        else
        {
            try { apply?.Invoke(); }
            catch (Exception ex) { Fail(ex); }
        }
        End(id);
    }

    private static void Fail(Exception error)
    {
        SC2Log.LogException(error);
        var onError = _onError;
        try { onError?.Invoke(error); }
        catch (Exception ex) { SC2Log.LogException(ex); }
    }

    private static bool IsMainThreadOnlyError(Exception ex) =>
        ex is UnityException && ex.Message.IndexOf("main thread", StringComparison.OrdinalIgnoreCase) >= 0;

    // A stale call of an earlier job must not end the plate of a later one.
    private static void End(int id)
    {
        if (id != _jobId) return;
        _start = null;
        _onError = null;
        _compute = null;
        _task = null;
        Gate.End();
        SetStage("", -1f);
    }

    private static void SetStage(string stage, float progress)
    {
        lock (StageLock)
        {
            _stage = stage ?? "";
            _progress = progress;
        }
    }

    private static double Now() => Time.realtimeSinceStartupAsDouble;

    private static GUIStyle _titleStyle;
    private static GUIStyle _detailStyle;
    private static GUIStyle _stageStyle;
    private static float _stylesAtScale = -1f;

    private static void EnsureStyles()
    {
        if (_titleStyle != null && Mathf.Approximately(_stylesAtScale, SC2UiScale.Scale)) return;
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Title,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };
        _titleStyle.normal.textColor = SC2UnifiedSkin.TextPrimary;
        _detailStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Body,
            alignment = TextAnchor.UpperCenter,
            wordWrap = true,
        };
        _detailStyle.normal.textColor = SC2UnifiedSkin.TextSecondary;
        _stageStyle = new GUIStyle(_detailStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
        _stageStyle.normal.textColor = SC2UnifiedSkin.TextPrimary;
        _stylesAtScale = SC2UiScale.Scale;
    }

    /// <summary>Draws the plate; call last in OnGUI, with the unified skin set. Swallows input while it is up.</summary>
    public static void Draw()
    {
        if (!Gate.IsActive) return;
        var e = Event.current;
        if (e == null) return;

        if (e.type == EventType.Repaint)
        {
            EnsureStyles();
            string stage;
            float progress;
            lock (StageLock) { stage = _stage; progress = _progress; }
            var now = Now();

            var previousDepth = GUI.depth;
            var previousColor = GUI.color;
            GUI.depth = -4000;

            GUI.color = new Color(0f, 0f, 0f, 0.4f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = previousColor;

            var width = Mathf.Min(SC2UiScale.Pxf(440f), Screen.width - 24f);
            var pad = SC2UiScale.Pxf(18f);
            var inner = width - 2f * pad;
            var gap = SC2UiScale.Pxf(8f);
            var titleHeight = SC2UiScale.Pxf(26f);
            var detailHeight = _detailStyle.CalcHeight(new GUIContent(Gate.Detail), inner);
            var stageHeight = SC2UiScale.Pxf(18f);
            var barHeight = SC2UiScale.Pxf(6f);
            var footerHeight = _detailStyle.CalcHeight(new GUIContent(FooterText(0.0)), inner);
            var height = pad + titleHeight + gap + detailHeight + gap + stageHeight + gap + barHeight + gap + footerHeight + pad;
            var card = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

            GUI.Box(card, GUIContent.none, GUI.skin.box);
            var x = card.x + pad;
            var y = card.y + pad;
            GUI.Label(new Rect(x, y, inner, titleHeight), Gate.Title + "...", _titleStyle);
            y += titleHeight + gap;
            GUI.Label(new Rect(x, y, inner, detailHeight), Gate.Detail, _detailStyle);
            y += detailHeight + gap;
            GUI.Label(new Rect(x, y, inner, stageHeight), stage, _stageStyle);
            y += stageHeight + gap;
            DrawBar(new Rect(x, y, inner, barHeight), progress, (float)now);
            y += barHeight + gap;
            GUI.Label(new Rect(x, y, inner, footerHeight), FooterText(Gate.RunningSeconds(now)), _detailStyle);

            GUI.depth = previousDepth;
            Gate.Presented();
        }
        else if (e.type != EventType.Layout)
        {
            e.Use();
        }
    }

    private static string FooterText(double seconds) =>
        $"Working in the background: {seconds:0} s. Editing is paused until it finishes.";

    // A filled bar for a known progress, a sliding segment otherwise.
    private static void DrawBar(Rect track, float progress, float time)
    {
        var previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);
        GUI.color = SC2UnifiedSkin.Accent;
        if (progress >= 0f)
        {
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp01(progress), track.height), Texture2D.whiteTexture);
        }
        else
        {
            const float segment = 0.3f;
            var start = Mathf.PingPong(time * 0.7f, 1f - segment);
            GUI.DrawTexture(new Rect(track.x + track.width * start, track.y, track.width * segment, track.height), Texture2D.whiteTexture);
        }
        GUI.color = previous;
    }
}
