Shader "UI/TutorialMask"
{
    Properties
    {
        [HideInInspector] _MainTex ("Texture", 2D) = "white" {}
        _DarkColor ("暗色", Color) = (0, 0, 0, 0.65)
        _HoleCenter ("挖孔中心(屏幕像素)", Vector) = (0, 0, 0, 0)
        _HoleSize ("挖孔尺寸(屏幕像素,含边距)", Vector) = (0, 0, 0, 0)
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
            float2 _HoleCenter;
            float2 _HoleSize;
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
                // 屏幕像素坐标（全屏 Image：uv(0..1) * 屏幕像素 = 屏幕像素）
                float2 px = i.uv * _ScreenParams.xy;
                fixed4 col = _DarkColor;
                if (_HoleEnabled > 0.5 && _HoleSize.x > 0.0 && _HoleSize.y > 0.0)
                {
                    float2 half = _HoleSize * 0.5;
                    if (abs(px.x - _HoleCenter.x) <= half.x && abs(px.y - _HoleCenter.y) <= half.y)
                        col.a = 0.0; // 挖孔：中间保持原样（透明）
                }
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
