// Paint brush for pair assignment and RNM blend.
// NO GPU BLEND — shader reads old value and returns new or old explicitly.
// This prevents accumulation artifacts.
Shader "Hidden/SC2/PaintBrush"
{
    Properties { _MainTex("Source RT", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        // NO Blend — we output exact value, no GPU blending
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float2 _BrushUV;        // brush centre in UV space
            float  _BrushRadius;    // radius in UV space
            float  _BrushStrength;  // 0..1 (used for soft edge threshold)
            float4 _PaintColor;     // target value to write
            int    _PaintMode;      // 0=paint(hard) 1=erase 2=soft(RNM blend)

            // Optional custom stamp for Soft-blend (RNM) painting only — Pair
            // assign/erase (modes 0/1) always keep the built-in round brush
            // below, untouched. _BrushTex's own alpha channel supplies the
            // per-pixel stamp strength (a round brush is just a PNG with a
            // circular alpha to begin with, so this generalizes the existing
            // shape rather than replacing it).
            sampler2D _BrushTex;
            float     _UseCustomBrush; // 0 = procedural round brush, 1 = sample _BrushTex
            // Per-stroke randomized stamp rotation (radians) — SC2PaintMode
            // rolls a fresh angle once per stroke (mouse-down), not per drag
            // frame, and holds it fixed for the frag call the whole time the
            // mouse is down.
            float     _BrushRotation;
            // A symmetric copy of a stroke on a REFLECTING symmetry slot (X/Y/
            // diagonal mirrors — see SC2SymmetryMath.GetSlotMirror) must show
            // the mirror image of the stamp, not an identical copy — a plain
            // rotation (_BrushRotation above) can never reproduce a reflection
            // (opposite handedness). (1,1)/false for slot 0 and every
            // ROTATION-only symmetric copy (radial, the 180-degree slot of
            // XY/DiagonalBoth), which keeps the stamp unmirrored there —
            // GetSlotRotationDegrees handles turning those instead.
            float2    _BrushMirror;
            float     _BrushSwapAxes;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 src    = tex2D(_MainTex, i.uv);
                float2 offset = (i.uv - _BrushUV) / _BrushRadius;

                if (_PaintMode == 2 && _UseCustomBrush > 0.5)
                {
                    // The stamp's own square footprint reaches its corners at
                    // sqrt(2)*radius, visibly outside the circular radius ring
                    // every other brush respects, so it must be clipped to
                    // that SAME circle first, on the unrotated offset
                    // (the boundary itself must not rotate with the stamp
                    // content) — the square-then-luminance sampling below
                    // only shapes the pattern WITHIN that circle now.
                    if (length(offset) >= 1.0)
                        return src;

                    // Mirror the sample offset (world-space, BEFORE the
                    // per-stroke rotation below) for a reflecting symmetric
                    // copy, so its content turns the opposite way from slot
                    // 0's — the same relationship SlotReflects/TurnsOpposite
                    // already describe for every other domain's own object
                    // rotation.
                    float2 mirrored = _BrushSwapAxes > 0.5 ? offset.yx : offset;
                    mirrored *= _BrushMirror;

                    // Rotate the sample offset about the brush centre before
                    // mapping to the stamp UV, so the stamp's CONTENT rotates
                    // (the circular clip above stays fixed).
                    float2 rotOffset = mirrored;
                    if (abs(_BrushRotation) > 0.0001)
                    {
                        float s = sin(_BrushRotation), c = cos(_BrushRotation);
                        rotOffset = float2(mirrored.x * c - mirrored.y * s, mirrored.x * s + mirrored.y * c);
                    }
                    if (abs(rotOffset.x) > 1.0 || abs(rotOffset.y) > 1.0)
                        return src;
                    // The stamp texture's row order runs opposite the brush's own world-space Z
                    // (north/+Z is the bottom row, not the top), so the V axis is flipped here,
                    // once, after every other transform — same fix in SC2TerrainRuntime.
                    // SampleBrushMask (the sculpt tools' CPU-side stamp sampler) for the identical
                    // reason.
                    float2 stampUv = float2(rotOffset.x, -rotOffset.y) * 0.5 + 0.5;
                    // A plain grayscale stamp image (no real alpha channel —
                    // the common World Machine/Gaea mask export convention)
                    // decodes with alpha=1 UNIFORMLY, its real shape data
                    // sitting in RGB instead — sampling alpha alone reads a
                    // flat, fully-opaque square. Luminance covers that case
                    // correctly; multiplying by alpha too keeps a genuine
                    // alpha-transparency brush (solid paint on a cut-out
                    // alpha shape) working exactly as before.
                    float4 stampSample = tex2D(_BrushTex, stampUv);
                    float  luminance   = dot(stampSample.rgb, float3(0.299, 0.587, 0.114));
                    float  amt         = luminance * stampSample.a * _BrushStrength;
                    return lerp(src, _PaintColor, amt);
                }

                float d = length(offset);
                if (d >= 1.0)
                    return src;  // outside brush — keep existing value exactly

                float fo = cos(d * UNITY_PI * 0.5); // cosine falloff 0..1

                if (_PaintMode == 0) // Hard paint — replace fully inside brush
                {
                    // Hard edge: threshold at _BrushStrength
                    // Within radius AND strength threshold → write new value
                    if (fo >= (1.0 - _BrushStrength))
                        return _PaintColor;
                    return src;
                }
                else if (_PaintMode == 1) // Erase — write 255/255 (unassigned = -1)
                {
                    if (fo >= (1.0 - _BrushStrength))
                        return float4(1, 1, 1, 1); // 255 = unassigned
                    return src;
                }
                else // Soft blend (RNM alpha painting)
                {
                    float amt    = fo * _BrushStrength;
                    float4 result = lerp(src, _PaintColor, amt);
                    return result;
                }
            }
            ENDCG
        }
    }
}
