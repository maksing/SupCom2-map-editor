using UnityEngine;

/// <summary>
/// Resolves a shader that is guaranteed to work for runtime-generated
/// placeholder materials, in both the Editor and a standalone build.
///
/// The bug this fixes: several call sites used
/// `Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")`.
/// In the Editor, Shader.Find can locate ANY shader compiled in the project,
/// so that pattern always "worked" there — but in an actual build, Unity
/// strips shaders nothing references (no material/scene uses them), and both
/// calls can return null, so the subsequent `new Material(shader)` throws
/// "Value cannot be null. Parameter name: shader" (exactly the crash seen at
/// the "terrain.bdf" stage of the runtime .scd import). A shader shipped
/// under a Resources/ folder is always included in the build regardless of
/// usage, so loading it via Resources.Load is reliable everywhere.
/// </summary>
public static class SC2FallbackShader
{
    private static Shader _cached;

    public static Shader Get()
    {
        if (_cached != null)
            return _cached;

        // Primary: our own Resources-shipped shader — always included in any build.
        _cached = Resources.Load<Shader>("SC2RuntimeFallback");
        if (_cached != null)
            return _cached;

        // Fallbacks below only help in the Editor (or a build that happens to
        // already include one of these for other reasons) — kept as a
        // best-effort chain rather than the primary strategy.
        _cached = Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Standard")
                  ?? Shader.Find("Legacy Shaders/Diffuse")
                  ?? Shader.Find("Hidden/InternalErrorShader");

        if (_cached == null)
            Debug.LogError("[SC2FallbackShader] No shader could be resolved at all — Resources/SC2RuntimeFallback.shader is missing or failed to compile.");

        return _cached;
    }
}
