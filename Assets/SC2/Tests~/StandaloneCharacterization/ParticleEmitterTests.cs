using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

public class ParticleEmitterTests
{
    private const string SmokeSource = @"
EmitterBlueprint {
	BlueprintId = 'smoke',
	Lifetime = -1.00,
	Repeattime = 130.00,
	Blendmode = 3.00,
	Flat = true,
	LocalVelocity = true,
	LODCutoff = 300.00,
	TextureStripcount = 2.00,
	Texture = [[/textures/particles/cloud.dds]],
	RampTexture = [[/textures/particles/ramp/ra_01.dds]],
	EmitRateCurve = { XRange = 130.0, Keys = { { x=40.0,y=2.0,z=0.0 } } },
	LifetimeCurve = { XRange = 130.0, Keys = { { x=60.0,y=20.0,z=0.0 } } },
	VelocityCurve = { XRange = 130.0, Keys = { { x=0.0,y=1.0,z=0.0 }, { x=100.0,y=3.0,z=0.0 } } },
	YDirectionCurve = { XRange = 130.0, Keys = { { x=0.0,y=1.0,z=0.0 } } },
	YAccelCurve = { XRange = 130.0, Keys = { { x=0.0,y=-0.1,z=0.0 } } },
	StartSizeCurve = { XRange = 130.0, Keys = { { x=0.0,y=4.0,z=0.0 } } },
	EndSizeCurve = { XRange = 130.0, Keys = { { x=0.0,y=8.0,z=0.0 } } },
	RotationRateCurve = { XRange = 130.0, Keys = { { x=0.0,y=90.0,z=0.0 } } },
	RampSelectionCurve = { XRange = 130.0, Keys = { { x=0.0,y=0.25,z=0.0 } } },
}";

    private static SC2EmitterBlueprint Smoke() => SC2EmitterBlueprintReader.Read(Encoding.UTF8.GetBytes(SmokeSource), "smoke");

    [Fact]
    public void TheReaderKeepsTheFieldsThePreviewNeeds()
    {
        var bp = Smoke();
        Assert.Equal("/textures/particles/cloud.dds", bp.Texture);
        Assert.Equal("/textures/particles/ramp/ra_01.dds", bp.RampTexture);
        Assert.Equal(3, bp.BlendMode);
        Assert.True(bp.Flat);
        Assert.Equal(130f, bp.RepeatTime);
        Assert.Equal(300f, bp.LodCutoff);
        Assert.Equal(2, bp.TextureStripCount);
        Assert.Equal(2, bp.Curve("VelocityCurve").Keys.Count);
        Assert.Equal(-0.1f, bp.Curve("YAccelCurve").Keys[0].Y, 4);
        Assert.Null(bp.Curve("ResistanceCurve"));
    }

    [Fact]
    public void ACurveIsLinearBetweenKeysAndHeldBeyondThem()
    {
        var curve = Smoke().Curve("VelocityCurve");
        curve.Evaluate(-5f, out var before, out _);
        curve.Evaluate(50f, out var middle, out _);
        curve.Evaluate(500f, out var after, out _);
        Assert.Equal(1f, before, 4);
        Assert.Equal(2f, middle, 4);
        Assert.Equal(3f, after, 4);
    }

    [Fact]
    public void TheRandomWidthIsCentredOnTheValue()
    {
        var curve = new SC2EmitterCurve();
        curve.Keys.Add(new SC2EmitterCurveKey(0f, 180f, 360f));
        Assert.Equal(0f, curve.Sample(0f, 0f), 3);
        Assert.Equal(180f, curve.Sample(0f, 0.5f), 3);
        Assert.Equal(360f, curve.Sample(0f, 1f), 3);
    }

    private static SC2ParticleEmitter Emitter(float scale = 1f, Vector3? origin = null, Quaternion? orientation = null, Vector3? offset = null)
        => new SC2ParticleEmitter(Smoke(), origin ?? Vector3.zero, orientation ?? Quaternion.identity, scale, offset ?? Vector3.zero, 1);

    [Fact]
    public void ItEmitsAtTheCurveRateAndParticlesLiveTheirLifetime()
    {
        var emitter = Emitter();
        emitter.Advance(10f);
        // 2 particles per tick for 10 ticks, none old enough to have died (lifetime 20 ticks).
        Assert.InRange(emitter.Count, 19, 20);
        emitter.Advance(100f);
        // Steady state: rate x lifetime = 40 live particles.
        Assert.InRange(emitter.Count, 38, 41);
    }

