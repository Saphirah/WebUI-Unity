Shader "WebUI/PremultipliedOverlay" {
 Properties { [PerRendererData] _MainTex("Browser",2D)="black"{} _Color("Tint",Color)=(1,1,1,1) }
 SubShader {
  Tags {"Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True"}
  Cull Off ZWrite Off ZTest Always Blend One OneMinusSrcAlpha
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   struct v2f {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   sampler2D _MainTex;float4 _Color;
   v2f vert(appdata v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=float2(v.uv.x,1-v.uv.y);o.color=v.color*_Color;return o;}
   float4 frag(v2f i):SV_Target {float4 c=tex2D(_MainTex,i.uv);
    #ifndef UNITY_COLORSPACE_GAMMA
    c.rgb=GammaToLinearSpace(c.rgb/max(c.a,0.00001))*c.a;
    #endif
    c.rgb*=i.color.rgb*i.color.a;c.a*=i.color.a;return c;
   }
   ENDCG
  }
 }
}
