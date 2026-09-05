Shader "翼揽无余/Double Sided Standard"
{
    Properties
    {
        _Color ("颜色", Color) = (1,1,1,1)
        _MainTex ("主贴图", 2D) = "white" {}

        _MetallicGlossMap ("金属度贴图 (R) 光滑度 (A)", 2D) = "white" {}
        _Metallic ("金属度", Range(0,1)) = 0
        _Glossiness ("光滑度", Range(0,1)) = 0.5

        [Normal] _BumpMap ("法线贴图", 2D) = "bump" {}
        _BumpScale ("法线强度", Float) = 1

        _OcclusionMap ("环境遮蔽贴图 (G)", 2D) = "white" {}
        _OcclusionStrength ("环境遮蔽强度", Range(0,1)) = 1

        _EmissionMap ("自发光贴图", 2D) = "white" {}
        [HDR] _EmissionColor ("自发光颜色", Color) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }
        LOD 300

        // 关键设置：关闭背面剔除，让模型内侧也参与渲染。
        Cull Off

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _MetallicGlossMap;
        sampler2D _BumpMap;
        sampler2D _OcclusionMap;
        sampler2D _EmissionMap;

        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        half _BumpScale;
        half _OcclusionStrength;
        fixed4 _EmissionColor;

        struct Input
        {
            float2 uv_MainTex;
            float facing : VFACE;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 albedo = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            fixed4 metallicGloss = tex2D(_MetallicGlossMap, IN.uv_MainTex);
            fixed occlusion = tex2D(_OcclusionMap, IN.uv_MainTex).g;
            fixed3 emission = tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor.rgb;

            half3 tangentNormal = UnpackScaleNormal(
                tex2D(_BumpMap, IN.uv_MainTex), _BumpScale);

            // 背面需要反转法线，否则内侧通常会显得异常发黑。
            if (IN.facing < 0)
                tangentNormal = -tangentNormal;

            o.Albedo = albedo.rgb;
            o.Alpha = albedo.a;
            o.Metallic = metallicGloss.r * _Metallic;
            o.Smoothness = metallicGloss.a * _Glossiness;
            o.Normal = tangentNormal;
            o.Occlusion = lerp(1.0h, occlusion, _OcclusionStrength);
            o.Emission = emission;
        }
        ENDCG
    }

    FallBack "Standard"
}
