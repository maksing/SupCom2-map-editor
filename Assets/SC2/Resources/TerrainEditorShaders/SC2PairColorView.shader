// Converts PairMap (R8, pair index 0..254) to colour using a lookup array.
// No CPU work — runs entirely on GPU as a blit over TerrainView chunks.
Shader "Hidden/SC2/PairColorView"
{
    Properties { _MainTex("PairMap",2D)="grey"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex   vert_img
            #pragma fragment frag
             #include "UnityCG.cginc"
             sampler2D _MainTex;
             float4 _MainTex_TexelSize;
             // Up to 72 pair colours packed as float4 array (xy=rg, zw=ba)
             float4 _PairColors[72];
             int    _PairCount;
             int    _UseBlend; // 0=mat1 color, 1=lerp(mat1,mat2,0.5)
             float4 frag(v2f_img i):SV_Target{
                 float2 texel = _MainTex_TexelSize.xy * 0.5;
                 float2 uv = clamp(i.uv, texel, 1.0 - texel);
                 float idx255 = tex2D(_MainTex,uv).r * 255.0;
                int   idx    = (int)round(idx255);
                if(idx<0||idx>=_PairCount) return float4(.4,.4,.4,1);
                return float4(_PairColors[idx].rgb,1);
            }
            ENDCG
        }
    }
}
