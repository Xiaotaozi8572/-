Shader "翼揽无余/UI/Alpha Packed Video"
{
    Properties
    {
        [PerRendererData] _MainTex ("Video Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Toggle] _ColorOnTop ("Color Is On Top", Float) = 1
        _AlphaGamma ("Alpha Gamma", Range(0.1, 3)) = 1
        _AlphaThreshold ("Alpha Threshold", Range(0, 1)) = 0
        _AlphaSoftness ("Alpha Edge Softness", Range(0.001, 0.25)) = 0.01

        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "AlphaPackedVideoUI"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;
            float _ColorOnTop;
            float _AlphaGamma;
            float _AlphaThreshold;
            float _AlphaSoftness;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float colorOffset = _ColorOnTop > 0.5 ? 0.5 : 0.0;
                float alphaOffset = _ColorOnTop > 0.5 ? 0.0 : 0.5;

                float2 colorUV = float2(i.texcoord.x, i.texcoord.y * 0.5 + colorOffset);
                float2 alphaUV = float2(i.texcoord.x, i.texcoord.y * 0.5 + alphaOffset);

                fixed4 result = tex2D(_MainTex, colorUV) * i.color;
                float alpha = tex2D(_MainTex, alphaUV).r;
                alpha = pow(saturate(alpha), max(_AlphaGamma, 0.001));
                alpha = smoothstep(_AlphaThreshold,
                    min(1.0, _AlphaThreshold + _AlphaSoftness), alpha);
                result.a *= alpha;

                #ifdef UNITY_UI_CLIP_RECT
                result.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(result.a - 0.001);
                #endif

                return result;
            }
            ENDCG
        }
    }
}
