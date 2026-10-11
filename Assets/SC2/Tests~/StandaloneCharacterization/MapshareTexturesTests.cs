using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class MapshareTexturesTests
{
    private static SC2DirectoryBlobStore Store()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc2-mapshare-textures-" + System.Guid.NewGuid().ToString("N"));
        return new SC2DirectoryBlobStore(root);
    }

    [Theory]
    [InlineData("/textures/Terrain/CA_C03/Rock_d.win.dds", "textures/terrain/ca_c03/rock_d")]
    [InlineData("textures/Terrain/CA_C03/Rock_d.dds", "textures/terrain/ca_c03/rock_d")]
    [InlineData("SC2Textures/textures/Terrain/CA_C03/Rock_d", "textures/terrain/ca_c03/rock_d")]
    [InlineData("\\textures\\Terrain\\CA_C03\\Rock_d.dds", "textures/terrain/ca_c03/rock_d")]
    [InlineData("file:C:\\Lib\\Rock_ab12.dds", "file:c:/lib/rock_ab12.dds")]
    public void KeyOf_MakesEverySpellingOfOneTextureMeet(string path, string key)
    {
        Assert.Equal(key, SC2MapshareTextures.KeyOf(path));
    }

    [Fact]
    public void ACapturedTexture_IsFoundAgainAfterTheMapIsLoaded()
    {
        var store = Store();
        var list = new List<SC2MapshareTextureDocument>();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        SC2MapshareTextures.Capture(list, store, "/textures/Terrain/X/y_d.win.dds", bytes, ".dds");
        Assert.Single(list);

        SC2MapshareTextures.Register(null, store); // another map was open in between
        Assert.False(SC2MapshareTextures.Has("textures/terrain/x/y_d"));

        SC2MapshareTextures.Register(list, store);
        Assert.True(SC2MapshareTextures.TryRead("SC2Textures/textures/Terrain/X/y_d", out var read, out var extension));
        Assert.Equal(bytes, read);
        Assert.Equal(".dds", extension);
    }

    [Fact]
    public void ASaveOnAMachineWithoutTheFile_CarriesTheTextureOn()
    {
        var store = Store();
        var first = new List<SC2MapshareTextureDocument>();
        SC2MapshareTextures.Capture(first, store, "file:C:/Lib/rock_ab12.dds", new byte[] { 9, 9, 9 }, ".dds");
        SC2MapshareTextures.Register(first, store);

        // The map is saved again where the file does not exist: no bytes, yet the copy the map had survives.
        var second = new List<SC2MapshareTextureDocument>();
        SC2MapshareTextures.Capture(second, store, "file:C:/Lib/rock_ab12.dds", null, ".dds");
        Assert.Single(second);
        Assert.Equal(first[0].Blob.ContentHash, second[0].Blob.ContentHash);
    }

    [Fact]
    public void ATextureWithNoFileAndNoCopy_IsLeftOut()
    {
        var store = Store();
        SC2MapshareTextures.Register(null, store);
        var list = new List<SC2MapshareTextureDocument>();
        SC2MapshareTextures.Capture(list, store, "/textures/Terrain/X/missing_d.dds", null, ".dds");
        Assert.Empty(list);
    }

    [Fact]
    public void TheTexturesSurviveTheManifestRoundTrip_AndStayAliveForTheBlobCollector()
    {
        var store = Store();
        var doc = new SC2MapDocument();
        SC2MapshareTextures.Capture(doc.Terrain.MapshareTextures, store, "/textures/Terrain/X/y_d.dds", new byte[] { 7, 7, 7, 7 }, ".dds");

        var json = SC2MapDocumentSerializer.Serialize(doc);
        var back = SC2MapDocumentSerializer.Deserialize(json);

        Assert.Single(back.Terrain.MapshareTextures);
        Assert.Equal("textures/terrain/x/y_d", back.Terrain.MapshareTextures[0].Key);
        Assert.Equal(".dds", back.Terrain.MapshareTextures[0].Extension);
        Assert.Equal(doc.Terrain.MapshareTextures[0].Blob.ContentHash, back.Terrain.MapshareTextures[0].Blob.ContentHash);
        Assert.Contains(doc.Terrain.MapshareTextures[0].Blob.ContentHash, SC2BlobStoreGarbageCollector.CollectReferencedHashes(json));
    }
}
