Shader "Hidden/SC2/TextureChannelPreview"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _Channel ("Channel", Float) = 0 }
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
            sampler2D _MainTex;
            float _Channel;
            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                if (_Channel < 1.5) return fixed4(c.rgb, 1);
                if (_Channel < 2.5) return fixed4(c.rrr, 1);
                if (_Channel < 3.5) return fixed4(c.ggg, 1);
                if (_Channel < 4.5) return fixed4(c.bbb, 1);
                return fixed4(c.aaa, 1);
            }
            ENDCG
        }
    }
}
