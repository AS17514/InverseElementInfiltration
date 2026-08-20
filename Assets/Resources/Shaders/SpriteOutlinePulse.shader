Shader "TheLaw/SpriteOutlinePulse"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (1,0.62,0.08,1)
        _OutlineSize ("Outline Size", Range(0.5,4)) = 1.25
        _Pulse ("Pulse", Range(0,1)) = 0.5
        _Flash ("Flash", Range(0,1)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            fixed4 _OutlineColor;
            float _OutlineSize;
            float _Pulse;
            float _Flash;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 sprite = tex2D(_MainTex, input.uv) * input.color;
                float2 texel = _MainTex_TexelSize.xy * _OutlineSize;

                fixed neighbourAlpha = 0;
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2( texel.x, 0)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2(-texel.x, 0)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2(0,  texel.y)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2(0, -texel.y)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2( texel.x,  texel.y)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2(-texel.x, -texel.y)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2( texel.x, -texel.y)).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, input.uv + float2(-texel.x,  texel.y)).a);

                fixed outlineAlpha = saturate(neighbourAlpha - sprite.a)
                    * _OutlineColor.a * saturate(_Pulse);
                fixed3 spriteRgb = lerp(sprite.rgb, fixed3(1, 1, 1), saturate(_Flash));

                // Opaque sprite pixels keep original art. Outline only occupies transparent pixels.
                fixed finalAlpha = max(sprite.a, outlineAlpha);
                fixed3 finalRgb = sprite.a > 0.001 ? spriteRgb : _OutlineColor.rgb;
                return fixed4(finalRgb, finalAlpha);
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
