// SC2VertexColorUnlit.shader
//
// Plain per-vertex-color unlit shader — used by the imported terrain.bdf
// Slope/Height preview modes (SC2GameViewEditorUI.ApplyPreviewModeToImportedTerrain),
// which bake a slope- or height-derived color directly into the mesh's
// vertex colors and need a shader that just displays them, unlit, with no
// other texture/lighting inputs. Lives under Resources/ so Resources.Load
// finds it reliably in a standalone build (see SC2FallbackShader.cs for why
// Shader.Find alone is not reliable there).
Shader "SC2/VertexColorUnlit"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
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
