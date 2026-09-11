Shader "Hidden/SC2/Texture3DSlicePreview"
{
    Properties { _Volume ("Volume", 3D) = "" {} _Slice ("Slice", Float) = 0.5 }
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler3D _Volume;
            float _Slice;
            fixed4 frag(v2f_img i) : SV_Target
            {
                return tex3D(_Volume, float3(i.uv, _Slice));
            }
            ENDCG
        }
    }
}
