// GPU sculpt brush — runs as Graphics.Blit on RFloat heightmap RT.
// 0 CPU work. Handles Raise/Lower/Smooth/Flatten/Noise in one pass.
Shader "Hidden/SC2/HeightBrush"
{
    Properties { _MainTex("",2D)="black"{} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float2 _BrushUV;      // brush centre in heightmap UV space
            float  _BrushRadius;  // in UV space
            float  _BrushStrength;
            int    _BrushMode;    // 0=Raise 1=Lower 2=Smooth 3=Flatten 4=Noise
            float  _FlattenTarget;

            float falloff(float2 uv){
                float d=length(uv-_BrushUV)/_BrushRadius;
                return d>=1?0:cos(d*UNITY_PI*.5);
            }
            float noise(float2 p){
                float2 i=floor(p*64);
                float h00=frac(sin(dot(i,            float2(127.1,311.7)))*43758.5);
                float h10=frac(sin(dot(i+float2(1,0),float2(127.1,311.7)))*43758.5);
                float h01=frac(sin(dot(i+float2(0,1),float2(127.1,311.7)))*43758.5);
                float h11=frac(sin(dot(i+float2(1,1),float2(127.1,311.7)))*43758.5);
                float2 f=smoothstep(0,1,frac(p*64));
                return lerp(lerp(h00,h10,f.x),lerp(h01,h11,f.x),f.y)-.5;
            }
            float4 frag(v2f_img i):SV_Target{
                float h=tex2D(_MainTex,i.uv).r;
                float fo=falloff(i.uv);
                if(fo<=0)return h;
                float a=fo*_BrushStrength;
                float2 ts=_MainTex_TexelSize.xy;
                if(_BrushMode==0) h+=a;
                else if(_BrushMode==1) h-=a;
                else if(_BrushMode==2){
                    float avg=(tex2D(_MainTex,i.uv+float2(ts.x,0)).r+
                               tex2D(_MainTex,i.uv-float2(ts.x,0)).r+
                               tex2D(_MainTex,i.uv+float2(0,ts.y)).r+
                               tex2D(_MainTex,i.uv-float2(0,ts.y)).r)*.25;
                    h=lerp(h,avg,a*.7);
                }
                else if(_BrushMode==3) h=lerp(h,_FlattenTarget,a);
                else if(_BrushMode==4) h+=noise(i.uv)*a*2;
                return saturate(h);
            }
            ENDCG
        }
    }
}
