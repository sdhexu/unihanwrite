Shader "Handwriting/Brush"
{
    // 圆形软边笔刷。使用顶点色携带颜色、UV.xy 为局部坐标 (-1..1)、UV.z 为软边阈值
    // 用 CGPROGRAM (UnityCG)，与渲染管线无关，确保在内置 / URP 下都能编译并被 Graphics.DrawMeshNow 使用。
    Properties
    {
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        ZTest Always
        ZWrite Off
        Cull Off
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
                float4 uv     : TEXCOORD0; // xy: 局部 -1..1, z: softness (0..1)
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float4 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 顶点已是 NDC 坐标 (-1..1)，直接输出到 clip 空间。
                // 不依赖 ObjectToClipPos / 任何 view-projection 矩阵，跨管线（内置/URP/HDRP）行为完全一致。
                o.pos = float4(v.vertex.x, v.vertex.y, 0.0, 1.0);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 距笔刷中心的距离 (0..1)
                float d = length(i.uv.xy);
                float softness = saturate(i.uv.z);
                // 软边过渡区间：[1 - softness, 1] -> alpha 从 1 到 0
                float inner = 1.0 - softness;
                float a = 1.0 - smoothstep(inner, 1.0, d);
                fixed4 c = i.color;
                c.a *= a;
                if (c.a <= 0.001) discard;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}