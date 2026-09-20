using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The imported preview pictures and the Collision2 tab's meshes/flags are stored in the map document, so a
/// normal load (which reads the document package) brings them back.</summary>
public class PreviewImagePersistenceTests
{
    [Fact]
    public void Serializer_RoundTripsThePreviewImageAndCollision2References()
    {
        var doc = new SC2MapDocument();
        doc.Scenario.MapShotBgra = new SC2BlobReference("aa11", 448 * 206 * 4);
        doc.Scenario.CustomMinimapPng = new SC2BlobReference("bb22", 12345);
        doc.Collision2.PreviewMeshBytes = new SC2BlobReference("cc33", 999);
        doc.Collision2.CustomMeshBytes = new SC2BlobReference("dd44", 888);
        doc.Collision2.UseCustomMesh = true;
        doc.Collision2.HidePreview = true;
        doc.Collision2.Wireframe = true;

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.Equal("aa11", restored.Scenario.MapShotBgra.ContentHash);
        Assert.Equal(448 * 206 * 4, restored.Scenario.MapShotBgra.Length);
        Assert.Equal("bb22", restored.Scenario.CustomMinimapPng.ContentHash);
        Assert.Equal("cc33", restored.Collision2.PreviewMeshBytes.ContentHash);
        Assert.Equal("dd44", restored.Collision2.CustomMeshBytes.ContentHash);
        Assert.True(restored.Collision2.UseCustomMesh);
        Assert.True(restored.Collision2.HidePreview);
        Assert.True(restored.Collision2.Wireframe);
    }

    [Fact]
    public void Serializer_ReadsADocumentWithoutTheKeysAsEmpty()
    {
        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));
        Assert.True(restored.Scenario.MapShotBgra.IsEmpty);
        Assert.True(restored.Scenario.CustomMinimapPng.IsEmpty);
        Assert.True(restored.Collision2.PreviewMeshBytes.IsEmpty);
        Assert.True(restored.Collision2.CustomMeshBytes.IsEmpty);
        Assert.False(restored.Collision2.UseCustomMesh);
    }
}
