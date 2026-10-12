// SC2FloorOverlay.shader
// The Costs / TerrainTypes overlay on the Collision2 floor plane. Where the ground lies below the floor, the collision mesh is
// flat at the floor height, so the overlay is shown there, on a plane at that height, instead of on the terrain surface far below
// (the terrain shaders leave the overlay out under the floor, see _SC2OverlayFloorY). The plane is depth tested, so wherever
// the ground rises above it the ground hides the plane.
//
// Lives under Resources/ so a standalone build always includes it (it is only ever bound at runtime through
// `new Material(shader)`, see SC2CostsOverlay.shader's own comment for the same reason).

Shader "SC2/FloorOverlay"
{
    Properties
    {
        _CostsOverlayTex ("Costs/TerrainTypes Overlay", 2D) = "black" {}
        _CostsOverlayGridSize ("Costs Overlay Grid Size", Float) = 1024
    }

    SubShader
    {
        // After the decals (SC2Decal.shader draws at Transparent+20 and leaves the overlay out below the floor, expecting this plane to
        // carry it): drawn before them, a decal hid the overlay wherever it lay.
        Tags { "RenderType"="Transparent" "Queue"="Transparent+30" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CostsOverlayTex;
            float _CostsOverlayGridSize;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_CostsOverlayTex, saturate(i.world.xz / max(_CostsOverlayGridSize, 1.0)));
            }
            ENDCG
        }
    }
}
