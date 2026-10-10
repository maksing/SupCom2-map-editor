// SC2PropCostsOverlay.shader
// The Costs / TerrainTypes overlay on the surfaces of props that are part of the heightmap (SC2ObjProp.IncludeInHeightmap).
// The terrain shaders drape the overlay over the terrain itself, so under a building it would sit on the ground, hidden by
// the building; this draws it on the building's own surfaces instead, from the same world-space X/Z lookup.
//
// Lives under Resources/ so a standalone build always includes it (it is only ever bound at runtime through
// `new Material(shader)`, see SC2CostsOverlay.shader's own comment for the same reason).

Shader "SC2/PropCostsOverlay"
{
    Properties
    {
        _CostsOverlayTex ("Costs/TerrainTypes Overlay", 2D) = "black" {}
        _CostsOverlayGridSize ("Costs Overlay Grid Size", Float) = 1024
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CostsOverlayTex;
            float _CostsOverlayGridSize;
            // The Collision2 floor plane (SC2FloorOverlayPlane): where the ground lies below it the overlay is drawn on the plane instead.
            float _SC2OverlayFloorOn;
            float _SC2OverlayFloorY;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_SC2OverlayFloorOn > 0.5 && i.world.y < _SC2OverlayFloorY) discard;
                half4 overlay = tex2D(_CostsOverlayTex, saturate(i.world.xz / max(_CostsOverlayGridSize, 1.0)));
                // Every surface of the prop shows the cells under it, walls included (a wall shows its cell's colour straight
                // up its side), so the building reads as see-through to the costs; a wall faces away only a little less.
                half facing = lerp(0.8, 1.0, saturate(normalize(i.normal).y));
                return fixed4(overlay.rgb, overlay.a * facing);
            }
            ENDCG
        }
    }
}
