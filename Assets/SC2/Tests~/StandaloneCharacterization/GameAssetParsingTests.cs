using System;
using System.IO;
using Xunit;

public sealed class GameAssetParsingTests
{
    [Fact]
    public void BlueprintLoader_reads_real_lua51_bytecode_when_fixture_is_provided()
    {
        string fixture = Environment.GetEnvironmentVariable("SC2_BYTECODE_BLUEPRINT_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture)) return;

        var bytes = File.ReadAllBytes(fixture);
        Assert.True(SC2BlueprintLoader.IsLua51Bytecode(bytes));
        var blueprint = SC2BlueprintLoader.ParseBytes(bytes, fixture);
        Assert.True(blueprint.Declaration == "UnitBlueprint" || blueprint.Declaration == "PropBlueprint");
        Assert.NotNull(blueprint.Table.GetTable("Display"));
    }

    [Fact]
    public void BrowserDiscovery_resolves_all_models_and_textures_when_game_data_root_is_provided()
    {
        string root = Environment.GetEnvironmentVariable("SC2_GAME_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        string configuredBlueprintRoots = Environment.GetEnvironmentVariable("SC2_BLUEPRINT_ROOTS");
        var blueprintRoots = string.IsNullOrWhiteSpace(configuredBlueprintRoots)
            ? new[] { root }
            : configuredBlueprintRoots.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var result = SC2GameAssetDiscovery.Build(blueprintRoots, new[] { root });
        Assert.NotEmpty(result.Entries);
        Assert.Empty(result.Catalog.Warnings);

        AssertVanillaPropTextureWhenPresent(result, root, "props/Buildings/Building01/Building01_prop.bp", "Building01_Diffuse.win.dds");
        AssertVanillaPropTextureWhenPresent(result, root, "props/Barges/Barge01/Barge01_prop.bp", "Barge01_Diffuse.win.dds");
        AssertVanillaPropTextureWhenPresent(result, root, "props/Trees/Acacia_001/Acacia_001_prop.bp", "Acacia_001_B_Atlas_D.win.dds");

        // A partial extracted game archive is useful for browser development:
        // it should load and expose precise missing-resource diagnostics. Set the
        // strict flag when a complete game-data fixture is available.
        if (string.Equals(Environment.GetEnvironmentVariable("SC2_REQUIRE_COMPLETE_GAME_ASSETS"), "1", StringComparison.Ordinal))
        {
            string summary = "Assets=" + result.Entries.Count + ", ready=" + result.ReadyCount + ", issues=" + result.IssueCount;
            string details = string.Join(Environment.NewLine, result.Entries
                .Where(entry => entry.Issues.Count != 0)
                .Take(20)
                .Select(entry => entry.Asset.SourcePath + ": " + string.Join("; ", entry.Issues.Select(issue => issue.Message))));
            Assert.True(result.IssueCount == 0, summary + Environment.NewLine + details);
        }
    }

    [Fact]
    public void BrowserDiscovery_uses_mesh_name_and_win_dds_variant_from_blueprint()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { LODCutoff = 100, MeshName = '/props/shared/Shared_lod0.scm', MaterialSets = { { Materials = { {
                DiffuseName = '/textures/Shared_Diffuse.dds', ShaderMacros = 'diffuse_lighting'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            string blueprintDirectory = Path.Combine(root, "props", "entry");
            Directory.CreateDirectory(blueprintDirectory);
            Directory.CreateDirectory(Path.Combine(root, "props", "shared"));
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            File.WriteAllText(Path.Combine(blueprintDirectory, "PXX0001_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(root, "props", "shared", "Shared_lod0.win.scm"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(root, "textures", "Shared_Diffuse.win.dds"), Array.Empty<byte>());

            var result = SC2GameAssetDiscovery.Build(new[] { root }, new[] { root });
            var asset = Assert.Single(result.Entries);
            Assert.Empty(asset.Issues);
            Assert.EndsWith("Shared_lod0.win.scm", Assert.Single(asset.Models), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Shared_Diffuse.win.dds", Assert.Single(asset.Textures).ResolvedPath, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BlueprintParser_extracts_scale_lods_and_all_material_textures()
    {
        const string source = @"UnitBlueprint {
            Display = {
                DisplayName = 'Test unit',
                UniformScale = 0.2,
                Mesh = { LODs = { { LODCutoff = 100, MaterialSets = { { Name = 'Base', Materials = { {
                    DiffuseName = '/units/test/diffuse.dds', NormalName = '/units/test/normal.dds', MaskName = '/units/test/mask.dds',
                    EffectName = '/effects/not_a_material_texture.dds', RampName = '/ramps/not_a_material_texture.dds', ShaderMacros = 'diffuse_lighting'
                } } } } } } }
            }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string bp = Path.Combine(root, "UXX0001_unit.bp"); File.WriteAllText(bp, source);
            File.WriteAllBytes(Path.Combine(root, "UXX0001_lod0.win.scm"), Array.Empty<byte>());
            var result = SC2GameAssetCatalog.Build(new[] { root }, new[] { root });
            var asset = Assert.Single(result.Assets);
            Assert.Equal("UXX0001", asset.Id); Assert.Equal("UnitBlueprint", asset.Kind); Assert.Equal(0.2f, asset.UniformScale); Assert.Equal("Test unit", asset.DisplayName);
            var lod = Assert.Single(asset.Lods); Assert.Equal(100f, lod.Cutoff); Assert.Single(lod.ModelPaths);
            var material = Assert.Single(Assert.Single(lod.MaterialSets).Materials);
            Assert.Equal("/units/test/diffuse.dds", material.Textures["DiffuseName"]);
            Assert.Equal("/units/test/normal.dds", material.Textures["NormalName"]);
            Assert.Equal("/units/test/mask.dds", material.Textures["MaskName"]);
            Assert.False(material.Textures.ContainsKey("EffectName"));
            Assert.False(material.Textures.ContainsKey("RampName"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_resolves_standard_sibling_fallbacks_per_slot()
    {
        const string source = @"UnitBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/Units/Test/Test_lod0.scm', MaterialSets = { { Materials = { {
                DiffuseName = '/stale/path/Test_Diffuse.dds', ShaderMacros = 'team_color'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-slot-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "Units", "Test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Test_unit.bp"), source);
            File.WriteAllBytes(Path.Combine(directory, "Test_lod0.win.scm"), Array.Empty<byte>());
            foreach (string slot in new[] { "Diffuse", "Normal", "Mask", "Team" })
                File.WriteAllBytes(Path.Combine(directory, "Test_" + slot + ".win.dds"), Array.Empty<byte>());

            var result = SC2GameAssetDiscovery.Build(new[] { root }, new[] { root });
            var entry = Assert.Single(result.Entries);
            Assert.Empty(entry.Issues);
            Assert.Equal(4, entry.Textures.Count);
            var material = Assert.Single(Assert.Single(Assert.Single(entry.Asset.Lods).MaterialSets).Materials);
            foreach (string field in new[] { "DiffuseName", "NormalName", "MaskName", "TeamName" })
            {
                Assert.True(material.ResolvedTextures.TryGetValue(field, out string resolved));
                Assert.True(File.Exists(resolved));
            }
            Assert.False(entry.Textures.Single(texture => texture.FieldName == "DiffuseName").IsSiblingFallback);
            Assert.All(entry.Textures.Where(texture => texture.FieldName != "DiffuseName"), texture => Assert.True(texture.IsSiblingFallback));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_resolves_sibling_textures_for_prop_diffuse_lighting()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/props/building/Building_lod0.scm', MaterialSets = { { Materials = { {
                ShaderMacros = 'ambient_lighting, diffuse_lighting, specular_lighting'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-prop-sibling-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "props", "building");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Building_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(directory, "Building_lod0.win.scm"), Array.Empty<byte>());
            foreach (string slot in new[] { "Diffuse", "Normal", "Mask" })
                File.WriteAllBytes(Path.Combine(directory, "Building_" + slot + ".win.dds"), Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            var material = Assert.Single(Assert.Single(Assert.Single(entry.Asset.Lods).MaterialSets).Materials);
            Assert.True(entry.IsReady);
            Assert.EndsWith("Building_Diffuse.win.dds", material.ResolvedTextures["DiffuseName"], StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Building_Normal.win.dds", material.ResolvedTextures["NormalName"], StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Building_Mask.win.dds", material.ResolvedTextures["MaskName"], StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_builds_implicit_lod_and_material_for_srt_prop()
    {
        const string source = @"PropBlueprint { Display = { SRTPath = '/props/trees/Tree/Tree.srt', UniformScale = 0.12 } }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-srt-prop-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "props", "trees", "Tree");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Tree_prop.bp"), source);
            WriteScmWithSingleMaterial(Path.Combine(directory, "Tree_lod0.win.scm"), "Diffuse");
            string diffuse = Path.Combine(directory, "Tree_B_Atlas_D.win.dds");
            string normal = Path.Combine(directory, "Tree_B_Atlas_N.win.dds");
            File.WriteAllBytes(diffuse, Array.Empty<byte>());
            File.WriteAllBytes(normal, Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            var lod = Assert.Single(entry.Asset.Lods);
            var material = Assert.Single(Assert.Single(lod.MaterialSets).Materials);
            Assert.True(entry.IsReady);
            Assert.Equal("Diffuse", material.Name);
            Assert.Equal(diffuse, material.ResolvedTextures["DiffuseName"]);
            Assert.Equal(normal, material.ResolvedTextures["NormalName"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_marks_blueprint_without_visual_scm_as_not_ready()
    {
        const string source = @"PropBlueprint { Display = { UniformScale = 1 } }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-no-visual-prop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Missing_prop.bp"), source);
            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            Assert.False(entry.IsReady);
            Assert.Equal("MissingScm", Assert.Single(entry.Issues).Kind);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_does_not_block_team_color_material_when_implicit_team_texture_is_absent()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/props/test/test_lod0.scm', MaterialSets = { { Materials = { {
                DiffuseName = '/props/test/test_Diffuse.dds', NormalName = '/props/test/test_Normal.dds',
                MaskName = '/props/test/test_Mask.dds', ShaderMacros = 'diffuse_lighting, team_color'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-optional-team-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "props", "test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "test_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(directory, "test_lod0.win.scm"), Array.Empty<byte>());
            foreach (string slot in new[] { "Diffuse", "Normal", "Mask" })
                File.WriteAllBytes(Path.Combine(directory, "test_" + slot + ".win.dds"), Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            Assert.Empty(entry.Issues);
            Assert.Contains(entry.Textures, texture => texture.FieldName == "TeamName" && texture.ResolvedPath == null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_resolves_stale_texture_directory_by_unique_basename()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/props/test/test_lod0.scm', MaterialSets = { { Materials = { {
                MaskName = '/textures/stale/moved_mask.dds'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-unique-texture-" + Guid.NewGuid().ToString("N"));
        try
        {
            string blueprintDirectory = Path.Combine(root, "props", "test");
            string textureDirectory = Path.Combine(root, "textures", "actual");
            Directory.CreateDirectory(blueprintDirectory);
            Directory.CreateDirectory(textureDirectory);
            File.WriteAllText(Path.Combine(blueprintDirectory, "test_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(blueprintDirectory, "test_lod0.win.scm"), Array.Empty<byte>());
            string texturePath = Path.Combine(textureDirectory, "moved_mask.win.dds");
            File.WriteAllBytes(texturePath, Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            Assert.Empty(entry.Issues);
            Assert.Equal(texturePath, Assert.Single(entry.Textures).ResolvedPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_recovers_declared_texture_missing_its_slot_suffix()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/props/test/test_lod0.scm', MaterialSets = { { Materials = { {
                NormalName = '/props/test/test.dds'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-slot-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "props", "test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "test_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(directory, "test_lod0.win.scm"), Array.Empty<byte>());
            string normalPath = Path.Combine(directory, "test_Normal.win.dds");
            File.WriteAllBytes(normalPath, Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            Assert.True(entry.IsReady);
            Assert.Empty(entry.Issues);
            Assert.Equal(normalPath, Assert.Single(entry.Textures).ResolvedPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BrowserDiscovery_reports_missing_declared_texture_without_blocking_valid_model()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/props/test/test_lod0.scm', MaterialSets = { { Materials = { {
                DiffuseName = '/missing/texture.dds'
            } } } } } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-texture-warning-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "props", "test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "test_prop.bp"), source);
            File.WriteAllBytes(Path.Combine(directory, "test_lod0.win.scm"), Array.Empty<byte>());

            var entry = Assert.Single(SC2GameAssetDiscovery.Build(new[] { root }, new[] { root }).Entries);
            Assert.True(entry.IsReady);
            var warning = Assert.Single(entry.Issues);
            Assert.Equal("MissingTexture", warning.Kind);
            Assert.False(warning.IsBlocking);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalog_falls_back_from_stale_wreckage_mesh_to_owner_lod0()
    {
        const string source = @"PropBlueprint {
            Display = { Mesh = { LODs = { { MeshName = '/Units/Illuminate/UIB0021/UIB0012_lod0.scm' } } } }
        }";
        string root = Path.Combine(Path.GetTempPath(), "sc2-wreckage-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "Units", "Illuminate", "UIB0021");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "UIB0021_wreckage02_prop.bp"), source);
            string model = Path.Combine(directory, "UIB0021_lod0.win.scm");
            File.WriteAllBytes(model, Array.Empty<byte>());

            var catalog = SC2GameAssetCatalog.Build(new[] { root }, new[] { root });
            var asset = Assert.Single(catalog.Assets);
            Assert.Equal(model, Assert.Single(Assert.Single(asset.Lods).ModelPaths));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ScmReader_reads_v7_header_and_material_range()
    {
        var bytes = new byte[256];
        using (var stream = new MemoryStream(bytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { (byte)'M', (byte)'O', (byte)'D', (byte)'L' });
            writer.Write((uint)7); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)128); writer.Write((uint)1);
            stream.Position = 128; writer.Write((uint)200); writer.Write((uint)2); writer.Write((uint)3); writer.Write((uint)4); writer.Write((uint)5);
            stream.Position = 200; writer.Write(System.Text.Encoding.ASCII.GetBytes("Base\0"));
        }
        using (var stream = new MemoryStream(bytes))
        {
            var model = SC2ScmModelReader.Read(stream);
            Assert.Equal((uint)7, model.Version); var material = Assert.Single(model.Materials);
            Assert.Equal("Base", material.Name); Assert.Equal((uint)2, material.FaceCount); Assert.Equal((uint)3, material.FirstIndex);
        }
    }

    [Fact]
    public void ScmReader_reads_v8_with_v7_compatible_material_layout()
    {
        var bytes = new byte[256];
        using (var stream = new MemoryStream(bytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { (byte)'M', (byte)'O', (byte)'D', (byte)'L' });
            writer.Write((uint)8); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)128); writer.Write((uint)1);
            stream.Position = 128; writer.Write((uint)200); writer.Write((uint)2); writer.Write((uint)3); writer.Write((uint)4); writer.Write((uint)5);
            stream.Position = 200; writer.Write(System.Text.Encoding.ASCII.GetBytes("V8Base\0"));
        }
        using (var stream = new MemoryStream(bytes))
        {
            var model = SC2ScmModelReader.Read(stream);
            Assert.Equal((uint)8, model.Version);
            Assert.Equal("V8Base", Assert.Single(model.Materials).Name);
        }
    }

    [Fact]
    public void ScmReader_reads_short_v5_header_without_material_section()
    {
        var bytes = new byte[96];
        using (var stream = new MemoryStream(bytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { (byte)'M', (byte)'O', (byte)'D', (byte)'L' });
            writer.Write((uint)5);
            // v5 has exactly ten remaining fields: bone through total-bone count.
            for (var i = 0; i < 10; i++) writer.Write((uint)0);
        }
        using (var stream = new MemoryStream(bytes))
        {
            var model = SC2ScmModelReader.Read(stream);
            Assert.Equal((uint)5, model.Version);
            Assert.Equal(48, model.HeaderSize);
            Assert.Empty(model.Materials);
            Assert.Equal((uint)0, model.MaterialOffset);
            Assert.Equal((uint)0, model.MaterialCount);
        }
    }

    private static void WriteScmWithSingleMaterial(string path, string materialName)
    {
        var bytes = new byte[256];
        using (var stream = new MemoryStream(bytes))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { (byte)'M', (byte)'O', (byte)'D', (byte)'L' });
            writer.Write((uint)7);
            for (int index = 0; index < 10; index++) writer.Write((uint)0);
            writer.Write((uint)128);
            writer.Write((uint)1);
            stream.Position = 128;
            writer.Write((uint)200);
            writer.Write((uint)0);
            writer.Write((uint)0);
            writer.Write((uint)0);
            writer.Write((uint)0);
            stream.Position = 200;
            writer.Write(System.Text.Encoding.ASCII.GetBytes(materialName + "\0"));
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void AssertVanillaPropTextureWhenPresent(SC2GameAssetDiscoveryResult result, string root, string relativeBlueprintPath, string expectedDiffuseFile)
    {
        string blueprintPath = Path.Combine(root, relativeBlueprintPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(blueprintPath)) return;
        SC2GameAssetDiscoveryEntry entry = Assert.Single(result.Entries, candidate =>
            string.Equals(Path.GetFullPath(candidate.Asset.SourcePath), Path.GetFullPath(blueprintPath), StringComparison.OrdinalIgnoreCase));
        Assert.True(entry.IsReady, string.Join("; ", entry.Issues.Select(issue => issue.Message)));
        SC2GameAssetTextureResolution[] diffuse = entry.Textures.Where(texture =>
            string.Equals(texture.FieldName, "DiffuseName", StringComparison.OrdinalIgnoreCase) && texture.ResolvedPath != null).ToArray();
        Assert.NotEmpty(diffuse);
        Assert.All(diffuse, texture => Assert.EndsWith(expectedDiffuseFile, texture.ResolvedPath, StringComparison.OrdinalIgnoreCase));
    }
}
