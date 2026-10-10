using System.IO;
using System.Linq;
using System.Text;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class SaveLayoutTests
{
    private const string Save = @"C:\Saves";

    [Fact]
    public void MapFolder_HoldsTheJsonTheManifestAndTheBlobs()
    {
        var folder = SC2SaveLayout.ShareDirectory(Save, "Alpha Factory");
        Assert.Equal(Path.Combine(Save, "Alpha Factory.sc2mapshare"), folder);
        Assert.Equal(Path.Combine(folder, "Alpha Factory.sc2terrain.json"), SC2SaveLayout.ShareJsonPath(Save, "Alpha Factory"));
        Assert.Equal(Path.Combine(folder, "Alpha Factory.sc2mapdoc", "manifest.json"), SC2SaveLayout.ShareManifestPath(Save, "Alpha Factory"));
        Assert.Equal(Path.Combine(folder, "Blobs"), SC2SaveLayout.ShareBlobsDirectory(Save, "Alpha Factory"));
    }

    [Fact]
    public void EarlierLayout_IsDirectlyInTheSaveDirectory()
    {
        Assert.Equal(Path.Combine(Save, "Canis.sc2terrain.json"), SC2SaveLayout.LegacyJsonPath(Save, "Canis"));
        Assert.Equal(Path.Combine(Save, "Canis.sc2mapdoc", "manifest.json"), SC2SaveLayout.LegacyManifestPath(Save, "Canis"));
        Assert.Equal(Path.Combine(Save, "Blobs"), SC2SaveLayout.LegacyBlobsDirectory(Save));
    }

    [Fact]
    public void SafeName_ReplacesCharactersAFileNameCannotHold_AndNamesAnEmptyMap()
    {
        Assert.Equal("a_b_c", SC2SaveLayout.SafeName("a/b?c"));
        Assert.Equal("Untitled", SC2SaveLayout.SafeName(""));
        Assert.Equal("Untitled", SC2SaveLayout.SafeName("   "));
        Assert.Equal("Untitled", SC2SaveLayout.SafeName(null));
        Assert.Equal("BDFTM  Commanders Rift", SC2SaveLayout.SafeName("BDFTM  Commanders Rift"));
    }

    [Fact]
    public void BackupFolder_IsPerMap()
    {
        Assert.Equal(Path.Combine(Save, "Backups", "Taiga"), SC2SaveLayout.BackupsDirectory(Save, "Taiga"));
    }

    [Theory]
    [InlineData("Taiga.sc2mapshare", ".sc2mapshare", "Taiga")]
    [InlineData("Taiga.SC2MAPSHARE", ".sc2mapshare", "Taiga")]
    [InlineData("Taiga.sc2terrain.json", ".sc2terrain.json", "Taiga")]
    [InlineData("Taiga.sc2mapshare.rar", ".sc2mapshare", null)]
    [InlineData(".sc2mapshare", ".sc2mapshare", null)]
    [InlineData("Taiga", ".sc2mapshare", null)]
    public void StemOf_ReturnsTheMapNameOrNull(string name, string suffix, string expected)
    {
        Assert.Equal(expected, SC2SaveLayout.StemOf(name, suffix));
    }

    [Fact]
    public void MergeMapNames_ListsAMapOnce_AndSortsThem()
    {
        var merged = SC2SaveLayout.MergeMapNames(new[] { "Taiga", "alpha" }, new[] { "TAIGA", "Canis" });

        Assert.Equal(new[] { "alpha", "Canis", "Taiga" }, merged);
    }

    [Fact]
    public void MergeMapNames_KeepsTheNameOfTheMapFolder_WhenBothLayoutsHoldTheMap()
    {
        var merged = SC2SaveLayout.MergeMapNames(new[] { "Taiga" }, new[] { "taiga" });

        Assert.Equal(new[] { "Taiga" }, merged);
    }

    [Fact]
    public void MergeMapNames_ToleratesNothingAtAll()
    {
        Assert.Empty(SC2SaveLayout.MergeMapNames(null, null));
        Assert.Equal(new[] { "A" }, SC2SaveLayout.MergeMapNames(null, new[] { "A", "", null }));
    }

    [Fact]
    public void LoadOrder_TriesTheMapFolderBeforeTheEarlierLayout()
    {
        var order = SC2SaveLayout.LoadOrder(true, true, true, true);

        Assert.Equal(new[]
        {
            SC2SaveLayout.Source.ShareManifest, SC2SaveLayout.Source.ShareJson,
            SC2SaveLayout.Source.LegacyManifest, SC2SaveLayout.Source.LegacyJson,
        }, order);
    }

    [Fact]
    public void LoadOrder_LeavesOutWhatIsNotThere()
    {
        Assert.Equal(new[] { SC2SaveLayout.Source.ShareJson, SC2SaveLayout.Source.LegacyJson },
            SC2SaveLayout.LoadOrder(false, true, false, true));
        Assert.Empty(SC2SaveLayout.LoadOrder(false, false, false, false));
    }
}

