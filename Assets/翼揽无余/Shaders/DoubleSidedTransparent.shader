Shader "翼揽无余/Double Sided Transparent"
{
    Properties
    {
        _Color ("颜色（A控制透明度）", Color) = (0.75,0.9,1,0.25)
        _MainTex ("主贴图（A控制透明度）", 2D) = "white" {}
        _Metallic ("金属度", Range(0,1)) = 0
        _Glossiness ("光滑度", Range(0,1)) = 0.9
        [Normal] _BumpMap ("法线贴图", 2D) = "bump" {}
        _BumpScale ("法线强度", Float) = 1
        [HDR] _EmissionColor ("自发光颜色", Color) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Cull Off
        ZWrite Off

        CGPROGRAM
        #pragma surface surf Standard alpha:fade fullforwardshadows keepalpha
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        half _BumpScale;
        fixed4 _EmissionColor;

        struct Input
        {
            float2 uv_MainTex;
            float facing : VFACE;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 glass = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            half3 tangentNormal = UnpackScaleNormal(
                tex2D(_BumpMap, IN.uv_MainTex), _BumpScale);

            if (IN.facing < 0)
                tangentNormal = -tangentNormal;

            o.Albedo = glass.rgb;
            o.Alpha = glass.a;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Normal = tangentNormal;
            o.Emission = _EmissionColor.rgb;
        }
        ENDCG
    }

    FallBack "Transparent/Diffuse"
}
