// Guaranteed-available fallback shader for runtime-generated placeholder
// materials (terrain import preview, heightmap/collision2 overlays, etc).
//
// Shader.Find("Standard")/Shader.Find("Universal Render Pipeline/Lit") work
// reliably in the Editor (it can find ANY compiled shader in the project),
// but in an actual standalone build Unity strips shaders that are not
// referenced by any material/scene in the build's "always included" list —
// so both calls can return null there, and `new Material(null)` throws
// "Value cannot be null. Parameter name: shader". Assets under a Resources/
// folder are always included in the build regardless of usage, so loading
// THIS shader via Resources.Load<Shader>("SC2RuntimeFallback") is guaranteed
// to succeed in both the Editor and a build. See SC2FallbackShader.cs.
Shader "SC2/RuntimeFallback"
{
    Properties
    {
        _Color ("Color", Color) = (0.7,0.7,0.7,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct a2v { float4 pos : POSITION; float3 nrm : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 worldNrm : TEXCOORD0; };

            v2f vert(a2v v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.pos);
                o.worldNrm = UnityObjectToWorldNormal(v.nrm);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Cheap directional shading so shapes read in 3D, no lighting setup required.
                float ndotl = saturate(dot(normalize(i.worldNrm), float3(0.4, 0.8, 0.4)));
                fixed3 shaded = _Color.rgb * (0.5 + 0.5 * ndotl);
                return fixed4(shaded, _Color.a);
            }
            ENDCG
        }
    }
    FallBack Off
}
