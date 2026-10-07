using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

public class StampGestureTests
{
    private static SC2StampGesture Started(float radius = 20f, float angle = 0f)
    {
        var gesture = new SC2StampGesture();
        gesture.Begin(new Vector3(100f, 0f, 200f), radius, angle);
        return gesture;
    }

    [Fact]
    public void AClickWithoutADragKeepsTheDefaultSizeAndAngle()
    {
        var gesture = Started(20f, 0.5f);
        gesture.Drag(new Vector3(100.5f, 0f, 200.5f), true, true, 4f, 1f, 1000f);
        Assert.False(gesture.Dragged);
        Assert.Equal(20f, gesture.Radius);
        Assert.Equal(0.5f, gesture.AngleRad);
    }

    [Fact]
    public void DraggingSetsTheRadiusToTheDistanceAndTheAngleToTheDirection()
    {
        var gesture = Started();
        gesture.Drag(new Vector3(100f, 0f, 250f), true, true, 4f, 1f, 1000f); // 50 units along +Z
        Assert.True(gesture.Dragged);
        Assert.Equal(50f, gesture.Radius, 3);
        Assert.Equal(Mathf.PI * 0.5f, gesture.AngleRad, 4);
    }

    [Fact]
    public void TheAngleOffsetIsAddedToTheDragDirection()
    {
        var gesture = Started(20f, Mathf.PI * 0.25f);
        gesture.Drag(new Vector3(130f, 0f, 200f), true, true, 4f, 1f, 1000f); // along +X
        Assert.Equal(Mathf.PI * 0.25f, gesture.AngleRad, 4);
    }

    [Fact]
    public void SizeAndAngleCanEachBeLeftToTheirDefaults()
    {
        var gesture = Started(20f, 0.3f);
        gesture.Drag(new Vector3(100f, 0f, 260f), false, true, 4f, 1f, 1000f);
        Assert.Equal(20f, gesture.Radius);
        Assert.Equal(0.3f + Mathf.PI * 0.5f, gesture.AngleRad, 4);

        gesture.Drag(new Vector3(100f, 0f, 260f), true, false, 4f, 1f, 1000f);
        Assert.Equal(60f, gesture.Radius, 3);
        Assert.Equal(0.3f, gesture.AngleRad, 4);
    }

    [Fact]
    public void TheRadiusIsClampedAndComingBackInsideTheDeadZoneAfterADragKeepsTheDragValues()
    {
        var gesture = Started();
        gesture.Drag(new Vector3(100f, 0f, 5000f), true, true, 4f, 1f, 1000f);
        Assert.Equal(1000f, gesture.Radius);
        gesture.Drag(new Vector3(100f, 0f, 200.2f), true, true, 4f, 1f, 1000f);
        Assert.True(gesture.Radius >= 1f); // once dragged, the size follows the mouse all the way in
    }

    [Fact]
    public void TheAngleCanSnap()
    {
        var gesture = Started();
        var step = Mathf.PI / 12f; // 15 degrees
        gesture.Drag(new Vector3(100f + 50f * Mathf.Cos(0.3f), 0f, 200f + 50f * Mathf.Sin(0.3f)), true, true, 4f, 1f, 1000f, step);
        Assert.Equal(Mathf.Round(0.3f / step) * step, gesture.AngleRad, 4);
    }

    [Fact]
    public void CancelEndsTheGesture()
    {
        var gesture = Started();
        gesture.Cancel();
        Assert.False(gesture.Active);
        gesture.Drag(new Vector3(300f, 0f, 300f), true, true, 4f, 1f, 1000f);
        Assert.Equal(20f, gesture.Radius);
    }

    [Fact]
    public void TheBrushRotationTurnsTheContentTheOppositeWay()
    {
        Assert.Equal(-1.25f, SC2StampGesture.BrushRotation(1.25f));
        // The shader/sampler rotate the sample offset by the brush rotation: for the stamp's +X axis to land on world direction
        // phi, the content must be turned by -phi. Checked against the same maths SampleBrushMask uses.
        var phi = 0.8f;
        var rotation = SC2StampGesture.BrushRotation(phi);
        var offset = new Vector2(Mathf.Cos(phi), Mathf.Sin(phi)); // a point on the stamp's +X axis, in world x/z
        var s = Mathf.Sin(rotation); var c = Mathf.Cos(rotation);
        var rotated = new Vector2(offset.x * c - offset.y * s, offset.x * s + offset.y * c);
        Assert.Equal(1f, rotated.x, 4);
        Assert.Equal(0f, rotated.y, 4);
    }

    [Fact]
    public void ThePreviewMapsTheStampImageOntoTheGroundTheWayTheSamplerReadsIt()
    {
        // The preview draws image point (px, py) (py down the image rows) at anchor + radius * (px * (cos, sin) + py * (-sin, cos)).
        // SampleBrushMask takes the world offset o, rotates it by the brush rotation and reads the image at (mx, mz) = rotated offset
        // (u = mx / 2 + 0.5, row index from the bottom v = -mz / 2 + 0.5, i.e. py = mz). Both must agree.
        foreach (var phi in new[] { 0f, 0.7f, 2.1f, -1.3f })
        foreach (var (px, py) in new[] { (1f, 0f), (0f, 1f), (0.5f, -0.25f) })
        {
            var offset = new Vector2(px * Mathf.Cos(phi) - py * Mathf.Sin(phi), px * Mathf.Sin(phi) + py * Mathf.Cos(phi));
            var rotation = SC2StampGesture.BrushRotation(phi);
            var s = Mathf.Sin(rotation); var c = Mathf.Cos(rotation);
            var mx = offset.x * c - offset.y * s;
            var mz = offset.x * s + offset.y * c;
            Assert.Equal(px, mx, 4);
            Assert.Equal(py, mz, 4);
        }
    }
}
