// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    // Splats render in two passes so they never break URP's main render pass:
    //
    //  1. Accumulate (before any camera rendering): view data, one shared depth sort across all
    //     splat objects, and the splat draw into one offscreen target per eye holding premultiplied
    //     colour (YCoCg, checkerboarded chroma), coverage and the alpha-weighted view depth.
    //     Nothing here touches the camera's targets.
    //  2. Composite (a raster pass after opaques): a fullscreen draw into the camera colour with
    //     the camera depth bound read-only. It outputs the splats' depth and the hardware depth
    //     test hides them behind opaque geometry. Being a plain raster pass on the camera's own
    //     attachments, it merges into the main native render pass on tile-based GPUs (Quest):
    //     the 4x MSAA colour and depth stay in tile memory instead of being stored and reloaded
    //     around an unsafe pass that reads and writes them.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        [Tooltip("Resolution of the splat buffers relative to the camera target. Splats are soft, so 0.7-0.8 is usually hard to tell apart from 1 in a headset while cutting splat fill cost roughly in half.")]
        [Range(0.5f, 1f)] public float m_ResolutionScale = 1f;

        [Tooltip("Hide splats behind opaque scene geometry using the camera depth buffer.")]
        public bool m_DepthTestAgainstScene = true;

        // Format of the splat accumulation buffer: (Y, chroma, depth, 1) * alpha. fp16 keeps the
        // premultiplied colour sum and coverage precise under many layers of blending, and resolves
        // the alpha-weighted view depth to ~1 mm at 1-2 m, plenty for occlusion. Depth shares this
        // target instead of a second render target, which cost about a third of the splat draw.
        internal static GraphicsFormat s_AccumFormat = GraphicsFormat.R16G16B16A16_SFloat;

        // Hands the per-eye splat targets from the accumulate pass to the composite pass.
        class SplatFrameData : ContextItem
        {
            internal TextureHandle color0, color1;
            internal Material composite;
            internal bool valid;

            public override void Reset()
            {
                color0 = color1 = TextureHandle.nullHandle;
                composite = null;
                valid = false;
            }
        }

        class AccumulatePass : ScriptableRenderPass
        {
            static readonly ProfilingSampler s_Sampler = new("GaussianSplatAccumulate");

            // One-shot so the multi-pass diagnostic below doesn't spam every frame.
            static bool s_WarnedMultiPass;

            internal float resolutionScale = 1f;

            class PassData
            {
                internal Camera camera;
                internal SplatViews views;
                internal TextureHandle color0, color1;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var splatData = frameData.GetOrCreate<SplatFrameData>();
                splatData.Reset();

                var cam = cameraData.camera;
                bool isStereo = cam.stereoEnabled;

                // The stereo path renders each eye into its own texture and the composite picks
                // the texture by eye index, which relies on single-pass (instanced / multiview)
                // rendering. Under multi-pass XR the pipeline runs once per eye and both eyes
                // would get the left eye's splats. Fail loudly rather than misrender silently:
                // set Project Settings > XR Plug-in Management > OpenXR > Render Mode to
                // "Single Pass Instanced" for this build target.
                if (isStereo && !s_WarnedMultiPass &&
                    XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass)
                {
                    s_WarnedMultiPass = true;
                    Debug.LogError("[GaussianSplat] OpenXR Render Mode is Multi Pass; the splat " +
                                   "composite requires Single Pass Instanced. Switch Render Mode " +
                                   "to Single Pass Instanced for this build target.");
                }

                // Per-eye size: in XR the camera descriptor already describes one eye, but use the
                // XR eye texture size when available, as the headset's native per-eye resolution.
                var camDesc = cameraData.cameraTargetDescriptor;
                int width = camDesc.width, height = camDesc.height;
                if (isStereo && XRSettings.eyeTextureWidth > 0)
                {
                    width = XRSettings.eyeTextureWidth;
                    height = XRSettings.eyeTextureHeight;
                }
                width = Mathf.Max(1, Mathf.RoundToInt(width * resolutionScale));
                height = Mathf.Max(1, Mathf.RoundToInt(height * resolutionScale));

                TextureHandle NewTarget(GraphicsFormat format, string name) =>
                    renderGraph.CreateTexture(new TextureDesc(width, height)
                    {
                        colorFormat = format,
                        dimension = TextureDimension.Tex2D,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        clearBuffer = false, // cleared by SetRenderTarget below
                        name = name,
                    });

                using var builder = renderGraph.AddUnsafePass("GaussianSplatAccumulate", out PassData passData, s_Sampler);
                passData.camera = cam;
                passData.views = SplatViews.ForCamera(cam, isStereo, new Vector2(width, height));
                passData.color0 = NewTarget(s_AccumFormat, "_GaussianSplatRT");
                passData.color1 = isStereo ? NewTarget(s_AccumFormat, "_GaussianSplatRTEye1") : passData.color0;

                builder.UseTexture(passData.color0, AccessFlags.Write);
                if (isStereo)
                    builder.UseTexture(passData.color1, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    var system = GaussianSplatRenderSystem.instance;
                    system.PrepareSplats(cmd, data.camera, data.views);

                    for (int eye = 0; eye < data.views.eyeCount; eye++)
                    {
                        RenderTargetIdentifier color = eye == 0 ? data.color0 : data.color1;
                        // Our own texture: FlipProjectionIfBackbuffer must not flip.
                        cmd.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, color);
                        // No depth buffer is bound; occlusion is resolved later, in the composite.
                        CoreUtils.SetRenderTarget(cmd, color, ClearFlag.Color, Color.clear);
                        system.DrawSplats(cmd, eye, data.views);
                    }
                });

                Material composite = GaussianSplatRenderSystem.instance.FindCompositeMaterial();
                splatData.color0 = passData.color0;
                splatData.color1 = passData.color1;
                splatData.composite = composite;
                splatData.valid = composite != null;
            }
        }

        class CompositePass : ScriptableRenderPass
        {
            static readonly ProfilingSampler s_Sampler = new("GaussianSplatComposite");

            internal bool depthTest = true;

            class PassData
            {
                internal TextureHandle color0, color1, target;
                internal Material material;
                internal float depthTest;
                internal MaterialPropertyBlock props;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<SplatFrameData>())
                    return;
                var splatData = frameData.Get<SplatFrameData>();
                if (!splatData.valid)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                using var builder = renderGraph.AddRasterRenderPass("GaussianSplatComposite", out PassData passData, s_Sampler);
                passData.color0 = splatData.color0;
                passData.color1 = splatData.color1;
                passData.target = resourceData.activeColorTexture;
                passData.material = splatData.composite;
                passData.depthTest = depthTest ? 1f : 0f;
                passData.props ??= new MaterialPropertyBlock();

                builder.UseTexture(passData.color0);
                if (!passData.color1.Equals(passData.color0))
                {
                    builder.UseTexture(passData.color1);
                }
                // ReadWrite: the composite blends over the scene, so the existing colour must load.
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                // Must match the opaque passes or the native render pass cannot merge.
                builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    // Splat targets are rendered like any render texture; flip only when the
                    // destination has the opposite UV origin (e.g. a backbuffer), as URP's own blits do.
                    bool yFlip = context.GetTextureUVOrigin(data.color0) != context.GetTextureUVOrigin(data.target);
                    var scaleBias = yFlip ? new Vector4(1, -1, 0, 1) : new Vector4(1, 1, 0, 0);

                    var props = data.props;
                    props.Clear();
                    props.SetTexture(GaussianSplatRenderer.Props.GaussianSplatRT, (RTHandle)data.color0);
                    props.SetTexture(GaussianSplatRenderer.Props.GaussianSplatRTEye1, (RTHandle)data.color1);
                    props.SetVector(GaussianSplatRenderer.Props.BlitScaleBias, scaleBias);
                    props.SetFloat(GaussianSplatRenderer.Props.SplatDepthTest, data.depthTest);

                    // Fullscreen triangle; URP's XR setup instances/broadcasts it per eye.
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, props);
                });
            }
        }

        AccumulatePass m_AccumulatePass;
        CompositePass m_CompositePass;
        bool m_HasCamera;

        public override void Create()
        {
            // Before any camera rendering so the offscreen splat pass sits outside the camera's
            // native render pass instead of splitting it.
            m_AccumulatePass = new AccumulatePass { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
            m_CompositePass = new CompositePass
            {
                // Composite splats before Unity draws transparent/overlay geometry.
                // FK preview markers use the Overlay queue and ZTest Always, so they
                // must be submitted after this fullscreen composite or the composite
                // simply paints over them regardless of their depth state.
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = GaussianSplatRenderSystem.instance.GatherSplatsForCamera(cameraData.camera);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;

            m_AccumulatePass.resolutionScale = m_ResolutionScale;
            m_CompositePass.depthTest = m_DepthTestAgainstScene;
            renderer.EnqueuePass(m_AccumulatePass);
            renderer.EnqueuePass(m_CompositePass);
        }

        protected override void Dispose(bool disposing)
        {
            m_AccumulatePass = null;
            m_CompositePass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
