// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            ZTest Always
            // Back-to-front premultiplied "over". For colour this produces the same result as
            // front-to-back "under" (C = sum c_i a_i T_i, A = 1 - prod(1 - a_i)) without the fp16
            // precision loss "under" suffers as dst.a approaches 1. The blue channel accumulates
            // the alpha-weighted view depth sum d_i a_i T_i; the composite divides by A to get
            // the depth it tests against the scene's depth buffer.
            Blend One OneMinusSrcAlpha
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float depth : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
StructuredBuffer<uint> _SplatVisibleCount; // [0] = splats in draw order that are visible
uint _ViewDataOffset; // stereo XR: per-eye offset into the (doubled) view-data buffer
float4 _SplatRTSize;  // xy = accumulation target size in pixels (matches the compute's screen params)

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    // Batched quads: instance = batch of _SplatsPerBatch splats, vertex id = 4 * splat + corner.
    uint slot = instID * _SplatsPerBatch + (vtxID >> 2);
    bool pastEnd = slot >= _SplatVisibleCount[0];
    uint splatIdx = _OrderBuffer[pastEnd ? 0 : slot];
	SplatViewData view = _SplatViewData[splatIdx + _ViewDataOffset];
	float4 centerClipPos = view.pos;
	bool behindCam = pastEnd || centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID & 3;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= SplatQuadExtent(o.col.a);

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _SplatRTSize.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;
		o.depth = centerClipPos.w; // view-space depth of the splat centre
	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

// One RGBA16F target holds colour, coverage and depth: (Y, chroma, depth, 1) * alpha, where
// colour is YCoCg with the two chroma channels checkerboarded -- Co on even pixels, Cg on odd --
// and rebuilt from the neighbours in the composite. YCoCg is a linear transform of RGB, so the
// premultiplied "over" blend stays exact per channel. A second (depth) render target would cost
// about a third of the whole splat draw on desktop GPUs, whatever its format.
half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}

    if (alpha < 1.0/255.0)
        discard;

    half3 rgb = i.col.rgb;
    half luma = dot(rgb, half3(0.25, 0.5, 0.25));
    uint2 px = (uint2)i.vertex.xy;
    half chroma = ((px.x + px.y) & 1) == 0
        ? dot(rgb, half3(0.5, 0.0, -0.5))    // Co
        : dot(rgb, half3(-0.25, 0.5, -0.25)); // Cg
    return half4(luma * alpha, chroma * alpha, i.depth * alpha, alpha);
}
ENDCG
        }
    }
}
