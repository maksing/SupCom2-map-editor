using System.Text;
using Xunit;

public class PropDestructableTests
{
    private static SC2GameAsset Load(string blueprint)
    {
        var path = "props/trees/test/test_prop.bp";
        var result = SC2GameAssetCatalog.BuildFromArchive(new[] { path }, _ => Encoding.UTF8.GetBytes(blueprint));
        return Assert.Single(result.Assets);
    }

    [Fact]
    public void A_prop_with_Destructable_true_breaks() =>
        Assert.True(Load("PropBlueprint {\n    Physics = {\n        BlockPath = false,\n        Destructable = true,\n    },\n}").Destructable);

    [Fact]
    public void A_prop_with_Destructable_false_stays_standing() =>
        Assert.False(Load("PropBlueprint {\n    Physics = {\n        BlockPath = true,\n        Destructable = false,\n    },\n}").Destructable);

    [Fact]
    public void A_prop_without_a_Physics_table_stays_standing() =>
        Assert.False(Load("PropBlueprint {\n    Categories = { 'TREE' },\n}").Destructable);
}
