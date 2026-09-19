using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>
/// Coverage for SC2VanillaArchiveClassifier — decides whether a texture
/// served by the configured game install's archives is "vanilla" (shipped by
/// the base game: textures.scd / z_dlc1.scd / z_dlc1_map_shared.scd) and so
/// can be referenced by its real game path instead of being copied into an
/// exported map. Driven through the explicit-reader overloads with fake
/// in-memory readers, since the registry-based ones read a PlayerPrefs-backed
/// static.
/// </summary>
public class VanillaArchiveClassifierTests
{
    private sealed class FakeReader : ISC2SourceContainerReader
    {
        private readonly List<SC2SourceEntryInfo> _entries = new();
        public FakeReader(string sourcePath, params string[] entryPaths)
        {
            SourcePath = sourcePath;
            foreach (var p in entryPaths) _entries.Add(new SC2SourceEntryInfo { NormalizedPath = p });
        }
        public SC2SourceContainerKind Kind => SC2SourceContainerKind.ZipArchive;
        public string SourcePath { get; }
        public IReadOnlyList<SC2SourceEntryInfo> Entries => _entries;
        public Stream OpenEntry(string normalizedPath) => new MemoryStream();
        public void Dispose() { }
    }

    private static ISC2SourceContainerReader Install() => new SC2MultiSourceContainerReader(new[]
    {
        new FakeReader(@"C:\game\gamedata\textures.scd", "textures/Terrain/sandstone_001a_d.win.dds", "textures/Terrain/plain_d.dds"),
        new FakeReader(@"C:\game\gamedata\z_dlc1.scd", "textures/Terrain/dlc_rock_d.win.dds"),
        new FakeReader(@"C:\game\gamedata\z_dlc1_map_shared.scd", "textures/Terrain/dlc_shared_n.win.dds"),
        new FakeReader(@"C:\game\gamedata\SCS maps v3.scd", "textures/Terrain/community_d.win.dds"),
    });

    [Theory]
    [InlineData("textures", true)]
    [InlineData("TEXTURES", true)]
    [InlineData("z_dlc1", true)]
    [InlineData("z_dlc1_map_shared", true)]
    [InlineData("maps", false)]
    [InlineData("textures2", false)]
    [InlineData("z_dlc2", false)]
    [InlineData("SCS maps v3", false)]
    [InlineData("", false)]
    public void ArchiveStem_ClassifiedAgainstTheThreeVanillaArchives(string stem, bool expected) =>
        Assert.Equal(expected, SC2VanillaArchiveClassifier.IsVanillaArchiveStem(stem));

