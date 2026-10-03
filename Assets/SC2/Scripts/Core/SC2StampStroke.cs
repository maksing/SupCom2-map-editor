using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Places the dabs of a stroke made with a custom stamp brush by DISTANCE, not by input event: one stamp where the stroke starts, then one
/// every <c>spacing</c> units along the path. Dabbing on every mouse event (about 60 a second whatever the mouse does) piles dozens of
/// stamps on top of each other, which smears an irregular stamp into a blob and builds up slowly dragged strokes far more than fast
/// ones. Pure, so the rules can be tested.
/// </summary>
public sealed class SC2StampStroke
{
    /// <summary>The most dabs one input event may place; a longer jump is cut short and the stroke resumes from where the mouse is.</summary>
    public const int MaxDabsPerEvent = 24;

    private Vector3 _last;
    private bool _started;

    /// <summary>Starts a stroke: the first dab is at <paramref name="position"/>.</summary>
    public List<Vector3> Begin(Vector3 position)
    {
        _last = position;
        _started = true;
        return new List<Vector3> { position };
    }

    /// <summary>The dabs for the mouse moving to <paramref name="position"/>: one every <paramref name="spacing"/> units from the last
    /// dab, none while the mouse has not moved that far.</summary>
    public List<Vector3> Move(Vector3 position, float spacing)
    {
        if (!_started) return Begin(position);
        var dabs = SC2FlattenMath.SpacedDabs(_last, position, spacing, MaxDabsPerEvent, out var jumped);
        if (dabs.Count > 0) _last = jumped ? position : dabs[dabs.Count - 1];
        return dabs;
    }

    public void End() => _started = false;

    /// <summary>Distance between two dabs for a brush of <paramref name="radius"/>: <paramref name="fraction"/> of the diameter, but never
    /// less than half a terrain cell (finer than that changes nothing).</summary>
    public static float Spacing(float radius, float fraction, float cellSize) => Mathf.Max(radius * 2f * fraction, cellSize * 0.5f);

    /// <summary>
    /// Within one stroke a stamp brush adds to a pixel only what its stamps reach there at most: the strongest amplitude any dab of
    /// the stroke has had at that pixel (the envelope of the stamps), however many dabs land on it. Dabbing on every input event
    /// piles the same stamp up dozens of times when the mouse moves slowly; adding each time saturates every pixel under the path
    /// to the same value and the stamp's pattern is lost in a flat blob. With the envelope, slow and fast strokes give the same
    /// result and the pattern stays.
    ///
    /// <paramref name="strokeMax"/> is the strongest amplitude so far at the pixel (updated); returns how much it grew, which is
    /// what this dab adds (to a height) -- or, as <see cref="RemainingShare"/>, the share of the remaining distance to the target
    /// to move (to a blend value or coverage, so that the steps together equal ONE lerp by the final envelope value).
    /// </summary>
    public static float EnvelopeGain(ref float strokeMax, float amplitude)
    {
        var grown = Mathf.Max(strokeMax, amplitude);
        var gain = grown - strokeMax;
        strokeMax = grown;
        return gain;
    }

    /// <summary>The share of the way still to go to the target that a dab with envelope <paramref name="gain"/> moves a value from a
    /// pixel whose envelope was <paramref name="previousMax"/>. Lerping by this after each dab, <c>1 - (1 - a1)(1 - a2)...</c>,
    /// adds up to exactly the final envelope value.</summary>
    public static float RemainingShare(float gain, float previousMax) => gain <= 0f ? 0f : gain / Mathf.Max(1f - previousMax, 0.0001f);
}
