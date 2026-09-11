Shader "Hidden/SC2/CubemapFacePreview"
{
    Properties { _Cube ("Cubemap", CUBE) = "" {} _Face ("Face", Float) = 0 }
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
            samplerCUBE _Cube;
            float _Face;
            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 p = i.uv * 2 - 1;
                float3 dir;
                if (_Face < 0.5)      dir = float3(1,    -p.y, -p.x);
                else if (_Face < 1.5) dir = float3(-1,   -p.y,  p.x);
                else if (_Face < 2.5) dir = float3(p.x,   1,    p.y);
                else if (_Face < 3.5) dir = float3(p.x,  -1,   -p.y);
                else if (_Face < 4.5) dir = float3(p.x,  -p.y,  1);
                else                  dir = float3(-p.x, -p.y, -1);
                return texCUBE(_Cube, dir);
            }
            ENDCG
        }
    }
}
