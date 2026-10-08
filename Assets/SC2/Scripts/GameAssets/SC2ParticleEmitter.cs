using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A simplified run of one game particle emitter, for the editor's preview of Effect markers. It follows what the game does with the
/// emitter blueprints of a marker effect (lua/simInit.lua creates every emitter of the template on an entity at the marker, scaled by
/// EmitterScale and moved by EmitterOffset) and what the particle shader (effects/effects.hlsl) makes of a particle: a start position
/// and velocity, an acceleration (with the optional drag term), a size that changes linearly over the particle's life, a rotation that
/// turns at a constant rate, and a colour read from a ramp texture by life and ramp row.
///
/// Approximate by design. Time is in game ticks (0.1 s), which the blueprints' values imply but the engine's own scale is not known;
/// the random width of a curve is read as a total width; the emitter's scale multiplies size, spread and speed; the texture-distortion
/// maps are not applied. Deterministic for a given seed.
/// </summary>
public sealed class SC2ParticleEmitter
{
    public const float TicksPerSecond = 10f;
    public const int DefaultMaxParticles = 600;

    /// <summary>One live particle, already evaluated at the emitter's current time.</summary>
    public struct Particle
    {
        public Vector3 Position;
        public Vector3 Direction;   // unit vector the particle travels along (zero when it has no velocity)
        public float Size;          // full width and height of the quad, in world units
        public float Rotation;      // radians
        public float Life01;        // 0 at birth, 1 at death: the column of the ramp texture
        public float RampRow;       // 0..1: the row of the ramp texture, fixed at birth
        public int Strip;           // which strip of the particle texture, fixed at birth
    }

    private struct Born
    {
        public Vector3 Position, Velocity, Acceleration;
        public float Drag;
        public float BornAt, Life;
        public float StartSize, EndSize;
        public float Rotation0, RotationRate; // degrees, degrees per tick
        public float RampRow;
        public int Strip;
    }

    private readonly SC2EmitterBlueprint _blueprint;
    // The marker's place, turn, scale and offset. Particles are simulated in the emitter's own frame (unscaled, unturned) and placed in the
    // world only when they are read, so changing these (SetTransform) moves, turns and scales the particles already alive at once.
    private Vector3 _origin;
    private Quaternion _orientation;
    private float _scale;
    private Vector3 _offset;
    private float? _waterLevel;
    private readonly int _maxParticles;
    private readonly System.Random _random;
    private readonly List<Born> _live = new List<Born>();
    private float _time;
    private float _emitDebt;

    public SC2EmitterBlueprint Blueprint => _blueprint;
    public float Time => _time;
    public int Count => _live.Count;

    /// <param name="origin">The marker's position.</param>
    /// <param name="orientation">The marker's rotation: the emitter's local offsets, velocity and acceleration turn with it.</param>
    /// <param name="scale">EmitterScale.</param>
    /// <param name="offset">EmitterOffset, in the marker's local frame.</param>
    /// <param name="waterLevel">The height particles of an emitter that snaps to the waterline are placed at (null = none).</param>
    public SC2ParticleEmitter(SC2EmitterBlueprint blueprint, Vector3 origin, Quaternion orientation, float scale, Vector3 offset,
        int seed, float? waterLevel = null, int maxParticles = DefaultMaxParticles)
    {
        _blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
        SetTransform(origin, orientation, scale, offset, waterLevel);
        _maxParticles = Mathf.Max(1, maxParticles);
        _random = new System.Random(seed);
    }

    /// <summary>Changes where the emitter sits and how it is turned and scaled; every live particle follows immediately.</summary>
    public void SetTransform(Vector3 origin, Quaternion orientation, float scale, Vector3 offset, float? waterLevel)
    {
        _origin = origin;
        _orientation = orientation;
        _scale = scale > 0f ? scale : 1f;
        _offset = offset;
        _waterLevel = waterLevel;
    }

    private float Rand() => (float)_random.NextDouble();

    private float Sample(string curveName, float clock, float fallback = 0f)
    {
        var curve = _blueprint.Curve(curveName);
        return curve == null || curve.IsEmpty ? fallback : curve.Sample(clock, Rand());
    }

    /// <summary>The emitter's own clock: it runs from 0 and, when the blueprint repeats, wraps every RepeatTime ticks.</summary>
    private float Clock(float time) => _blueprint.RepeatTime > 0f ? time % _blueprint.RepeatTime : time;

    private bool Emitting(float time) => _blueprint.RepeatTime > 0f || _blueprint.Lifetime < 0f || time < _blueprint.Lifetime;

    /// <summary>Runs the emitter forward by <paramref name="ticks"/>, in steps of at most one tick.</summary>
    public void Advance(float ticks)
    {
        while (ticks > 1e-4f)
        {
            var step = Mathf.Min(ticks, 1f);
            ticks -= step;
            Step(step);
        }
    }

