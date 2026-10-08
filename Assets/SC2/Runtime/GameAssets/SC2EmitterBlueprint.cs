using System;
using System.Collections.Generic;

/// <summary>One key of an emitter curve: the value <see cref="Y"/> at position <see cref="X"/> on the emitter clock, randomised by a total
/// width of <see cref="Z"/> when a particle is born.</summary>
public readonly struct SC2EmitterCurveKey
{
    public readonly float X, Y, Z;
    public SC2EmitterCurveKey(float x, float y, float z) { X = x; Y = y; Z = z; }
}

/// <summary>
/// One animated value of a game particle emitter (an <c>XxxCurve = { XRange = ..., Keys = { { x=, y=, z= }, ... } }</c> table of an
/// effects/Emitters/*.bp): <c>y</c> is the value at the key's <c>x</c> along the emitter's own clock (0 to <see cref="XRange"/>), and
/// <c>z</c> is how far that value is randomised at the moment a particle is born. A curve with one key is a constant. The
/// randomisation is read as a total width centred on <c>y</c> (value = y + (random - 0.5) * z), which is what the game's own data
/// implies: an initial rotation of y = 180, z = 360 covers one full turn and a ramp row of y = 0.5, z = 1 covers the whole ramp.
/// </summary>
public sealed class SC2EmitterCurve
{
    public float XRange;
    public readonly List<SC2EmitterCurveKey> Keys = new List<SC2EmitterCurveKey>();

    public bool IsEmpty => Keys.Count == 0;

    /// <summary>The value and its random width at <paramref name="clock"/>: linear between keys, held beyond the first and last.</summary>
    public void Evaluate(float clock, out float value, out float width)
    {
        if (Keys.Count == 0) { value = 0f; width = 0f; return; }
        if (Keys.Count == 1 || clock <= Keys[0].X) { value = Keys[0].Y; width = Keys[0].Z; return; }
        var last = Keys[Keys.Count - 1];
        if (clock >= last.X) { value = last.Y; width = last.Z; return; }
        for (var i = 1; i < Keys.Count; i++)
        {
            var b = Keys[i];
            if (clock > b.X) continue;
            var a = Keys[i - 1];
            var span = b.X - a.X;
            var t = span > 1e-6f ? (clock - a.X) / span : 1f;
            value = a.Y + (b.Y - a.Y) * t;
            width = a.Z + (b.Z - a.Z) * t;
            return;
        }
        value = last.Y; width = last.Z;
    }

    /// <summary>The value at <paramref name="clock"/> with the random width applied: <paramref name="random01"/> is a number in [0, 1).</summary>
    public float Sample(float clock, float random01)
    {
        Evaluate(clock, out var value, out var width);
        return value + (random01 - 0.5f) * width;
    }
}

/// <summary>The parts of a game <c>EmitterBlueprint</c> that drive the editor's particle preview.</summary>
public sealed class SC2EmitterBlueprint
{
    public string Name = "";
    public string Texture = "";
    public string RampTexture = "";

    /// <summary>Particle blending: 0 = alpha blend, 3 = additive (the only two the map marker effects use); anything else is drawn as alpha blend.</summary>
    public int BlendMode;

    /// <summary>Seconds the emitter lives in game ticks; negative = it never ends.</summary>
    public float Lifetime = -1f;
    /// <summary>The emitter clock repeats every this many ticks (0 = it does not repeat).</summary>
    public float RepeatTime;

    public bool Flat;
    public bool AlignRotation;
    public bool LocalVelocity = true;
    public bool LocalAcceleration;
    public bool ParticleResistance;
    public bool SnapToWaterline;
    /// <summary>Camera distance beyond which the game stops drawing the emitter (0 or less = no limit).</summary>
    public float LodCutoff;
    public int TextureStripCount = 1;

    public readonly Dictionary<string, SC2EmitterCurve> Curves = new Dictionary<string, SC2EmitterCurve>(StringComparer.Ordinal);

    public SC2EmitterCurve Curve(string name) => Curves.TryGetValue(name, out var curve) ? curve : null;

    /// <summary>The names of the curves that are read; any other field of the file is ignored.</summary>
    public static readonly string[] CurveNames =
    {
        "EmitRateCurve", "LifetimeCurve", "XPosCurve", "YPosCurve", "ZPosCurve", "VelocityCurve",
        "XDirectionCurve", "YDirectionCurve", "ZDirectionCurve", "XAccelCurve", "YAccelCurve", "ZAccelCurve", "ResistanceCurve",
        "StartSizeCurve", "EndSizeCurve", "InitialRotationCurve", "RotationRateCurve", "TextureSelectionCurve", "RampSelectionCurve",
    };
}
