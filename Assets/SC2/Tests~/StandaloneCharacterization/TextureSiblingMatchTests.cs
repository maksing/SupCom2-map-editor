using System.Collections.Generic;
using SC2.MapEditor.Textures;

namespace SC2CharacterizationTests;

/// <summary>
/// Coverage tests for SC2TextureSiblingMatcher (Scripts/MapDocument/
/// TextureBrowser/SC2TextureSiblingMatcher.cs) — the pure matching core
/// behind SC2TextureCatalog.FindAllSiblingMatches/TryFindSiblingMatch,
/// which suggests a Diffuse/Normal/Mask texture's counterpart on the same
/// material for the texture browser's "sibling suggestions" feature.
///
/// The corpus below is drawn from a real game install's textures/Terrain
/// tree (naming conventions confirmed by direct inspection, not guessed):
/// single-letter suffixes ("_d"/"_n"/"_s"/"_m"), optionally with a
/// numbered-variant suffix ("_001"); whole-word suffixes with inconsistent
/// capitalization ("_Diffuse"/"_Normal"); real decal atlas names
/// ("<map>.decal_atlas_diffuse.win.dds"); and per-map decal files
/// ("sc2_mp_001_decal01_d.win.dds"). It also covers the separate
/// prop/building AO/Specular/Reflectivity/SpecMask convention that must
/// NOT be treated as a terrain-material sibling, since that's a different
/// material system entirely (confirmed by inspecting real files like
/// "I01_CPNBuilding001_AO.win.dds").
/// </summary>
public class TextureSiblingMatchTests
{
    // Mirrors SC2TextureCatalog.LoadArchiveLibrary's own real entry
    // construction: Id must be built from ArchiveEntryPath explicitly
    // before Normalize(), since (unlike ResourcePath/GamePath)
    // ArchiveEntryPath isn't part of AddEntry's own empty-Id fallback.
    private static SC2TextureCatalogEntry Entry(string path, SC2TextureSource source = SC2TextureSource.Vanilla)
    {
        var entry = new SC2TextureCatalogEntry
        {
            ArchiveEntryPath = path,
            Source = source,
            Id = SC2TextureCatalogEntry.BuildStableId(source, path),
        };
        entry.Normalize();
        return entry;
    }

    private static Dictionary<string, SC2TextureCatalogEntry> Index(params SC2TextureCatalogEntry[] entries)
    {
        var byId = new Dictionary<string, SC2TextureCatalogEntry>();
        foreach (var entry in entries) byId[entry.Id] = entry;
        return byId;
    }

