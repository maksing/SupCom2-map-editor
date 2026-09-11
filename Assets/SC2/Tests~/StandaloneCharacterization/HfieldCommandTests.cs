using System;
using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class HfieldCommandTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2hfieldcmdblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void Execute_ReplacesTerrainTypesGridBytes()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            document.Hfield.TerrainTypesUInt8 = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetHfieldTerrainTypesCommand(new byte[] { 4, 5, 6 }, store));

            using var stream = store.OpenRead(document.Hfield.TerrainTypesUInt8.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 4, 5, 6 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Undo_RestoresThePreviousGridReferenceWithoutRewritingTheStore()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            document.Hfield.TerrainTypesUInt8 = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));
            var originalRef = document.Hfield.TerrainTypesUInt8;
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetHfieldTerrainTypesCommand(new byte[] { 4, 5, 6 }, store));
            history.Undo(document);

            Assert.Equal(originalRef.ContentHash, document.Hfield.TerrainTypesUInt8.ContentHash);
            using var stream = store.OpenRead(document.Hfield.TerrainTypesUInt8.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 1, 2, 3 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Redo_ReappliesTheNewGrid()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetHfieldTerrainTypesCommand(new byte[] { 4, 5, 6 }, store));
            history.Undo(document);
            history.Redo(document);

            using var stream = store.OpenRead(document.Hfield.TerrainTypesUInt8.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 4, 5, 6 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Do_ReturnsChangeSetWithTerrainTypesDirtyFlagAndHfieldDomain()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var command = new SC2SetHfieldTerrainTypesCommand(new byte[] { 1 }, store);

            var changeSet = command.Do(document);

            Assert.Equal(SC2DirtyFlags.HfieldTerrainTypes, changeSet.DirectDirtyFlags);
            Assert.Contains(SC2DomainId.Hfield, changeSet.ChangedDomains);
            Assert.Empty(changeSet.InvalidatedDomains);
            Assert.Equal("Set Hfield.TerrainTypes", changeSet.CommandName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => new SC2SetHfieldTerrainTypesCommand(null, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetHfieldTerrainTypesCommand(new byte[] { 1 }, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class HfieldValidatorTests
{
    [Fact]
    public void Validate_ErrorsOnEmptyTerrainTypesGrid()
    {
        var doc = new SC2HfieldDocument { TerrainTypesUInt8 = SC2BlobReference.Empty };
        var report = SC2HfieldValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "HFD001" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_NoErrorWhenTerrainTypesGridIsPopulated()
    {
        var root = Directory.CreateTempSubdirectory("sc2hfieldvalidateblobs_").FullName;
        try
        {
            var store = new SC2DirectoryBlobStore(root);
            var doc = new SC2HfieldDocument { TerrainTypesUInt8 = store.Put(new MemoryStream(new byte[] { 1 })) };

            var report = SC2HfieldValidator.Validate(doc);

            Assert.Empty(report.Messages);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2HfieldValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
