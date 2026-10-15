// Supreme Commander 2 sky for Unity's Built-in Render Pipeline: effects/sky.hlsl, drawn as the scene's skybox.
// The game draws a dome around the camera and looks the sky cubemap (info.bdf SkyMap) up with the world-space direction from
// the camera to the vertex. The skybox mesh of the camera is already centred on it, so its vertex position is that direction.
// Nothing else is applied to the sample: no tint, no fog, no lighting. The game works on the texel values as they are stored, so
// when the cubemap is not flagged as sRGB and the project works in linear space, the values are converted to linear first
// (_SkyDecode) and the output stage brings them back unchanged.
Shader "SC2/Sky"
{
    Properties
    {
        _SkyMap ("Sky Map", Cube) = "black" {}
        _SkyDecode ("Decode gamma", Float) = 0
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "Sky"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            samplerCUBE _SkyMap;
            float _SkyDecode;

            struct Attributes
            {
                float4 vertex : POSITION;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float3 viewVec : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.viewVec = v.vertex.xyz;
                return o;
            }

            fixed4 frag(Varyings i) : SV_Target
            {
                fixed4 color = texCUBE(_SkyMap, i.viewVec);
                if (_SkyDecode > 0.5) color.rgb = GammaToLinearSpace(color.rgb);
                return color;
            }
            ENDCG
        }
    }

    Fallback Off
}
