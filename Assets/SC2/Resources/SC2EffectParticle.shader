// SC2EffectParticle.shader
//
// The editor's preview of a game particle (see SC2EffectPreview): colour = particle texture * ramp texture, the same product the game's
// particle shader (effects/effects.hlsl, Effect1Map * Effect2Map) returns. The ramp is read at (life, ramp row); the blend (alpha or
// additive) is set per material. Under Resources/ so Resources.Load finds it in a standalone build.
Shader "SC2/EffectParticle"
{
    Properties
    {
        _MainTex ("Particle", 2D) = "white" {}
        _RampTex ("Ramp", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst", Float) = 10
        _Intensity ("Intensity", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _RampTex;
            float _Intensity;

            struct a2v { float4 pos : POSITION; float2 uv : TEXCOORD0; float2 ramp : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 ramp : TEXCOORD1; };

            v2f vert(a2v v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.pos);
                o.uv = v.uv;
                o.ramp = v.ramp;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 particle = tex2D(_MainTex, i.uv);
                fixed4 ramp = tex2D(_RampTex, saturate(i.ramp));
                fixed4 col = particle * ramp;
                col.rgb *= _Intensity;
                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
