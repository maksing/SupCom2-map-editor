// SC2CostsOverlay.shader
// Рендерит costs текстуру поверх terrain меша.
// UV вычисляются из мировых координат X/Z -> [0,1].
//
// Lives under Resources/ (moved from Shaders/) so Unity always includes it in
// a standalone build regardless of whether any scene/material references it
// — this shader is only ever bound at runtime via `new Material(shader)`
// (SC2CostsTerrainTypesEditor/SC2CostsShell), so a build that strips
// "unreferenced" assets would otherwise drop it, making Shader.Find("SC2/
// CostsOverlay") return null and `new Material(null)` throw
// "Value cannot be null. Parameter name: shader" — the exact class of bug
// already fixed for SC2/Terrain and SC2/Water (see SC2TerrainMaterialFactory.
// ResolveShader's own comment for the same failure mode).

Shader "SC2/CostsOverlay"
{
    Properties
    {
        _MainTex ("Costs Tex (RGBA32)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                // Terrain идёт от 0..1024 в X и Z
                o.uv = float2(world.x / 1024.0, world.z / 1024.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