    [Fact]
    public void ParticlesMoveGrowTurnAndAgeTheWayTheBlueprintSays()
    {
        var emitter = Emitter();
        emitter.Advance(1f);
        emitter.Advance(10f);
        var particles = new List<SC2ParticleEmitter.Particle>();
        emitter.GetParticles(particles);
        Assert.NotEmpty(particles);
        foreach (var p in particles)
        {
            Assert.InRange(p.Life01, 0f, 1f);
            // Size is linear from 4 to 8 over the life.
            Assert.Equal(4f + 4f * p.Life01, p.Size, 3);
            // Rotation turns 90 degrees per tick: age = life01 * 20 ticks.
            Assert.Equal(90f * p.Life01 * 20f * Mathf.Deg2Rad, p.Rotation, 3);
            Assert.Equal(0.25f, p.RampRow, 4);
            Assert.True(p.Position.y >= 0f);
        }
    }

    [Fact]
    public void TheScaleMultipliesSize()
    {
        var emitter = Emitter(scale: 3f);
        emitter.Advance(5f);
        var particles = new List<SC2ParticleEmitter.Particle>();
        emitter.GetParticles(particles);
        foreach (var p in particles) Assert.Equal((4f + 4f * p.Life01) * 3f, p.Size, 3);
    }

    [Fact]
    public void TheMarkerPositionRotationAndOffsetPlaceTheEmitter()
    {
        var emitter = Emitter(origin: new Vector3(100f, 5f, 200f), offset: new Vector3(0f, 2f, 0f));
        emitter.Advance(1f);
        var particles = new List<SC2ParticleEmitter.Particle>();
        emitter.GetParticles(particles);
        foreach (var p in particles)
        {
            Assert.Equal(100f, p.Position.x, 2);
            Assert.True(p.Position.y >= 7f - 0.5f);
            Assert.Equal(200f, p.Position.z, 2);
        }

        // Turning the marker 90 degrees about Z sends its local up velocity along world -X.
        var turned = Emitter(orientation: SC2MarkerOrientation.EulerToQuaternion(new Vector3(0f, 0f, 90f)));
        turned.Advance(5f);
        turned.GetParticles(particles);
        foreach (var p in particles) Assert.True(p.Direction.x < -0.8f, p.Direction.ToString()); // a little below -1: gravity bends the path down
    }

    [Fact]
    public void DragSlowsAParticleDownTowardItsTerminalVelocity()
    {
        var start = Vector3.zero; var velocity = new Vector3(10f, 0f, 0f);
        var free = SC2ParticleEmitter.PositionAt(start, velocity, Vector3.zero, 0f, 5f);
        var dragged = SC2ParticleEmitter.PositionAt(start, velocity, Vector3.zero, 0.5f, 5f);
        Assert.Equal(50f, free.x, 3);
        Assert.Equal(10f * (1f - Mathf.Exp(-2.5f)) / 0.5f, dragged.x, 3);
        Assert.True(dragged.x < free.x);
    }

    [Fact]
    public void ItNeverHoldsMoreThanTheParticleCap()
    {
        var emitter = new SC2ParticleEmitter(Smoke(), Vector3.zero, Quaternion.identity, 1f, Vector3.zero, 1, null, 10);
        emitter.Advance(100f);
        Assert.True(emitter.Count <= 10);
    }

    [Fact]
    public void TheSameSeedGivesTheSameParticles()
    {
        var a = Emitter(); var b = Emitter();
        a.Advance(30f); b.Advance(30f);
        var pa = new List<SC2ParticleEmitter.Particle>(); var pb = new List<SC2ParticleEmitter.Particle>();
        a.GetParticles(pa); b.GetParticles(pb);
        Assert.Equal(pa.Count, pb.Count);
        for (var i = 0; i < pa.Count; i++) Assert.Equal(pa[i].Position, pb[i].Position);
    }

    [Fact]
    public void ChangingTheTransformMovesTurnsAndScalesTheParticlesAlreadyAlive()
    {
        var emitter = Emitter();
        emitter.Advance(10f);
        var before = new List<SC2ParticleEmitter.Particle>();
        emitter.GetParticles(before);
        Assert.NotEmpty(before);

        emitter.SetTransform(new Vector3(50f, 0f, 0f), SC2MarkerOrientation.EulerToQuaternion(new Vector3(0f, 0f, 90f)), 2f, Vector3.zero, null);
        var after = new List<SC2ParticleEmitter.Particle>();
        emitter.GetParticles(after);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            // Twice the size, and every position turned 90 degrees about Z, doubled and moved 50 along X.
            Assert.Equal(before[i].Size * 2f, after[i].Size, 3);
            var local = before[i].Position;
            Assert.Equal(50f - 2f * local.y, after[i].Position.x, 2);
            Assert.Equal(2f * local.x, after[i].Position.y, 2);
            Assert.Equal(2f * local.z, after[i].Position.z, 2);
        }
    }
}