    [Fact]
    public void SingleLetterSuffix_FindsNormalAndMaskFromDiffuse()
    {
        var diffuse = Entry("textures/Terrain/sandstone_001a_d.win.dds");
        var normal = Entry("textures/Terrain/sandstone_001a_n.win.dds");
        var mask = Entry("textures/Terrain/sandstone_001a_s.win.dds");
        var byId = Index(diffuse, normal, mask);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
        Assert.Equal(mask.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Mask, byId)?.Id);
        Assert.Equal(diffuse.Id, SC2TextureSiblingMatcher.FindFirst(normal, SC2TextureUsage.Diffuse, byId)?.Id);
    }

    [Fact]
    public void NumberedVariantSuffix_StillMatchesAcrossUsages()
    {
        var diffuse = Entry("textures/Terrain/Rock_wall_brown_d_001.win.dds");
        var normal = Entry("textures/Terrain/Rock_wall_brown_n_001.win.dds");
        var mask = Entry("textures/Terrain/Rock_wall_brown_s_001.win.dds");
        var byId = Index(diffuse, normal, mask);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
        Assert.Equal(mask.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Mask, byId)?.Id);
    }

    [Fact]
    public void BothMaskSpellings_AreBothReturnedByFindAll()
    {
        // Real vanilla data uses both "_s" and "_m" for the same packed
        // mask/specular channel in different art folders — a set that
        // genuinely has both must offer both, not silently pick one.
        var diffuse = Entry("textures/Terrain/Grass01_d.win.dds");
        var maskS = Entry("textures/Terrain/Grass01_s.win.dds");
        var maskM = Entry("textures/Terrain/Grass01_m.win.dds");
        var byId = Index(diffuse, maskS, maskM);

        var all = SC2TextureSiblingMatcher.FindAll(diffuse, SC2TextureUsage.Mask, byId);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == maskS.Id);
        Assert.Contains(all, e => e.Id == maskM.Id);
    }

    [Fact]
    public void WholeWordSuffix_MatchesRegardlessOfCapitalization()
    {
        var diffuse = Entry("textures/Terrain/CA_C02/C03_CommArray01_Diffuse.win.dds");
        var normal = Entry("textures/Terrain/CA_C02/C03_CommArray01_normal.win.dds");
        var byId = Index(diffuse, normal);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
        Assert.Equal(diffuse.Id, SC2TextureSiblingMatcher.FindFirst(normal, SC2TextureUsage.Diffuse, byId)?.Id);
    }

    [Fact]
    public void DecalAtlasSuffix_MatchesDiffuseAndNormalAtlas()
    {
        var diffuse = Entry("textures/Terrain/CA_C02/SC2_CA_C02.decal_atlas_diffuse.win.dds");
        var normal = Entry("textures/Terrain/CA_C02/SC2_CA_C02.decal_atlas_normal.win.dds");
        var byId = Index(diffuse, normal);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
        Assert.Equal(diffuse.Id, SC2TextureSiblingMatcher.FindFirst(normal, SC2TextureUsage.Diffuse, byId)?.Id);
    }

    [Fact]
    public void PerMapDecalFile_MatchesNormalFromDiffuse()
    {
        var diffuse = Entry("textures/Terrain/MP_001/sc2_mp_001_decal01_d.win.dds");
        var normal = Entry("textures/Terrain/MP_001/sc2_mp_001_decal01_n.win.dds");
        var byId = Index(diffuse, normal);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
    }

    [Fact]
    public void CustomSourcedEntry_FallsBackToGamePathWhenNoArchiveOrResourcePath()
    {
        var diffuse = new SC2TextureCatalogEntry { GamePath = "C:/maps/mymap/decals_diffuse.dds", Source = SC2TextureSource.Custom };
        diffuse.Id = SC2TextureCatalogEntry.BuildStableId(diffuse.Source, diffuse.GamePath);
        diffuse.Normalize();
        var normal = new SC2TextureCatalogEntry { GamePath = "C:/maps/mymap/decals_normal.dds", Source = SC2TextureSource.Custom };
        normal.Id = SC2TextureCatalogEntry.BuildStableId(normal.Source, normal.GamePath);
        normal.Normalize();
        var byId = Index(diffuse, normal);

        Assert.Equal(normal.Id, SC2TextureSiblingMatcher.FindFirst(diffuse, SC2TextureUsage.Normal, byId)?.Id);
    }

    [Theory]
    [InlineData("I01_CPNBuilding001_AO.win.dds")]
    [InlineData("MP_201_CPNBuilding001_Specular.win.dds")]
    [InlineData("MP_201_CPNBuilding001_Reflectivity.win.dds")]
    [InlineData("MP_201_CPNBuilding001_specmask.win.dds")]
    [InlineData("MP_201_CPNBuilding001_spec.win.dds")]
    public void PropMaterialSuffixes_AreNeverTreatedAsTerrainSiblings(string leaf)
    {
        // These belong to the separate unit/prop material system, not
        // SC2MaterialsPairsMode terrain pairs — the matcher must not even
        // recognize the suffix, let alone guess a Diffuse/Normal/Mask
        // sibling for one.
        var entry = Entry("textures/Units/UEF/" + leaf);
        var diffuseNearby = Entry("textures/Units/UEF/MP_201_CPNBuilding001_d.win.dds");
        var byId = Index(entry, diffuseNearby);

        Assert.Empty(SC2TextureSiblingMatcher.FindAll(entry, SC2TextureUsage.Diffuse, byId));
        Assert.Empty(SC2TextureSiblingMatcher.FindAll(entry, SC2TextureUsage.Normal, byId));
        Assert.Empty(SC2TextureSiblingMatcher.FindAll(entry, SC2TextureUsage.Mask, byId));
    }

    [Fact]
    public void SoloTexture_WithNoRealSiblingInCatalog_ReturnsNull()
    {
        var solo = Entry("textures/Terrain/SkyDome_d.win.dds");
        var byId = Index(solo);

        Assert.Null(SC2TextureSiblingMatcher.FindFirst(solo, SC2TextureUsage.Normal, byId));
    }

    [Fact]
    public void NullOrUnresolvablePath_IsSafeNoOp()
    {
        var byId = Index(Entry("textures/Terrain/sandstone_001a_n.win.dds"));
        Assert.Empty(SC2TextureSiblingMatcher.FindAll(null, SC2TextureUsage.Normal, byId));

        var noPath = new SC2TextureCatalogEntry { Source = SC2TextureSource.Vanilla };
        Assert.Empty(SC2TextureSiblingMatcher.FindAll(noPath, SC2TextureUsage.Normal, byId));
    }

    /// <summary>Runs the whole confirmed-real-family corpus above through
    /// FindFirst for every OTHER usage in Diffuse/Normal/Mask, as a single
    /// aggregate coverage assertion — the intent behind this (a coverage
    /// test close to 100% for genuinely related real files, per the
    /// original ask) is that every one of these representative real
    /// families must fully cross-resolve, not just the single pairs each
    /// dedicated test above already checks individually.</summary>
    [Fact]
    public void RepresentativeRealFamilies_AllCrossResolveCompletely()
    {
        var families = new (string Diffuse, string Normal, string Mask)[]
        {
            ("textures/Terrain/sandstone_001a_d.win.dds", "textures/Terrain/sandstone_001a_n.win.dds", "textures/Terrain/sandstone_001a_s.win.dds"),
            ("textures/Terrain/Rock_wall_brown_d_001.win.dds", "textures/Terrain/Rock_wall_brown_n_001.win.dds", "textures/Terrain/Rock_wall_brown_s_001.win.dds"),
            ("textures/Terrain/CA_C02/C03_CommArray01_Diffuse.win.dds", "textures/Terrain/CA_C02/C03_CommArray01_Normal.win.dds", null),
            ("textures/Terrain/CA_C02/SC2_CA_C02.decal_atlas_diffuse.win.dds", "textures/Terrain/CA_C02/SC2_CA_C02.decal_atlas_normal.win.dds", null),
            ("textures/Terrain/MP_001/sc2_mp_001_decal01_d.win.dds", "textures/Terrain/MP_001/sc2_mp_001_decal01_n.win.dds", null),
        };

        var entries = new List<SC2TextureCatalogEntry>();
        var byFamily = new List<(SC2TextureCatalogEntry Diffuse, SC2TextureCatalogEntry Normal, SC2TextureCatalogEntry Mask)>();
        foreach (var family in families)
        {
            var diffuse = Entry(family.Diffuse);
            var normal = Entry(family.Normal);
            SC2TextureCatalogEntry mask = family.Mask != null ? Entry(family.Mask) : null;
            entries.Add(diffuse);
            entries.Add(normal);
            if (mask != null) entries.Add(mask);
            byFamily.Add((diffuse, normal, mask));
        }
        var byId = Index(entries.ToArray());

        var total = 0;
        var resolved = 0;
        foreach (var family in byFamily)
        {
            total++; resolved += SC2TextureSiblingMatcher.FindFirst(family.Diffuse, SC2TextureUsage.Normal, byId)?.Id == family.Normal.Id ? 1 : 0;
            total++; resolved += SC2TextureSiblingMatcher.FindFirst(family.Normal, SC2TextureUsage.Diffuse, byId)?.Id == family.Diffuse.Id ? 1 : 0;
            if (family.Mask == null) continue;
            total++; resolved += SC2TextureSiblingMatcher.FindFirst(family.Diffuse, SC2TextureUsage.Mask, byId)?.Id == family.Mask.Id ? 1 : 0;
            total++; resolved += SC2TextureSiblingMatcher.FindFirst(family.Mask, SC2TextureUsage.Diffuse, byId)?.Id == family.Diffuse.Id ? 1 : 0;
        }

        Assert.Equal(total, resolved);
    }
}