    /// <summary>Runs the emitter forward as it would have run for <paramref name="ticks"/> before anyone looked, so a smoke plume does not
    /// start empty. Capped so the cost stays small.</summary>
    public void Prewarm(float ticks) => Advance(Mathf.Min(ticks, 400f));

    private void Step(float step)
    {
        var start = _time;
        _time += step;

        for (var i = _live.Count - 1; i >= 0; i--)
            if (_time - _live[i].BornAt >= _live[i].Life) _live.RemoveAt(i);

        if (!Emitting(start)) return;
        var clock = Clock(start);
        var rate = Mathf.Max(0f, Sample("EmitRateCurve", clock));
        _emitDebt += rate * step;
        while (_emitDebt >= 1f)
        {
            _emitDebt -= 1f;
            if (_live.Count >= _maxParticles) continue;
            Spawn(start + Rand() * step, clock);
        }
    }

    private void Spawn(float bornAt, float clock)
    {
        var bp = _blueprint;
        var life = Mathf.Max(0.5f, Sample("LifetimeCurve", clock, 10f));

        var position = new Vector3(Sample("XPosCurve", clock), Sample("YPosCurve", clock), Sample("ZPosCurve", clock));

        var direction = new Vector3(Sample("XDirectionCurve", clock), Sample("YDirectionCurve", clock), Sample("ZDirectionCurve", clock));
        var velocity = direction.sqrMagnitude > 1e-8f ? direction.normalized * Sample("VelocityCurve", clock) : Vector3.zero;
        var acceleration = new Vector3(Sample("XAccelCurve", clock), Sample("YAccelCurve", clock), Sample("ZAccelCurve", clock));
        // Local velocity / acceleration turn with the marker; the others are along the world axes, which in the emitter's frame is
        // the marker's turn undone.
        var worldToFrame = new Quaternion(-_orientation.x, -_orientation.y, -_orientation.z, _orientation.w); // the inverse of a unit rotation
        if (!bp.LocalVelocity) velocity = worldToFrame * velocity;
        if (!bp.LocalAcceleration) acceleration = worldToFrame * acceleration;

        var strips = Mathf.Max(1, bp.TextureStripCount);
        _live.Add(new Born
        {
            Position = position,
            Velocity = velocity,
            Acceleration = acceleration,
            Drag = bp.ParticleResistance ? Mathf.Max(0f, Sample("ResistanceCurve", clock)) : 0f,
            BornAt = bornAt,
            Life = life,
            StartSize = Mathf.Max(0f, Sample("StartSizeCurve", clock, 1f)),
            EndSize = Mathf.Max(0f, Sample("EndSizeCurve", clock, 1f)),
            Rotation0 = Sample("InitialRotationCurve", clock),
            RotationRate = Sample("RotationRateCurve", clock),
            RampRow = Mathf.Clamp01(Sample("RampSelectionCurve", clock, 0.5f)),
            Strip = strips > 1 ? Mathf.Clamp(Mathf.FloorToInt(Sample("TextureSelectionCurve", clock, 0f) + 0.0001f), 0, strips - 1) : 0,
        });
    }

    /// <summary>Position after <paramref name="age"/> ticks: the shader's own formula, with the drag term when the particle has one
    /// (velocity changes as v' = a - k v).</summary>
    public static Vector3 PositionAt(Vector3 start, Vector3 velocity, Vector3 acceleration, float drag, float age)
    {
        if (drag > 1e-5f)
        {
            var exp = Mathf.Exp(-drag * age);
            return start + acceleration * (age / drag) + (velocity - acceleration / drag) * ((1f - exp) / drag);
        }
        return start + velocity * age + acceleration * (0.5f * age * age);
    }

    /// <summary>Fills <paramref name="into"/> with every live particle as it is now.</summary>
    public void GetParticles(List<Particle> into)
    {
        into.Clear();
        for (var i = 0; i < _live.Count; i++)
        {
            var b = _live[i];
            var age = _time - b.BornAt;
            if (age < 0f || age >= b.Life) continue;
            var life01 = age / b.Life;

            Vector3 direction;
            if (b.Drag > 1e-5f)
            {
                var exp = Mathf.Exp(-b.Drag * age);
                direction = (b.Velocity - b.Acceleration / b.Drag) * exp + b.Acceleration / b.Drag;
            }
            else direction = b.Velocity + b.Acceleration * age;

            var position = _origin + _orientation * (PositionAt(b.Position, b.Velocity, b.Acceleration, b.Drag, age) * _scale + _offset);
            if (_blueprint.SnapToWaterline && _waterLevel.HasValue) position.y = _waterLevel.Value;
            direction = _orientation * direction;

            into.Add(new Particle
            {
                Position = position,
                Direction = direction.sqrMagnitude > 1e-10f ? direction.normalized : Vector3.zero,
                Size = Mathf.Lerp(b.StartSize, b.EndSize, life01) * _scale,
                Rotation = (b.Rotation0 + b.RotationRate * age) * Mathf.Deg2Rad,
                Life01 = life01,
                RampRow = b.RampRow,
                Strip = b.Strip,
            });
        }
    }
}
