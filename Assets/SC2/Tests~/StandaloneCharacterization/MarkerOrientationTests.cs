using System.Globalization;
using System.Text.RegularExpressions;
using SC2.MapEditor.Document;
using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

/// <summary>A marker's QUATERNION in a save.lua is (x, y, z, w): the identity is QUATERNION( 0, 0, 0, 1 ).</summary>
public class MarkerOrientationTests
{
    private static string Format(float f) => f.ToString(CultureInfo.InvariantCulture);

    private static float AngleBetween(Quaternion a, Quaternion b)
    {
        var dot = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
        return 2f * Mathf.Acos(Mathf.Min(1f, dot)) * Mathf.Rad2Deg;
    }

    [Fact]
    public void TheEulerHelpersRoundTrip()
    {
        foreach (var euler in new[] { new Vector3(10, 200, 30), new Vector3(0, 26, 0), new Vector3(350, 5, 90), new Vector3(90, 40, 0), new Vector3(-30, 120, 270) })
        {
            var q = SC2MarkerOrientation.EulerToQuaternion(euler);
            var back = SC2MarkerOrientation.EulerToQuaternion(SC2MarkerOrientation.QuaternionToEuler(q));
            Assert.True(AngleBetween(q, back) < 0.2f, euler.ToString());
        }
    }

    [Fact]
    public void TheIdentityIsWrittenAsZeroZeroZeroOne()
        => Assert.Equal("QUATERNION( 0, 0, 0, 1 )", SC2MarkerOrientation.FormatQuaternion(SC2MarkerOrientation.EulerToQuaternion(Vector3.zero), Format));

    [Fact]
    public void AGameQuaternionIsReadInTheOrderTheGameWritesIt()
    {
        var identity = SC2LuaValue.ExtractQuaternion("QUATERNION( 0, 0, 0, 1 )").Value;
        Assert.Equal(1f, identity.w);
        Assert.Equal(0f, identity.z);

        // From a real map: a turn about the vertical axis (x = z = 0, y = 0.225, w = -0.974).
        var turned = SC2LuaValue.ExtractQuaternion("QUATERNION( 0, 0.225079, 0, -0.974341 )").Value;
        Assert.Equal(0.225079f, turned.y, 5);
        Assert.Equal(-0.974341f, turned.w, 5);
        Assert.Equal(0f, turned.x);
    }

    [Fact]
    public void WritingAndReadingBackKeepsTheRotation()
    {
        foreach (var euler in new[] { new Vector3(0, 0, 0), new Vector3(0, 90, 0), new Vector3(10, 200, 30), new Vector3(0, 26, 0) })
        {
            var q = SC2MarkerOrientation.EulerToQuaternion(euler);
            var text = SC2MarkerOrientation.FormatQuaternion(q, Format);
            var back = SC2LuaValue.ExtractQuaternion(text).Value;
            Assert.True(AngleBetween(q, back) < 0.2f, text);
        }
    }

    [Fact]
    public void MigratingAnOlderMarkerAtZeroKeepsTheIdentity()
        => Assert.Equal(Vector3.zero, SC2MarkerOrientation.MigrateLegacyEuler(Vector3.zero));

    [Fact]
    public void MigratingAMarkerDecodedFromAGameFileRestoresTheRealRotation()
    {
        // An older version read the file values (a, b, c, d) as (w, x, y, z), so it stored the Euler angles of Quaternion(b, c, d, a).
        foreach (var (a, b, c, d) in new[] { (0f, 0f, 0f, 1f), (0f, 0.225079f, 0f, -0.974341f), (-0.012999f, 0f, 0.043998f, 0.998947f) })
        {
            var storedByOlderVersion = SC2MarkerOrientation.QuaternionToEuler(new Quaternion(b, c, d, a));
            var migrated = SC2MarkerOrientation.EulerToQuaternion(SC2MarkerOrientation.MigrateLegacyEuler(storedByOlderVersion));
            var real = new Quaternion(a, b, c, d);
            Assert.True(AngleBetween(real, migrated) < 0.2f, $"({a}, {b}, {c}, {d})");
        }
    }

    private static SC2MapDocument DocumentWithAMarker(Vector3 euler)
    {
        var doc = new SC2MapDocument();
        doc.Objects.Markers.Add(new SC2ObjMarker { Id = 1, Name = "Marker_1", Type = "Mass", OrientationEuler = euler, OrientationIsQuaternion = true });
        return doc;
    }

    [Fact]
    public void ADocumentStoresAndReloadsAMarkerOrientationUnchanged()
    {
        var json = SC2MapDocumentSerializer.Serialize(DocumentWithAMarker(new Vector3(0, 90, 0)));
        Assert.Contains("orientationFormat", json);
        var marker = SC2MapDocumentSerializer.Deserialize(json).Objects.Markers[0];
        Assert.Equal(90f, marker.OrientationEuler.y, 3);
    }

    [Fact]
    public void ADocumentFromBeforeTheFixIsMigratedOnLoad()
    {
        // Older versions stored the Euler angles of a vanilla identity marker (file value 0, 0, 0, 1) as the rotation Quaternion(0, 0, 1, 0).
        var legacy = SC2MarkerOrientation.QuaternionToEuler(new Quaternion(0f, 0f, 1f, 0f));
        var json = SC2MapDocumentSerializer.Serialize(DocumentWithAMarker(legacy));
        json = Regex.Replace(json, @"""orientationFormat""\s*:\s*\d+\s*,?", "");
        var marker = SC2MapDocumentSerializer.Deserialize(json).Objects.Markers[0];
        Assert.True(AngleBetween(Quaternion.identity, SC2MarkerOrientation.EulerToQuaternion(marker.OrientationEuler)) < 0.2f);
    }
}
