// Invisible depth-only material used for Magic Leap 2 world-mesh occlusion.
// Renders before regular geometry, writes to the depth buffer, emits no color.
// Real surfaces (floor, shoes, etc.) that produce mesh chunks will therefore
// occlude any opaque virtual content drawn afterwards.
Shader "AdaptiveCueing/DepthOccluder"
{
    SubShader
    {
        Tags
        {
            "RenderType"   = "Opaque"
            "Queue"        = "Geometry-1"
            "IgnoreProjector" = "True"
            "DisableBatching" = "True"
        }

        LOD 100

        Pass
        {
            Name "DepthOnly"

            ZWrite On
            ZTest  LEqual
            ColorMask 0
            Cull Back
            Offset -1, -1

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
            };

            v2f vert (appdata_t v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }

    Fallback Off
}
