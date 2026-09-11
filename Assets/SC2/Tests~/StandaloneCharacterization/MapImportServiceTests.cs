using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BdfIO.Core;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class MapImportServiceTests
{
    private static SC2MapImportService NewService(out string blobRoot)
    {
        blobRoot = Directory.CreateTempSubdirectory("sc2importblobs_").FullName;
        return new SC2MapImportService(new SC2SourceContainerReaderFactory(), new SC2DirectoryBlobStore(blobRoot));
    }

    private static void Cleanup(string zipOrDirPath, string blobRoot)
    {
        if (File.Exists(zipOrDirPath)) File.Delete(zipOrDirPath);
        if (Directory.Exists(zipOrDirPath)) Directory.Delete(zipOrDirPath, true);
        Directory.Delete(blobRoot, true);
    }

    [Fact]
    public async Task Probe_FindsAllScenarioCandidatesInMultiMapContainer()
    {
        var service = NewService(out var blobRoot);
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);

            Assert.Equal(3, probe.ScenarioCandidates.Count);

            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);
            Assert.True(valid.CanImport);
            Assert.Equal("Test Map One", valid.DisplayName);
            Assert.Empty(valid.Messages);

            var missingHfield = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.MissingHfieldStem);
            Assert.False(missingHfield.CanImport);
            Assert.Contains(missingHfield.Messages, m => m.Code == "missing-hfield-bdf");

            var compiled = probe.ScenarioCandidates.Single(c => c.ScenarioEntryPath.Contains(ImportServiceTestFixtures.CompiledStem));
            Assert.False(compiled.CanImport);
            Assert.Contains(compiled.Messages, m => m.Code == "compiled-lua-unsupported");
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task Probe_NoScenarioLuaAnywhere_ReportsHardErrorAndEmptyCandidates()
    {
        var service = NewService(out var blobRoot);
        var entries = new System.Collections.Generic.Dictionary<string, byte[]> { ["readme.txt"] = new byte[] { 1 } };
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(entries);
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);

            Assert.Empty(probe.ScenarioCandidates);
            Assert.True(probe.Diagnostics.HasErrors);
            Assert.Contains(probe.Diagnostics.Messages, m => m.Code == "no-scenario-lua");
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task ProbeThenImport_ValidCandidate_PopulatesDocumentFields()
    {
        var service = NewService(out var blobRoot);
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);
            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);

            var result = await service.ImportSelectedScenarioAsync(probe, valid.ScenarioEntryPath, null, null, CancellationToken.None);
            var doc = result.Document;

            Assert.Equal("Test Map One", doc.Identity.DisplayName);
            Assert.Equal(ImportServiceTestFixtures.ValidStem, doc.Identity.MapStem);

            Assert.Equal(SC2MapSourceKind.ScdArchiveFile, doc.Source.Kind);
            Assert.Equal(SC2SourceContainerKind.ZipArchive, doc.Source.ContainerKind);
            Assert.Equal(zipPath, doc.Source.OriginalPath);
            Assert.True(doc.Source.OriginalStillAvailable);

            Assert.Equal(valid.ScenarioEntryPath, doc.EntryPoint.ScenarioEntryPath);
            Assert.Equal($"maps/{ImportServiceTestFixtures.ValidStem}", doc.EntryPoint.BdfDirectory);
            Assert.Equal(ImportServiceTestFixtures.ValidStem, doc.EntryPoint.BdfStem);
            Assert.Equal(BdfPlatform.Pc, doc.EntryPoint.Platform);
            Assert.Equal("scenario.lua", doc.EntryPoint.ResolutionMethod);
            Assert.False(string.IsNullOrEmpty(doc.EntryPoint.ScenarioEntrySha256));

            Assert.Equal(valid.ScenarioEntryPath, doc.Archive.SelectedScenarioEntryPath);

            var terrainEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.terrain.win.bdf");
            Assert.NotNull(terrainEntry);
            Assert.Equal(SC2ArchiveEntryRole.TerrainBdf, terrainEntry.Role);
            Assert.False(terrainEntry.OriginalContent.IsEmpty);

            var hfieldEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.hfield.win.bdf");
            Assert.Equal(SC2ArchiveEntryRole.HfieldBdf, hfieldEntry.Role);

            var scmapEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.scmap");
            Assert.Equal(SC2ArchiveEntryRole.CustomAsset, scmapEntry.Role);

            var saveEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}_save.lua");
            Assert.Equal(SC2ArchiveEntryRole.SaveLua, saveEntry.Role);

            var scriptEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}_script.lua");
            Assert.Equal(SC2ArchiveEntryRole.ScriptLua, scriptEntry.Role);

            var textureEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.ValidStem}/lighting/mask01.dds");
            Assert.Equal(SC2ArchiveEntryRole.MapTexture, textureEntry.Role);

            var foreignEntry = doc.Archive.Find($"maps/{ImportServiceTestFixtures.MissingHfieldStem}/{ImportServiceTestFixtures.MissingHfieldStem}.terrain.win.bdf");
            Assert.Equal(SC2ArchiveEntryRole.ForeignMapEntry, foreignEntry.Role);
            Assert.True(foreignEntry.OriginalContent.IsEmpty);
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task Import_BlobStoreActuallyContainsSelectedMapBytes()
    {
        var service = NewService(out var blobRoot);
        var fixtureEntries = ImportServiceTestFixtures.BuildFixtureEntries();
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(fixtureEntries);
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);
            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);
            var result = await service.ImportSelectedScenarioAsync(probe, valid.ScenarioEntryPath, null, null, CancellationToken.None);

            var terrainEntry = result.Document.Archive.Find(
                $"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.terrain.win.bdf");

            var blobStore = new SC2DirectoryBlobStore(blobRoot);
            using var blobStream = blobStore.OpenRead(terrainEntry.OriginalContent.ContentHash);
            using var ms = new MemoryStream();
            blobStream.CopyTo(ms);

            var expectedBytes = fixtureEntries[$"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.terrain.win.bdf"];
            Assert.Equal(expectedBytes, ms.ToArray());
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task Import_RejectsScenarioPathNotInProbeResult()
    {
        var service = NewService(out var blobRoot);
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ImportSelectedScenarioAsync(probe, "maps/does_not_exist/x_scenario.lua", null, null, CancellationToken.None));
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task Import_RejectsCandidateThatCannotImport()
    {
        var service = NewService(out var blobRoot);
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);
            var missingHfield = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.MissingHfieldStem);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ImportSelectedScenarioAsync(probe, missingHfield.ScenarioEntryPath, null, null, CancellationToken.None));
            Assert.Contains("hfield", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task Import_WarnsWhenSourceChangedSinceProbe_ButStillImports()
    {
        var service = NewService(out var blobRoot);
        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);
            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);

            ImportServiceTestFixtures.AddEntryToZip(zipPath, "extra_file_added_after_probe.txt", new byte[] { 1, 2, 3 });

            var result = await service.ImportSelectedScenarioAsync(probe, valid.ScenarioEntryPath, null, null, CancellationToken.None);

            Assert.Contains(result.Diagnostics.Messages, m => m.Code == "source-changed-since-probe");
            Assert.NotNull(result.Document.Archive.Find(
                $"maps/{ImportServiceTestFixtures.ValidStem}/{ImportServiceTestFixtures.ValidStem}.terrain.win.bdf"));
        }
        finally
        {
            Cleanup(zipPath, blobRoot);
        }
    }

    [Fact]
    public async Task ProbeThenImport_WorksIdenticallyAgainstDirectorySource()
    {
        var service = NewService(out var blobRoot);
        var dirPath = ImportServiceTestFixtures.WriteDirectoryFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        // Directory container probing warns (not hard-fails) when the
        // directory name doesn't end in ".scd" -- rename to satisfy the
        // vanilla naming convention and avoid that unrelated warning noise.
        var scdDirPath = dirPath + ".scd";
        Directory.Move(dirPath, scdDirPath);
        try
        {
            var probe = await service.ProbeScdAsync(scdDirPath, null, CancellationToken.None);
            Assert.Equal(SC2SourceContainerKind.Directory, probe.ContainerKind);
            Assert.DoesNotContain(probe.Diagnostics.Messages, m => m.Code == "source-not-scd-named");

            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);
            var result = await service.ImportSelectedScenarioAsync(probe, valid.ScenarioEntryPath, null, null, CancellationToken.None);

            Assert.Equal(SC2MapSourceKind.ScdDirectory, result.Document.Source.Kind);
            Assert.Equal(SC2SourceContainerKind.Directory, result.Document.Source.ContainerKind);
            Assert.Equal(BdfPlatform.Pc, result.Document.EntryPoint.Platform);
        }
        finally
        {
            Cleanup(scdDirPath, blobRoot);
        }
    }

    [Fact]
    public async Task Probe_WarnsWhenDirectorySourceDoesNotEndInScd()
    {
        var service = NewService(out var blobRoot);
        var dirPath = ImportServiceTestFixtures.WriteDirectoryFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(dirPath, null, CancellationToken.None);
            Assert.Contains(probe.Diagnostics.Messages, m => m.Code == "source-not-scd-named");
        }
        finally
        {
            Cleanup(dirPath, blobRoot);
        }
    }
}
