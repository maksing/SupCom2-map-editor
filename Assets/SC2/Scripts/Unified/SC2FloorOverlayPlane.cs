using UnityEngine;

/// <summary>
/// The Costs / TerrainTypes overlay on the Collision2 floor plane. Below the floor the collision mesh is flat at the floor height, so
/// where the ground lies under it the overlay belongs on a plane at that height; this keeps that plane (a quad covering the
/// map, lit by SC2/FloorOverlay) and tells the terrain shaders, through two globals, to leave the overlay out of the ground below the
/// floor. With no floor, or no overlay, the plane is hidden and the globals say "off".
/// </summary>
public static class SC2FloorOverlayPlane
{
    private static readonly int FloorOnId = Shader.PropertyToID("_SC2OverlayFloorOn");
    private static readonly int FloorYId = Shader.PropertyToID("_SC2OverlayFloorY");
    private static GameObject _plane;
    private static Material _material;
    private static Mesh _mesh;

    /// <summary>Call every time the overlay is pushed to (or taken from) the terrain. <paramref name="floorHeight"/> is the Collision2
    /// floor and <paramref name="collisionOffset"/> the Collision2 height offset: the mesh lies that much above the ground, so the ground
    /// is below the floor where it is under floor - offset.</summary>
    public static void Apply(Texture overlay, int gridSize, bool floorEnabled, float floorHeight, float collisionOffset)
    {
        var on = overlay != null && floorEnabled && gridSize > 0;
        Shader.SetGlobalFloat(FloorOnId, on ? 1f : 0f);
        Shader.SetGlobalFloat(FloorYId, floorHeight - collisionOffset);
        if (!on)
        {
            if (_plane != null) _plane.SetActive(false);
            return;
        }

        if (_plane == null) Create();
        if (_plane == null) return;
        _material.SetTexture("_CostsOverlayTex", overlay);
        _material.SetFloat("_CostsOverlayGridSize", gridSize);
        _plane.transform.position = new Vector3(gridSize * 0.5f, floorHeight, gridSize * 0.5f);
        _plane.transform.localScale = new Vector3(gridSize, 1f, gridSize);
        _plane.SetActive(true);
    }

    private static void Create()
    {
        var shader = Resources.Load<Shader>("SC2FloorOverlay") ?? Shader.Find("SC2/FloorOverlay");
        if (shader == null) return;
        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _mesh = new Mesh { name = "SC2FloorOverlayQuad", hideFlags = HideFlags.HideAndDontSave };
        _mesh.SetVertices(new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) });
        _mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
        _mesh.RecalculateBounds();
        _plane = new GameObject("SC2FloorOverlay") { hideFlags = HideFlags.HideAndDontSave };
        _plane.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _plane.AddComponent<MeshRenderer>().sharedMaterial = _material;
    }
}
