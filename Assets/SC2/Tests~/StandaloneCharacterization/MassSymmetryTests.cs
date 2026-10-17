using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

namespace SC2CharacterizationTests;

public class MassSymmetryTests
{
    private static readonly Vector3 Center = new Vector3(512f, 0f, 512f);

    private static SC2ObjMarker Mass(int id, float x, float z, string type = "Mass") =>
        new SC2ObjMarker { Id = id, Name = "Mass " + id, Type = type, Position = new Vector3(x, 0f, z) };

    [Fact]
    public void AMapWithoutSymmetry_NeedsNoTwins()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 100f) };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.None, Center, 4));
    }

    [Fact]
    public void WithoutASymmetrySetting_ALoneMassIsUnpaired()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f) };
        var unpaired = SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(markers, Center);
        Assert.Equal(1, Assert.Single(unpaired).Id);

        var report = SC2MassSymmetryValidator.Validate(markers, SymmetryMode.None, Center, 4);
        var message = Assert.Single(report.Messages);
        Assert.Equal(SC2ValidationSeverity.Warning, message.Severity);
        Assert.Equal("1", message.ObjectId);
        Assert.Contains("has no symmetric twin", message.Message);
    }

    [Fact]
    public void WithoutASymmetrySetting_APairThatAnyStandardSymmetryMapsOntoEachOther_IsPaired()
    {
        Assert.Empty(SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 924f, 40f) }, Center));
        // The two sides of a diagonal mirror.
        Assert.Empty(SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 40f, 100f) }, Center));
        // A quarter turn alone is not a pair: a 4-way rotation also wants the other two copies.
        Assert.Equal(2, SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 40f, 924f) }, Center).Count);
        Assert.Empty(SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(new List<SC2ObjMarker> { Mass(1, 512f, 300f) }, Center));
    }

    [Fact]
    public void WithoutASymmetrySetting_AnExtraMassNextToAPair_IsUnpaired()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 924f, 40f), Mass(3, 300f, 600f) };
        var unpaired = SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(markers, Center);
        Assert.Equal(new[] { 3 }, unpaired.Select(m => m.Id).ToArray());
    }

    private static SC2ObjMarker Army(int id, float x, float z) =>
        new SC2ObjMarker { Id = id, Name = "ARMY_" + id, Type = "Blank Marker", Position = new Vector3(x, 0f, z) };

    [Fact]
    public void ALoneArmyOnAMirroredMap_IsMissingItsTwin_AMassMarkerIsNotAnArmy()
    {
        var markers = new List<SC2ObjMarker> { Army(1, 100f, 40f), Mass(2, 300f, 300f), Mass(3, 724f, 300f) };
        var report = SC2ArmySymmetryValidator.Validate(markers, SymmetryMode.X, Center, 4);
        var message = Assert.Single(report.Messages);
        Assert.Equal(SC2ValidationSeverity.Warning, message.Severity);
        Assert.Equal("1", message.ObjectId);
        Assert.Contains("Army 'ARMY_1'", message.Message);
        Assert.Contains("has no symmetric twin", message.Message);
        Assert.Empty(SC2MassSymmetryValidator.Validate(markers, SymmetryMode.X, Center, 4).Messages);
    }

    [Fact]
    public void AMirroredArmyPair_HasItsTwins()
    {
        var markers = new List<SC2ObjMarker> { Army(1, 100f, 40f), Army(2, 924f, 40f) };
        Assert.Empty(SC2ArmySymmetryValidator.Validate(markers, SymmetryMode.X, Center, 4).Messages);
    }

    [Fact]
    public void WithoutASymmetrySetting_ALoneArmyIsUnpaired_AMirroredPairIsNot()
    {
        var lone = SC2ArmySymmetryValidator.Validate(new List<SC2ObjMarker> { Army(1, 100f, 40f) }, SymmetryMode.None, Center, 4);
        Assert.Equal(SC2ValidationSeverity.Warning, Assert.Single(lone.Messages).Severity);
        Assert.Empty(SC2ArmySymmetryValidator.Validate(new List<SC2ObjMarker> { Army(1, 100f, 40f), Army(2, 924f, 984f) }, SymmetryMode.None, Center, 4).Messages);
    }

    [Fact]
    public void AMirroredPair_HasItsTwins()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 924f, 40f) };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.X, Center, 4));
    }

    [Fact]
    public void ALoneMassOnAMirroredMap_IsMissingItsTwinAtTheMirroredPoint()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f) };
        var missing = SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.X, Center, 4);

        var twin = Assert.Single(missing);
        Assert.Equal(1, twin.MarkerId);
        Assert.Equal(924f, twin.Expected.x, 3);
        Assert.Equal(40f, twin.Expected.z, 3);
    }

    [Fact]
    public void ATwinThatIsAFewUnitsOff_StillCounts_AFarOneDoesNot()
    {
        var snapped = new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 925f, 41f) };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(snapped, SymmetryMode.X, Center, 4));

        // The second marker is too far from the first one's mirrored point, so neither has a twin.
        var wrong = new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 930f, 60f) };
        Assert.Equal(2, SC2MassSymmetryCheck.FindMissingTwins(wrong, SymmetryMode.X, Center, 4).Count);
    }

    [Fact]
    public void AMassOnTheAxis_IsItsOwnTwin()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 512f, 300f) };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.X, Center, 4));
    }

    [Fact]
    public void OnlyMassMarkersAreChecked()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f, "Hydrocarbon"), Mass(2, 200f, 50f, "Blank Marker") };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.X, Center, 4));
        Assert.True(SC2MassSymmetryCheck.IsMass(Mass(3, 0f, 0f, "mass")));
    }

    [Fact]
    public void FourWayRotation_WantsThreeCopiesOfALoneMass()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 612f, 512f) };
        var missing = SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.Radial, Center, 4);

        Assert.Equal(3, missing.Count);
        Assert.Contains(missing, t => Mathf.Abs(t.Expected.x - 512f) < 0.01f && Mathf.Abs(t.Expected.z - 612f) < 0.01f);
        Assert.Contains(missing, t => Mathf.Abs(t.Expected.x - 412f) < 0.01f && Mathf.Abs(t.Expected.z - 512f) < 0.01f);
        Assert.Contains(missing, t => Mathf.Abs(t.Expected.x - 512f) < 0.01f && Mathf.Abs(t.Expected.z - 412f) < 0.01f);
    }

    [Fact]
    public void APointTwoMarkersBothMiss_IsReportedOnce()
    {
        // Two of the four rotational copies exist; each of them asks for the same two missing points.
        var markers = new List<SC2ObjMarker> { Mass(1, 612f, 512f), Mass(2, 412f, 512f) };
        var missing = SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.Radial, Center, 4);
        Assert.Equal(2, missing.Count);
    }

    [Fact]
    public void ACompleteRotationalSet_IsSymmetric()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 612f, 512f), Mass(2, 512f, 612f), Mass(3, 412f, 512f), Mass(4, 512f, 412f) };
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, SymmetryMode.Radial, Center, 4));
    }

    [Theory]
    [InlineData(SymmetryMode.Y)]
    [InlineData(SymmetryMode.XY)]
    [InlineData(SymmetryMode.DiagonalTLBR)]
    [InlineData(SymmetryMode.DiagonalTRBL)]
    [InlineData(SymmetryMode.DiagonalBoth)]
    public void EveryModeFindsATwinPlacedWhereTheSlotMathPutsIt(SymmetryMode mode)
    {
        var origin = new Vector3(300f, 0f, 200f);
        var markers = new List<SC2ObjMarker>();
        var id = 1;
        foreach (var slot in SC2SymmetryMath.GetSlots(mode, Center, 4, origin))
            markers.Add(Mass(id++, slot.x, slot.z));
        Assert.Empty(SC2MassSymmetryCheck.FindMissingTwins(markers, mode, Center, 4));

        markers.RemoveAt(markers.Count - 1);
        Assert.NotEmpty(SC2MassSymmetryCheck.FindMissingTwins(markers, mode, Center, 4));
    }

    [Fact]
    public void TheValidator_WarnsAboutEachMissingTwin_NamingTheMarkerAndThePoint()
    {
        var markers = new List<SC2ObjMarker> { Mass(7, 100f, 40f) };
        var report = SC2MassSymmetryValidator.Validate(markers, SymmetryMode.X, Center, 4);

        var message = Assert.Single(report.Messages);
        Assert.Equal(SC2ValidationSeverity.Warning, message.Severity);
        Assert.Equal("OBJ006", message.Code);
        Assert.Equal("7", message.ObjectId);
        Assert.Contains("Mass 7", message.Message);
        Assert.Contains("(924, 40)", message.Message);
        Assert.Contains("mirrored left-right", message.Message);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void TheValidator_SaysNothingForASymmetricMap()
    {
        var markers = new List<SC2ObjMarker> { Mass(1, 100f, 40f), Mass(2, 924f, 40f) };
        Assert.Empty(SC2MassSymmetryValidator.Validate(markers, SymmetryMode.X, Center, 4).Messages);
        Assert.Empty(SC2MassSymmetryValidator.Validate(null, SymmetryMode.X, Center, 4).Messages);
    }
}
