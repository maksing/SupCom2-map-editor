// SC2SelectionOutline.shader
// The outline of a selected object, along the edge of its silhouette on screen (as in Blender), instead of a box around it.
// Pass 0 draws the object flat white into a mask texture; pass 1 reads that mask while blitting onto the camera target and
// draws a coloured line on the pixels just outside the mask. See SC2SelectionOutline.cs.
//
// Lives under Resources/ so a standalone build always includes it (it is only ever bound at runtime through
// `new Material(shader)`, see SC2CostsOverlay.shader's own comment for the same reason).

Shader "SC2/SelectionOutline"
{
    Properties
    {
        _MainTex ("Mask", 2D) = "black" {}
        _OutlineColor ("Outline Color", Color) = (1, 0.62, 0.1, 1)
        _OutlineWidth ("Outline Width (pixels)", Float) = 2
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        // 0: the object, flat white.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            fixed4 frag() : SV_Target { return fixed4(1, 1, 1, 1); }
            ENDCG
        }

        // 1: the outline, on the pixels outside the mask that touch it.
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _OutlineColor;
            float _OutlineWidth;

            fixed4 frag(v2f_img i) : SV_Target
            {
                if (tex2D(_MainTex, i.uv).r > 0.5) discard; // inside the object: the object itself shows

                float reach = 0;
                [unroll] for (int step = 1; step <= 3; step++)
                {
                    float2 d = _MainTex_TexelSize.xy * step;
                    float hit =
                        tex2D(_MainTex, i.uv + float2( d.x, 0)).r + tex2D(_MainTex, i.uv + float2(-d.x, 0)).r +
                        tex2D(_MainTex, i.uv + float2(0,  d.y)).r + tex2D(_MainTex, i.uv + float2(0, -d.y)).r +
                        tex2D(_MainTex, i.uv + float2( d.x,  d.y)).r + tex2D(_MainTex, i.uv + float2(-d.x,  d.y)).r +
                        tex2D(_MainTex, i.uv + float2( d.x, -d.y)).r + tex2D(_MainTex, i.uv + float2(-d.x, -d.y)).r;
                    if (hit > 0.5 && step <= _OutlineWidth) reach = 1;
                }
                if (reach < 0.5) discard;
                return _OutlineColor;
            }
            ENDCG
        }
    }
}
