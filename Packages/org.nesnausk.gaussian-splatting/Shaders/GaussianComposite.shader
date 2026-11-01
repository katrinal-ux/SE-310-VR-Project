// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        Pass
        {
            Name "GaussianComposite"
            ZWrite Off
            // Occlusion against the scene: the fragment outputs the splats' alpha-weighted depth
            // and the hardware tests it against the camera depth attachment (per MSAA sample).
            // With _SplatDepthTest off it outputs the nearest depth, i.e. always passes.
            ZTest LEqual
            Cull Off
            // Premultiplied-alpha "over": the fragment emits an already-premultiplied colour.
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            // Never DXC: this shader picks the eye with unity_StereoEyeIndex, which under
            // multiview (Quest, Vulkan) is the placeholder cbuffer value OVR_multiview.gl_ViewID
            // (UnityInput.hlsl). Only Unity's FXC->HLSLcc path rewrites that into the hardware
            // view index; compiled with DXC it stays an unbound 0, so the right eye composited
            // the left eye's splats (zero disparity: the model looked far away and swam as the
            // head turned). The editor never shows this: D3D11 ignores use_dxc and uses instancing.
            #pragma never_use_dxc
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Splat accumulation per eye, one RGBA16F texture: (Y, chroma, depth, 1) * alpha with
            // YCoCg colour whose chroma is checkerboarded -- Co on even pixels ((x + y) & 1 == 0),
            // Cg on odd -- see RenderGaussianSplats.shader. In mono the eye-1 texture is the eye-0 one.
            TEXTURE2D(_GaussianSplatRT);
            TEXTURE2D(_GaussianSplatRTEye1);
            float _SplatDepthTest;

            #if UNITY_REVERSED_Z
                #define SPLAT_NEAREST_DEVICE_DEPTH 1.0
            #else
                #define SPLAT_NEAREST_DEVICE_DEPTH 0.0
            #endif

            float4 LoadSplatTexel(int2 p, bool eye1)
            {
                return eye1 ? LOAD_TEXTURE2D(_GaussianSplatRTEye1, p) : LOAD_TEXTURE2D(_GaussianSplatRT, p);
            }

            // Bilinear sample of the splat buffer that rebuilds each of the 2x2 texels' missing
            // chroma from its opposite-parity neighbours in the block before filtering (plain
            // bilinear filtering would mix Co into Cg). Neighbours are weighted by coverage and by
            // how close their luma is, so chroma does not bleed across edges. Premultiplied values
            // are un-premultiplied for that, so partially covered neighbours contribute correctly.
            void SampleSplats(float2 uv, out half4 col, out float weightedDepth)
            {
                // The draw is instanced per eye by URP's single-pass XR setup (or broadcast by
                // multiview), so the eye index is valid here, unlike in a raw per-slice blit.
                bool eye1 = false;
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                eye1 = unity_StereoEyeIndex != 0;
                #endif
                float2 size;
                if (eye1) _GaussianSplatRTEye1.GetDimensions(size.x, size.y);
                else _GaussianSplatRT.GetDimensions(size.x, size.y);

                float2 st = uv * size - 0.5;
                float2 base = floor(st);
                float2 f = st - base;
                int2 maxP = (int2)size - 1;
                int2 p[4];
                p[0] = clamp((int2)base, 0, maxP);
                p[1] = clamp((int2)base + int2(1, 0), 0, maxP);
                p[2] = clamp((int2)base + int2(0, 1), 0, maxP);
                p[3] = clamp((int2)base + int2(1, 1), 0, maxP);

                float4 t[4];
                uint parity[4];
                float luma[4];
                [unroll] for (uint k = 0; k < 4; k++)
                {
                    t[k] = LoadSplatTexel(p[k], eye1);
                    parity[k] = (uint)(p[k].x + p[k].y) & 1;
                    luma[k] = t[k].x / max(t[k].w, 1e-4);
                }

                float4 rgba = 0;
                weightedDepth = 0;
                float w[4] = { (1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y };
                [unroll] for (uint k2 = 0; k2 < 4; k2++)
                {
                    // Block neighbours: 0 and 3 touch 1 and 2, and vice versa. Clamping at the
                    // image border can repeat a texel, hence the explicit parity check.
                    uint n0 = (k2 == 0 || k2 == 3) ? 1 : 0;
                    uint n1 = (k2 == 0 || k2 == 3) ? 2 : 3;
                    float wsum = 0, csum = 0;
                    [unroll] for (uint j = 0; j < 2; j++)
                    {
                        uint n = j == 0 ? n0 : n1;
                        if (parity[n] != parity[k2] && t[n].w > 1.0 / 255.0)
                        {
                            float wn = t[n].w / (abs(luma[n] - luma[k2]) + 0.05);
                            wsum += wn;
                            csum += wn * (t[n].y / t[n].w);
                        }
                    }
                    float otherChroma = wsum > 0 ? csum / wsum * t[k2].w : 0;
                    float co = parity[k2] == 0 ? t[k2].y : otherChroma;
                    float cg = parity[k2] == 0 ? otherChroma : t[k2].y;
                    float y = t[k2].x;
                    float3 rgb = float3(y - cg + co, y + cg, y - cg - co);
                    rgba += w[k2] * float4(rgb, t[k2].w);
                    weightedDepth += w[k2] * t[k2].z;
                }
                col = (half4)rgba;
            }

            half4 frag (Varyings input, out float outDepth : SV_Depth) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 col;
                float weightedDepth;
                SampleSplats(input.texcoord, col, weightedDepth);
                if (col.a < 1.0 / 255.0)
                    discard;
                // Rebuilt chroma can overshoot a channel slightly below 0; SRGBToLinear of a
                // negative is NaN.
                col.rgb = max(col.rgb, 0);

                // Depth of the splat "surface" at this pixel: the view depth of each splat
                // centre weighted by its contribution, which is how 3DGS renders depth. Converted
                // back to the device depth this camera's depth buffer stores.
                outDepth = SPLAT_NEAREST_DEVICE_DEPTH;
                float eyeDepth = weightedDepth / col.a;
                if (_SplatDepthTest > 0.5 && eyeDepth > 0 && unity_OrthoParams.w == 0)
                    outDepth = saturate((rcp(eyeDepth) - _ZBufferParams.w) / _ZBufferParams.z);

                // 3DGS SH colours are sRGB-encoded (training compares SH*0.282+0.5 directly
                // against sRGB photos), and the splat buffer accumulates them in that space:
                //     C = sum(c_i * a_i * T_i),  A = 1 - prod(1 - a_i)
                // The reference 3DGS result over a background B is, in DISPLAY space,
                //     out = C + (1 - A) * B
                //
                // Un-premultiplying first and re-multiplying after the transfer function --
                // SRGBToLinear(C/A) * A -- is NOT the same thing whenever A < 1, because the
                // transfer function is non-linear. Measured against a CPU reference: a splat
                // of colour 1.0 at coverage 0.5 came out at 0.735 instead of 0.500, and 0.25
                // coverage gave 0.537 instead of 0.250. That error lands on every silhouette
                // edge, which is what makes composited splats need "alpha edge cleanup".
                //
                // Converting the PREMULTIPLIED value and using premultiplied-alpha blending
                // (Blend One OneMinusSrcAlpha) reproduces 3DGS exactly over a black
                // background -- the dataset-generation case -- and degrades gracefully to the
                // standard "over" operator against arbitrary scene content.
                half3 rgb = col.rgb;
                #if !UNITY_COLORSPACE_GAMMA
                rgb = SRGBToLinear(rgb);
                #endif

                return half4(rgb, col.a);
            }
            ENDHLSL
        }
    }
}