public class LayeredBlobStoreTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2layered_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static string PutText(ISC2BlobStore store, string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return store.Put(stream).ContentHash;
    }

    private static string ReadText(ISC2BlobStore store, string hash)
    {
        using var stream = store.OpenRead(hash);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Put_GoesToTheWorkingStoreOnly()
    {
        var working = NewStore(out var workingRoot);
        var fallback = NewStore(out var fallbackRoot);
        try
        {
            var layered = new SC2LayeredBlobStore(working, () => new ISC2BlobStore[] { fallback });

            var hash = PutText(layered, "new blob");

            Assert.True(working.Contains(hash));
            Assert.False(fallback.Contains(hash));
        }
        finally { Directory.Delete(workingRoot, true); Directory.Delete(fallbackRoot, true); }
    }

    [Fact]
    public void Read_FindsABlobThatOnlyAFallbackHolds()
    {
        var working = NewStore(out var workingRoot);
        var fallback = NewStore(out var fallbackRoot);
        try
        {
            var hash = PutText(fallback, "lives in a map folder");
            var layered = new SC2LayeredBlobStore(working, () => new ISC2BlobStore[] { fallback });

            Assert.True(layered.Contains(hash));
            Assert.Equal("lives in a map folder", ReadText(layered, hash));
        }
        finally { Directory.Delete(workingRoot, true); Directory.Delete(fallbackRoot, true); }
    }

    [Fact]
    public void Read_PrefersTheWorkingStore()
    {
        var working = NewStore(out var workingRoot);
        var fallback = NewStore(out var fallbackRoot);
        try
        {
            var hash = PutText(working, "same bytes");
            PutText(fallback, "same bytes");
            var layered = new SC2LayeredBlobStore(working, () => new ISC2BlobStore[] { fallback });

            Assert.Equal("same bytes", ReadText(layered, hash));
        }
        finally { Directory.Delete(workingRoot, true); Directory.Delete(fallbackRoot, true); }
    }

    [Fact]
    public void Fallbacks_AreAskedForOnEveryRead_SoAMapOpenedLaterIsFound()
    {
        var working = NewStore(out var workingRoot);
        var late = NewStore(out var lateRoot);
        try
        {
            var fallbacks = new System.Collections.Generic.List<ISC2BlobStore>();
            var layered = new SC2LayeredBlobStore(working, () => fallbacks);
            var hash = PutText(late, "opened after the session started");
            Assert.False(layered.Contains(hash));

            fallbacks.Add(late);

            Assert.True(layered.Contains(hash));
        }
        finally { Directory.Delete(workingRoot, true); Directory.Delete(lateRoot, true); }
    }

    [Fact]
    public void Read_OfABlobNoStoreHolds_RaisesBlobNotFound()
    {
        var working = NewStore(out var workingRoot);
        try
        {
            var layered = new SC2LayeredBlobStore(working, () => System.Array.Empty<ISC2BlobStore>());

            Assert.Throws<FileNotFoundException>(() => layered.OpenRead(new string('a', 64)));
        }
        finally { Directory.Delete(workingRoot, true); }
    }

    [Fact]
    public void Collecting_OnlyTouchesTheWorkingStore()
    {
        var working = NewStore(out var workingRoot);
        var fallback = NewStore(out var fallbackRoot);
        try
        {
            var inWorking = PutText(working, "cache only");
            var inFallback = PutText(fallback, "map folder");
            var layered = new SC2LayeredBlobStore(working, () => new ISC2BlobStore[] { fallback });

            Assert.Equal(new[] { inWorking }, layered.EnumerateHashes().ToArray());
            Assert.False(layered.TryDelete(inFallback));
            Assert.True(fallback.Contains(inFallback));
            Assert.True(layered.TryDelete(inWorking));
        }
        finally { Directory.Delete(workingRoot, true); Directory.Delete(fallbackRoot, true); }
    }
}
