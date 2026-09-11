// SC2MatPairPreview.shader
//
// Preview shader for the imported-terrain.bdf "Mats"/"Pairs" toggle
// (SC2GameViewEditorUI.BuildMatPairPreviewMaterials). A "material" is a
// single texture (D1 or D2) — _Color1/_Color2 are flat colors assigned by
// texture identity (see ColorForKey, shared across the whole mesh so the
// same source texture always maps to the same color everywhere). The real
// blend factor between D1 and D2 comes from the same RNM mask alpha the
// actual SC2/Terrain shader uses:
//   - Mats  (_Discrete=1): a hard D1-or-D2 pick at the 0.5 threshold — shows
//     which single texture "owns" each point, no interpolation.
//   - Pairs (_Discrete=0): the real smooth lerp(D1, D2, blend) — shows the
//     pair as an actual blend, matching how the terrain is really shaded.
// A material with no real mask assigned defaults to a black mask (alpha=0),
// so it just renders as flat _Color1 either way — correct for materials
// that only ever use a single diffuse texture.
Shader "SC2/MatPairPreview"
{
    Properties
    {
        _Color1 ("Color 1 (D1)", Color) = (1,0,0,1)
        _Color2 ("Color 2 (D2)", Color) = (0,0,1,1)
        _RNMMask1 ("Blend Mask (alpha = blend factor)", 2D) = "black" {}
        [Toggle] _Discrete ("Discrete (Mats) vs Smooth (Pairs)", Float) = 1
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

            sampler2D _RNMMask1;
            fixed4 _Color1;
            fixed4 _Color2;
            float _Discrete;

            struct a2v { float4 pos : POSITION; float2 uv2 : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float2 uv2 : TEXCOORD1; };

            v2f vert(a2v v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.pos);
                o.uv2 = v.uv2;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                half blend = tex2D(_RNMMask1, i.uv2).a;
                if (_Discrete > 0.5)
                    blend = blend < 0.5h ? 0.0h : 1.0h;
                return lerp(_Color1, _Color2, blend);
            }
            ENDCG
        }
    }
    FallBack Off
}
