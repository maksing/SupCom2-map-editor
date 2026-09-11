using System.Reflection;

namespace InfoBdfEditorTests;

/// <summary>
/// Sanity checks on InfoFieldLimits.cs — the shared slider-range source
/// both the Unity panel and this app read from. Reflection-driven so it
/// automatically covers every constant that exists today AND any added
/// later, without needing a matching new test per field.
/// </summary>
public sealed class InfoFieldLimitsTests
{
    private static readonly Type LimitsType = typeof(InfoFieldLimits);

    public static IEnumerable<object[]> FloatLimitFields() =>
        LimitsType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof((float Min, float Max)))
            .Select(f => new object[] { f.Name });

    public static IEnumerable<object[]> IntLimitFields() =>
        LimitsType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof((int Min, int Max)))
            .Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(FloatLimitFields))]
    public void FloatRangeMinIsNeverGreaterThanMax(string fieldName)
    {
        var (min, max) = ((float Min, float Max))LimitsType.GetField(fieldName)!.GetValue(null)!;
        Assert.True(min <= max, $"{fieldName}: Min ({min}) > Max ({max})");
    }

    [Theory]
    [MemberData(nameof(FloatLimitFields))]
    public void FloatRangeIsFinite(string fieldName)
    {
        var (min, max) = ((float Min, float Max))LimitsType.GetField(fieldName)!.GetValue(null)!;
        Assert.True(float.IsFinite(min), $"{fieldName}.Min is not finite ({min})");
        Assert.True(float.IsFinite(max), $"{fieldName}.Max is not finite ({max})");
    }

    [Theory]
    [MemberData(nameof(IntLimitFields))]
    public void IntRangeMinIsNeverGreaterThanMax(string fieldName)
    {
        var (min, max) = ((int Min, int Max))LimitsType.GetField(fieldName)!.GetValue(null)!;
        Assert.True(min <= max, $"{fieldName}: Min ({min}) > Max ({max})");
    }

    [Fact]
    public void AtLeastOneFloatAndOneIntLimitExist()
    {
        // Guards against the reflection filter itself silently matching
        // nothing (e.g. after a future refactor changes the tuple shape) —
        // without this, every [Theory] above would report "0 tests ran"
        // and look like a pass.
        Assert.NotEmpty(FloatLimitFields());
        Assert.NotEmpty(IntLimitFields());
    }
}
