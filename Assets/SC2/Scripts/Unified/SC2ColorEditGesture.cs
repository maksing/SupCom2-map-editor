using UnityEngine;

/// <summary>Tells the first change of a colour edit from the ones that follow it. A colour picker reports a new value on every frame
/// of a drag, and the undo checkpoint of the edit must be taken once, before the first of them, not on every frame (a checkpoint
/// copies the terrain state). Changes of the same colour that come within a short time of each other belong to one edit.</summary>
public static class SC2ColorEditGesture
{
    private const float PauseSeconds = 0.6f;
    private static string _key;
    private static float _lastChange = float.NegativeInfinity;

    /// <summary>True for the first change of an edit of the colour <paramref name="key"/>; call it for every change.</summary>
    public static bool IsFirstChange(string key)
    {
        var now = Time.realtimeSinceStartup;
        var first = key != _key || now - _lastChange > PauseSeconds;
        _key = key;
        _lastChange = now;
        return first;
    }
}
