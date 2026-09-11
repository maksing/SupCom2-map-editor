// GPU-instanced viewport pick-dot billboard for Props/Units/Markers
// (SC2ObjectMarkerRenderer.cs). Replaces per-object IMGUI GUI.DrawTexture
// calls (2 per object, reported directly as a real bottleneck at 2871
// props: ~5742 draw calls + matrix pushes every Repaint) with a handful of
// Graphics.DrawMeshInstanced calls, one per category batch (Normal/
// Selected/Invalid/Hover per kind).
//
// Constant-screen-size billboard technique: the shared quad mesh's local
// vertices only carry a +-1 "which corner" sign, never an actual world-space
// offset. The vertex shader projects the INSTANCE'S PIVOT (object-space
// origin, since every instance matrix is translation-only — see
// SC2ObjectMarkerRenderer.BuildTranslationMatrix) to clip space once, then
// nudges that single clip-space point by a pixel-space offset (derived from
// _ScreenParams) BEFORE the hardware's perspective divide — the standard
// "billboard stays N pixels wide regardless of camera distance" trick. No
// per-frame CPU work is needed when only the camera moves: the GPU
// re-projects the same instance-matrix array against the new VP matrix on
// its own.
Shader "Hidden/SC2/ObjectMarker"
{
    Properties
    {
        _Color ("Outer Color", Color) = (0.25, 0.86, 1, 0.9)
        _InnerColor ("Inner Color", Color) = (0.04, 0.05, 0.07, 1)
        _PixelSize ("Pixel Size", Float) = 9
        _InnerRatio ("Inner Ratio", Range(0,1)) = 0.55
        // 2026-08-23: per-category Props icons (Trees/Rocks/Crates/
        // Buildings) — when _UseIcon is set, the fragment shader samples
        // _IconTex instead of drawing the procedural diamond, using the
        // same instance/billboard machinery (one real texture swap per
        // batch's shared Material, same as _Color already is — no
        // per-instance data needed). Every other batch (props with no
        // special icon, units, markers, selection) leaves _UseIcon at its
        // default 0 and renders exactly as before.
        _IconTex ("Icon", 2D) = "white" {}
        _UseIcon ("Use Icon", Float) = 0
    }
    SubShader
    {
        // Always-on-top, like the IMGUI dots it replaces (drawn in OnGUI,
        // over the fully-rendered 3D scene) — ZTest Always + no ZWrite so
        // markers never get occluded by terrain/props and never occlude
        // real geometry either.
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 local : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float _PixelSize;
            fixed4 _Color;
            fixed4 _InnerColor;
            float _InnerRatio;
            sampler2D _IconTex;
            float _UseIcon;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                // Object-space origin = the instance's world pivot, since
                // every submitted matrix is Matrix4x4.Translate(worldPos)
                // (no rotation/scale — see this shader's own header note).
                float4 pivotWorld = mul(unity_ObjectToWorld, float4(0, 0, 0, 1));
                float4 pivotClip = mul(UNITY_MATRIX_VP, pivotWorld);

                // Behind the camera (w<=0): pushing this vertex far outside
                // the clip volume lets the hardware's own near/far clipping
                // drop it cleanly, instead of the perspective divide
                // wrapping a negative-w point back onto the visible screen.
                if (pivotClip.w <= 0.0001)
                {
                    o.pos = float4(0, 0, -1e6, 1);
                    o.local = 0;
                    return o;
                }

                float2 pixelOffset = v.vertex.xy * (_PixelSize * 0.5);
                float2 ndcOffset = pixelOffset / (_ScreenParams.xy * 0.5);
                float4 clip = pivotClip;
                // Offset applied pre-divide (scaled by w) so it lands
                // correctly in NDC space once the hardware divides by w.
                clip.xy += ndcOffset * clip.w;
                o.pos = clip;
                o.local = v.vertex.xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_UseIcon > 0.5)
                {
                    // 2026-08-23 fix: confirmed by direct in-game screenshot —
                    // Props category icons (Trees etc.) rendered upside down
                    // without this V-flip. i.local.y follows the quad's own
                    // local up axis; this billboard's screen-space "up" ends
                    // up inverted relative to that by the time it reaches
                    // here, so the source PNG's top row must map to the
                    // quad's local.y=+1 (not -1) to read right-side up.
                    float2 uv = float2(i.local.x * 0.5 + 0.5, 0.5 - i.local.y * 0.5);
                    fixed4 iconColor = tex2D(_IconTex, uv);
                    clip(iconColor.a - 0.01);
                    return iconColor;
                }
                // Diamond shape (matches the 45-degree-rotated square the
                // IMGUI DrawViewportGlyph drew) via a simple L1-distance
                // test, with a smaller solid inner diamond for the same
                // two-tone look.
                float d = abs(i.local.x) + abs(i.local.y);
                clip(1.0 - d);
                return d <= _InnerRatio ? _InnerColor : _Color;
            }
            ENDCG
        }
    }
}