    [Fact]
    public void EntryOwnedByAVanillaArchive_IsVanilla_EvenWhenSeveralVanillaArchivesExist()
    {
        var reader = Install();
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(reader, "textures/Terrain/sandstone_001a_d.win.dds", out var owner));
        Assert.EndsWith("textures.scd", owner);
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(reader, "textures/Terrain/dlc_rock_d.win.dds", out _));
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(reader, "textures/Terrain/dlc_shared_n.win.dds", out _));
    }

    [Fact]
    public void EntryOwnedByAnyOtherArchive_IsNotVanilla()
    {
        var reader = Install();
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(reader, "textures/Terrain/community_d.win.dds", out var owner));
        Assert.EndsWith("SCS maps v3.scd", owner);
    }

    [Fact]
    public void UnknownEntryOrMissingReader_IsNotVanilla_SoTheCallerFallsBackToCopying()
    {
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(Install(), "textures/Terrain/nope_d.win.dds", out _));
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(null, "textures/x.dds", out _));
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(Install(), "", out _));
    }

    [Fact]
    public void SingleArchiveReader_UsesItsOwnSourcePath()
    {
        var vanilla = new FakeReader(@"C:\game\gamedata\textures.scd", "textures/a_d.win.dds");
        var other = new FakeReader(@"C:\maps\mypack.scd", "textures/a_d.win.dds");
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(vanilla, "textures/a_d.win.dds", out _));
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaOwnedEntry(other, "textures/a_d.win.dds", out _));
    }

    [Fact]
    public void ShouldSkipCopy_OnlyForVanillaEntries_AndNeverWhenForceExportIsChecked()
    {
        var reader = Install();
        const string vanilla = "textures/Terrain/sandstone_001a_d.win.dds";
        const string community = "textures/Terrain/community_d.win.dds";

        Assert.True(SC2VanillaArchiveClassifier.ShouldSkipCopy(reader, vanilla, forceExport: false));
        Assert.False(SC2VanillaArchiveClassifier.ShouldSkipCopy(reader, vanilla, forceExport: true));
        Assert.False(SC2VanillaArchiveClassifier.ShouldSkipCopy(reader, community, forceExport: false));
        Assert.False(SC2VanillaArchiveClassifier.ShouldSkipCopy(reader, community, forceExport: true));
    }

    [Theory]
    [InlineData("textures/Terrain/CA_C03/x_d.win.dds", "/textures/Terrain/CA_C03/x_d.dds")]
    [InlineData("/textures/Terrain/x_n.win.dds", "/textures/Terrain/x_n.dds")]
    [InlineData("textures\\Terrain\\x_s.WIN.dds", "/textures/Terrain/x_s.dds")]
    [InlineData("textures/Terrain/plain_d.dds", "/textures/Terrain/plain_d.dds")]
    [InlineData("textures/Terrain/Water/foam_001.win.dds", "/textures/Terrain/Water/foam_001.dds")]
    [InlineData("", "")]
    public void ToGamePath_StripsTheWinInfixAndAddsALeadingSlash(string entry, string expected) =>
        Assert.Equal(expected, SC2VanillaArchiveClassifier.ToGamePath(entry));

    [Fact]
    public void GamePathLookup_FindsTheArchiveEntryWithOrWithoutTheWinInfix()
    {
        var reader = Install();
        Assert.True(SC2VanillaArchiveClassifier.TryResolveGamePathEntry(reader, "/textures/Terrain/sandstone_001a_d.dds", out var entry, out var owner));
        Assert.Equal("textures/Terrain/sandstone_001a_d.win.dds", entry);
        Assert.EndsWith("textures.scd", owner);

        Assert.True(SC2VanillaArchiveClassifier.TryResolveGamePathEntry(reader, "/textures/Terrain/plain_d.dds", out entry, out _));
        Assert.Equal("textures/Terrain/plain_d.dds", entry);

        Assert.False(SC2VanillaArchiveClassifier.TryResolveGamePathEntry(reader, "/textures/Terrain/missing_d.dds", out _, out _));
    }

    [Fact]
    public void GamePathVanillaCheck_UsesTheOwningArchive()
    {
        var reader = Install();
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaGamePath(reader, "/textures/Terrain/sandstone_001a_d.dds"));
        Assert.True(SC2VanillaArchiveClassifier.IsVanillaGamePath(reader, "textures/Terrain/dlc_rock_d.dds"));
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaGamePath(reader, "/textures/Terrain/community_d.dds"));
        Assert.False(SC2VanillaArchiveClassifier.IsVanillaGamePath(reader, "/textures/Terrain/missing_d.dds"));
    }

    [Fact]
    public void ArchiveTag_RoundTripsToAGamePath_AndAStaleTagIsRejected()
    {
        const string tag = "archive:textures/Terrain/sandstone_001a_d.win.dds";
        Assert.Equal("textures/Terrain/sandstone_001a_d.win.dds", SC2VanillaArchiveClassifier.EntryPathFromTag(tag));
        Assert.Null(SC2VanillaArchiveClassifier.EntryPathFromTag("file:C:/x.dds"));
        Assert.Null(SC2VanillaArchiveClassifier.EntryPathFromTag(""));

        Assert.True(SC2VanillaArchiveClassifier.TagMatchesGamePath(tag, "/textures/Terrain/sandstone_001a_d.dds"));
        Assert.True(SC2VanillaArchiveClassifier.TagMatchesGamePath(tag, "textures/terrain/SANDSTONE_001A_D.dds"));
        // The slot's path was changed after the pick — the remembered tag no longer describes it.
        Assert.False(SC2VanillaArchiveClassifier.TagMatchesGamePath(tag, "/textures/Terrain/other_d.dds"));
        Assert.False(SC2VanillaArchiveClassifier.TagMatchesGamePath("", "/textures/Terrain/sandstone_001a_d.dds"));
    }

    [Fact]
    public void EnvironmentTextureExport_DefaultsToUncheckedAndSurvivesCloneAndUndoSemantics()
    {
        var export = new SC2EnvironmentTextureExport();
        Assert.False(export.GetForce(SC2EnvironmentTextureSlot.SkyMap));
        Assert.Equal("", export.GetTag(SC2EnvironmentTextureSlot.SkyMap));

        export.Set(SC2EnvironmentTextureSlot.SkyMap, "archive:textures/environment/sky.win.dds", true);
        var clone = export.Clone();
        Assert.True(clone.GetForce(SC2EnvironmentTextureSlot.SkyMap));
        Assert.Equal("archive:textures/environment/sky.win.dds", clone.GetTag(SC2EnvironmentTextureSlot.SkyMap));
        Assert.True(export.ContentEquals(clone));

        clone.Set(SC2EnvironmentTextureSlot.SkyMap, "", false);
        Assert.False(export.ContentEquals(clone));
        Assert.True(export.GetForce(SC2EnvironmentTextureSlot.SkyMap));
        // Other slots untouched.
        Assert.False(clone.GetForce(SC2EnvironmentTextureSlot.EnvironmentMap));
    }

    [Fact]
    public void SetEnvironmentTextureExportCommand_DoAndUndoRestoreTheSlot()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetEnvironmentTextureExportCommand(SC2EnvironmentTextureSlot.EnvironmentMap, "archive:textures/environment/env.win.dds", true);

        command.Do(document);
        Assert.True(document.Environment.TextureExport.GetForce(SC2EnvironmentTextureSlot.EnvironmentMap));
        Assert.Equal("archive:textures/environment/env.win.dds", document.Environment.TextureExport.GetTag(SC2EnvironmentTextureSlot.EnvironmentMap));

        command.Undo(document);
        Assert.False(document.Environment.TextureExport.GetForce(SC2EnvironmentTextureSlot.EnvironmentMap));
        Assert.Equal("", document.Environment.TextureExport.GetTag(SC2EnvironmentTextureSlot.EnvironmentMap));
    }
}
