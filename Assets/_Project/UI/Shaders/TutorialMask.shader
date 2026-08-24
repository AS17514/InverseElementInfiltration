Shader "UI/TutorialMask"
{
    Properties
    {
        [HideInInspector] _MainTex ("Texture", 2D) = "white" {}
        _DarkColor ("暗色", Color) = (0, 0, 0, 0.65)
        _HoleRect ("挖孔区域(像素 xMin,yMin,xMax,yMax)", Vector) = (0, 0, 0, 0)
        _Padding ("外扩边距(像素)", Float) = 20
        _HoleEnabled ("启用挖孔", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _DarkColor;
            float4 _HoleRect;
            float _Padding;
            float _HoleEnabled;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // ScreenSpaceOverlay 全屏 Image: uv(0..1) * 屏幕像素 = 像素坐标
                float2 px = i.uv * _ScreenParams.xy;
                fixed4 col = _DarkColor;
                if (_HoleEnabled > 0.5)
                {
                    float4 r = _HoleRect + float4(-_Padding, -_Padding, _Padding, _Padding);
                    if (px.x >= r.x && px.x <= r.z && px.y >= r.y && px.y <= r.w)
                        col.a = 0.0; // 挖孔：中间保持原样（透明）
                }
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
