// Flattens one blend mask editing layer over the running composite (Normal blend):
//   alpha = lerp(previous alpha, layer value, layer coverage * opacity)
// A layer render texture stores its VALUE in R and its COVERAGE in G (see SC2BlendMaskLayer). The composite
// keeps the blend mask's usual layout: neutral RGB 0.5 and the blend factor in alpha, so it can be written
// straight into SC2TerrainRuntime.BlendRT, which the terrain shader and the mask export read.
// NO GPU BLEND — the previous composite is the source texture and the result is returned explicitly.
Shader "Hidden/SC2/BlendComposite"
{
    Properties
    {
        _MainTex("Composite so far", 2D) = "black" {}
        _LayerTex("Layer (R value, G coverage)", 2D) = "black" {}
        _Opacity("Opacity", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _LayerTex;
            float _Opacity;

            float4 frag(v2f_img i) : SV_Target
            {
                float previous = tex2D(_MainTex, i.uv).a;
                float4 layer = tex2D(_LayerTex, i.uv);
                float weight = saturate(layer.g * _Opacity);
                return float4(0.5, 0.5, 0.5, lerp(previous, layer.r, weight));
            }
            ENDCG
        }
    }
}
