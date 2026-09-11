using System;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class TerrainCommandTests
{
    private static SC2MapDocument DocumentWithMaterial(string id, string name)
    {
        var document = new SC2MapDocument();
        document.Terrain.Materials.Add(new SC2MaterialDocument { Id = id, Name = name });
        return document;
    }

    [Fact]
    public void Execute_RenamesTheTargetedMaterialOnly()
    {
        var document = DocumentWithMaterial("mat-1", "Grass");
        document.Terrain.Materials.Add(new SC2MaterialDocument { Id = "mat-2", Name = "Rock" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialNameCommand("mat-1", "GrassLush"));

        Assert.Equal("GrassLush", document.Terrain.FindMaterial("mat-1").Name);
        Assert.Equal("Rock", document.Terrain.FindMaterial("mat-2").Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = DocumentWithMaterial("mat-1", "Grass");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialNameCommand("mat-1", "GrassLush"));
        history.Undo(document);

        Assert.Equal("Grass", document.Terrain.FindMaterial("mat-1").Name);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewName()
    {
        var document = DocumentWithMaterial("mat-1", "Grass");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialNameCommand("mat-1", "GrassLush"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("GrassLush", document.Terrain.FindMaterial("mat-1").Name);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithTerrainMaterialsDirtyFlagAndDomain()
    {
        var document = DocumentWithMaterial("mat-1", "Grass");
        var command = new SC2SetMaterialNameCommand("mat-1", "GrassLush");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.TerrainMaterials, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Terrain, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Rename Material 'mat-1'", changeSet.CommandName);
    }

    [Fact]
    public void Do_ThrowsWhenMaterialIdIsNotFound()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetMaterialNameCommand("does-not-exist", "New");

        Assert.Throws<InvalidOperationException>(() => command.Do(document));
    }

    [Fact]
    public void Constructor_ThrowsOnNullMaterialId()
    {
        Assert.Throws<ArgumentNullException>(() => new SC2SetMaterialNameCommand(null, "New"));
    }

    [Fact]
    public void Execute_NullNameIsTreatedAsEmptyString()
    {
        var document = DocumentWithMaterial("mat-1", "Grass");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialNameCommand("mat-1", null));

        Assert.Equal("", document.Terrain.FindMaterial("mat-1").Name);
    }
}

public class MaterialPairNameCommandTests
{
    private static SC2MapDocument DocumentWithPair(string id, string name)
    {
        var document = new SC2MapDocument();
        document.Terrain.MaterialPairs.Add(new SC2MaterialPairDocument { Id = id, Name = name });
        return document;
    }

    [Fact]
    public void Execute_RenamesTheTargetedPairOnly()
    {
        var document = DocumentWithPair("pair-1", "GrassRock");
        document.Terrain.MaterialPairs.Add(new SC2MaterialPairDocument { Id = "pair-2", Name = "SandRock" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairNameCommand("pair-1", "GrassRockBlend"));

        Assert.Equal("GrassRockBlend", document.Terrain.FindPair("pair-1").Name);
        Assert.Equal("SandRock", document.Terrain.FindPair("pair-2").Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = DocumentWithPair("pair-1", "GrassRock");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairNameCommand("pair-1", "GrassRockBlend"));
        history.Undo(document);

        Assert.Equal("GrassRock", document.Terrain.FindPair("pair-1").Name);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewName()
    {
        var document = DocumentWithPair("pair-1", "GrassRock");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairNameCommand("pair-1", "GrassRockBlend"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("GrassRockBlend", document.Terrain.FindPair("pair-1").Name);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithTerrainMaterialsDirtyFlagAndDomain()
    {
        var document = DocumentWithPair("pair-1", "GrassRock");
        var command = new SC2SetMaterialPairNameCommand("pair-1", "GrassRockBlend");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.TerrainMaterials, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Terrain, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Rename Pair 'pair-1'", changeSet.CommandName);
    }

    [Fact]
    public void Do_ThrowsWhenPairIdIsNotFound()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetMaterialPairNameCommand("does-not-exist", "New");

        Assert.Throws<InvalidOperationException>(() => command.Do(document));
    }

    [Fact]
    public void Constructor_ThrowsOnNullPairId()
    {
        Assert.Throws<ArgumentNullException>(() => new SC2SetMaterialPairNameCommand(null, "New"));
    }

    [Fact]
    public void Execute_NullNameIsTreatedAsEmptyString()
    {
        var document = DocumentWithPair("pair-1", "GrassRock");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairNameCommand("pair-1", null));

        Assert.Equal("", document.Terrain.FindPair("pair-1").Name);
    }
}

public class TerrainValidatorTests
{
    [Fact]
    public void Validate_ErrorsOnDuplicateMaterialId()
    {
        var doc = new SC2TerrainDocument();
        doc.Materials.Add(new SC2MaterialDocument { Id = "mat-1", Name = "A" });
        doc.Materials.Add(new SC2MaterialDocument { Id = "mat-1", Name = "B" });

        var report = SC2TerrainValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "TER001" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_ErrorsOnPairReferencingMissingMaterial()
    {
        var doc = new SC2TerrainDocument();
        doc.Materials.Add(new SC2MaterialDocument { Id = "mat-1", Name = "A" });
        doc.MaterialPairs.Add(new SC2MaterialPairDocument { Id = "pair-1", Material1Id = "mat-1", Material2Id = "mat-missing" });

        var report = SC2TerrainValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "TER003" && m.Severity == SC2ValidationSeverity.Error);
        Assert.DoesNotContain(report.Messages, m => m.Code == "TER002");
    }

    [Fact]
    public void Validate_NoErrorsForWellFormedDocument()
    {
        var doc = new SC2TerrainDocument();
        doc.Materials.Add(new SC2MaterialDocument { Id = "mat-1", Name = "A" });
        doc.Materials.Add(new SC2MaterialDocument { Id = "mat-2", Name = "B" });
        doc.MaterialPairs.Add(new SC2MaterialPairDocument { Id = "pair-1", Material1Id = "mat-1", Material2Id = "mat-2" });

        var report = SC2TerrainValidator.Validate(doc);

        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2TerrainValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
