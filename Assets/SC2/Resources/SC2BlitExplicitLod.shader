// Explicit-LOD blit for building small, correctly-antialiased preview
// thumbnails (SC2CustomTextureImporter.BuildFilteredPreview) without
// mutating the SOURCE texture asset's own mipMapBias property.
//
// The previous approach set `source.mipMapBias` before Graphics.Blit(source,
// dest) and restored it in a finally block. That mutation is GLOBAL to the
// Texture2D asset, not scoped to the blit call — any other renderer sampling
// that same asset (in particular, a real terrain Material's _Diffuse1/
// _Diffuse2 slot, which is frequently the exact same Texture2D object a
// preview box is also drawing) picks up whatever bias is currently set on
// it. Confirmed the real symptom this caused: the actual 3D-rendered terrain
// surface appearing blurred (matching a biased/higher mip), which "fixed
// itself" when toggling an unrelated material property — that toggle merely
// forced a repaint/material refresh that happened to occur after the
// preview's `finally` had already restored bias to 0, not a real fix.
//
// tex2Dlod samples an EXPLICIT mip level per-draw-call, entirely local to
// this one blit — it never reads or writes the source asset's own
// mipMapBias field, so no other renderer sharing the same Texture2D can ever
// observe a transient bias from this call.
Shader "Hidden/SC2/BlitExplicitLod"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _Lod ("Lod", Float) = 0 }
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Lod;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2Dlod(_MainTex, float4(i.uv, 0, _Lod));
            }
            ENDCG
        }
    }
}
