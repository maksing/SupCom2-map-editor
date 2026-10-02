using System.IO;
using System.Linq;
using System.Text;

public sealed class EffectTemplateNamesTests
{
    private const string Sample = @"
-- comment with a non-ASCII byte: ©
EffectTemplates = {
	Wreckage = { Small = { Fire = { { '/effects/a.bp' } } } },
	Marker = {
		SmokeCloud02 = {
			'/effects/smoke_07.bp',
		},
		BlinkingLight01 = { -- trailing comment
			'/effects/light_01.bp',
			'/effects/light_02.bp',
		},
		NotATemplate = 5,
	},
	Other = { Marker = { Decoy = { '/effects/decoy.bp' } } },
}";

    [Fact]
    public void ReadMarkerTemplates_ReturnsTheMarkerTableSortedByName()
    {
        var templates = SC2EffectTemplateNames.ReadMarkerTemplates(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(new[] { "BlinkingLight01", "SmokeCloud02" }, templates.Select(t => t.Name).ToArray());
        Assert.Equal(new[] { "/effects/light_01.bp", "/effects/light_02.bp" }, templates[0].Emitters.ToArray());
        Assert.Single(templates[1].Emitters);
    }

    [Fact]
    public void ReadMarkerTemplates_ReturnsNothingWithoutAnEffectTemplatesTable()
    {
        Assert.Empty(SC2EffectTemplateNames.ReadMarkerTemplates(Encoding.UTF8.GetBytes("Other = {}")));
    }

    [Fact]
    public void ReadMarkerTemplates_ReportsBrokenLua()
    {
        Assert.Throws<InvalidDataException>(() => SC2EffectTemplateNames.ReadMarkerTemplates(Encoding.UTF8.GetBytes("EffectTemplates = {")));
    }

    // Optional checks against the game's real file, as source or as the shipped bytecode: set
    // SC2_EFFECT_TEMPLATES_PATH to a copy of lua/sim/EffectTemplates.lua. Skipped (passes) when unset.
    [Fact]
    public void ReadMarkerTemplates_RealFile_HasTheMapEffects()
    {
        var path = System.Environment.GetEnvironmentVariable("SC2_EFFECT_TEMPLATES_PATH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var templates = SC2EffectTemplateNames.ReadMarkerTemplates(File.ReadAllBytes(path));
        var c02 = templates.Single(t => t.Name == "C02Fog01");
        Assert.Equal("/effects/emitters/ambient/terrain/smoke/ambient_t_smoke_08_large_emit.bp", c02.Emitters.Single());
        Assert.True(templates.Count >= 20, "expected the vanilla Marker table, got " + templates.Count);
    }
}
