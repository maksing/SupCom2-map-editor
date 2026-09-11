using UnityEngine;

/// <summary>
/// Composite marker presets — placing one preset drops several related
/// markers at once, each offset from a single anchor point (Objects Editor
/// UX pass, "practical recommendations" #2). Distinct from
/// SC2MarkerTemplates (a single marker TYPE's default fields): a preset is a
/// small cluster of template references + relative offsets, e.g. a square
/// of 4 Mass markers placed with one click.
///
/// This is the one genuinely useful idea salvaged from the FAF map editor's
/// preset system (a prefab of child marker components with relative
/// offsets) — but built as a plain data table against this project's own
/// SC2MarkerTemplates registry instead of Unity prefabs, since Objects here
/// are POCOs, not GameObjects (see SC2ObjectsModel.cs's header comment for
/// why that split exists).
/// </summary>
public static class SC2MarkerPresets
{
    public sealed class Entry
    {
        public string TemplateName = "";
        public Vector3 Offset;
    }

    public sealed class Preset
    {
        public string Name = "";
        public Entry[] Entries = System.Array.Empty<Entry>();
    }

    public static readonly Preset[] All =
    {
        new()
        {
            Name = "Mass Square (Axis-aligned)",
            Entries = new[]
            {
                new Entry { TemplateName = "Mass", Offset = new Vector3(-10f, 0f, -10f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(10f, 0f, 10f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(-10f, 0f, 10f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(10f, 0f, -10f) },
            }
        },
        new()
        {
            Name = "Mass Square (Diamond)",
            Entries = new[]
            {
                new Entry { TemplateName = "Mass", Offset = new Vector3(10f, 0f, 0f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(-10f, 0f, 0f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(0f, 0f, 10f) },
                new Entry { TemplateName = "Mass", Offset = new Vector3(0f, 0f, -10f) },
            }
        },
    };
}
