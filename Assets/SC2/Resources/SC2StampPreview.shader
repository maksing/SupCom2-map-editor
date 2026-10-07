// SC2StampPreview.shader
//
// The ground preview of a stamp brush being placed (SC2StampPreview): a translucent, tinted copy of the stamp image laid on the
// terrain, optionally shaded so a lifted/lowered stamp reads as a shape. Unlit apart from that fake shading; blended over the terrain
// with a depth offset so it does not z-fight with it. Under Resources/ so Resources.Load finds it in a standalone build.
Shader "SC2/StampPreview"
{
    Properties
    {
        _MainTex ("Stamp", 2D) = "white" {}
        _Color ("Tint", Color) = (0, 0.85, 1, 0.6)
        _Shade ("Shade", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -2, -2
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _Shade;

            struct a2v { float4 pos : POSITION; float2 uv : TEXCOORD0; float3 nrm : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 nrm : TEXCOORD1; };

            v2f vert(a2v v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.pos);
                o.uv = v.uv;
                o.nrm = v.nrm;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 stamp = tex2D(_MainTex, i.uv);
                fixed4 col = fixed4(_Color.rgb, stamp.a * _Color.a);
                if (_Shade > 0.5)
                {
                    float3 n = normalize(i.nrm);
                    float lit = saturate(dot(n, normalize(float3(0.4, 0.8, 0.3))));
                    col.rgb *= 0.45 + 0.75 * lit;
                    col.a = saturate(col.a + 0.25 * stamp.a);
                }
                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
