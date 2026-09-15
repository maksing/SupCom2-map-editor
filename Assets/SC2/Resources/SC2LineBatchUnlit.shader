// SC2LineBatchUnlit.shader
//
// Vertex-colored, alpha-blended, always-on-top unlit shader for
// SC2ObjectsShell's batched GL line drawing (path graph edge overlay) —
// GL.Begin(GL.QUADS)/GL.Color/GL.Vertex3 needs a real Pass to SetPass into,
// and Unity's own built-in "Hidden/Internal-Colored" (the shader every GL
// line-drawing tutorial reaches for) lives in the engine's builtin resource
// archive, not this project, so it gets stripped from a standalone build the
// same way SC2FallbackShader's own doc comment describes — confirmed
// directly: adding it to Graphics Settings' Always Included Shaders instead
// of shipping our own actually broke the Standalone build (a build-time
// "Failed to write file: Resources/unity_builtin_extra" assertion), so a
// project-owned shader under Resources/ (this codebase's established fix for
// exactly this class of problem, see SC2FallbackShader.cs/
// SC2VertexColorUnlit.shader) is not just simpler, it is the one that
// actually builds.
Shader "SC2/LineBatchUnlit"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct a2v { float4 pos : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(a2v v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.pos);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
    FallBack Off
}
