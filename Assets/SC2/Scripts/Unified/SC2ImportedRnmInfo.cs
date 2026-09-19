using System.Collections.Generic;

/// <summary>One layer (material) of an imported terrain.bdf as shown in
/// Terrain &gt; Lighting Bake &gt; Imported Objects: which imported RNM mask its
/// lighting is baked onto ("" = none). Built by SC2GameViewEditorUI, which
/// owns the imported objects.</summary>
public sealed class SC2ImportedRnmLayerInfo
{
    public string MaterialName = "";
    public int Triangles;
    public string MaskName = "";
}

public sealed class SC2ImportedRnmObjectInfo
{
    public string ObjectId = "";
    public string Name = "";
    public List<SC2ImportedRnmLayerInfo> Layers = new();
}
