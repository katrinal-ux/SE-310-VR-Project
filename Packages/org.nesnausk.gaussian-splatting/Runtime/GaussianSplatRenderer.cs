// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    // Per-eye camera setup for one splat render. Mono uses eyeCount = 1 and only the eye-0 fields.
    internal struct SplatViews
    {
        public int eyeCount;
        public Matrix4x4 view0, view1;   // world -> eye view
        public Matrix4x4 proj0, proj1;   // eye GPU projection (render-into-texture convention)
        public Vector3 shOrigin;         // view origin for view-dependent (SH) colour
        public Matrix4x4 sortView;       // world -> view that defines the shared draw order
        public Vector2 targetSize;       // accumulation target size in pixels

        public Matrix4x4 View(int eye) => eye == 0 ? view0 : view1;
        public Matrix4x4 Proj(int eye) => eye == 0 ? proj0 : proj1;

        public static SplatViews ForCamera(Camera cam, bool stereo, Vector2 targetSize)
        {
            var v = new SplatViews
            {
                eyeCount = stereo ? 2 : 1,
                shOrigin = cam.transform.position,
                sortView = cam.worldToCameraMatrix,
                targetSize = targetSize,
            };
            if (stereo)
            {
                v.view0 = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                v.view1 = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
                v.proj0 = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), true);
                v.proj1 = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), true);
            }
            else
            {
                v.view0 = v.view1 = cam.worldToCameraMatrix;
                v.proj0 = v.proj1 = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            }
            return v;
        }
    }

    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        // ── Shared state for all splat objects ─────────────────────
        // Every object in Splats render mode writes its view data into one buffer, which is
        // depth-sorted and drawn as a single instanced draw per eye. Splats of different objects
        // therefore interleave by depth: overlapping models (the two arm segments at the elbow,
        // an object inside a room scan) composite correctly regardless of m_RenderOrder.
        const int kSortKeyBits = 16; // must match DepthToBackToFrontKey in SplatUtilities.compute
        const int kGpuViewDataSize = 40;
        // Splats draw as batches of quads from one index buffer (instance = batch) instead of one
        // 2-triangle instance per splat, which leaves most of the vertex hardware idle, especially
        // on mobile GPUs. Passed to the shaders as _SplatsPerBatch (single source of truth); 4 * batch vertex
        // ids must fit the 16-bit index buffer.
        const int kSplatsPerBatch = 4096;
        // [0..4] indexed indirect args (batch count in [1] set on the GPU), [5] visible splat count.
        static readonly uint[] s_DrawArgsInit = { 6 * kSplatsPerBatch, 0, 0, 0, 0, 0 };
        GraphicsBuffer m_GlobalView;        // [eye * count + global splat index]
        GraphicsBuffer m_GlobalSortKeys;    // draw-order keys
        GraphicsBuffer m_GlobalSortPayload; // global splat indices, sorted = draw order
        GraphicsBuffer m_DrawArgs;
        GraphicsBuffer m_VisibleCount;      // [0] = visible splats, read by the vertex shader
        GraphicsBuffer m_QuadIndexBuffer;
        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;
        int m_GlobalCapacity;
        int m_GlobalCount;
        int m_GlobalEyeCapacity;
        Material m_GlobalDrawMaterial;
        readonly MaterialPropertyBlock[] m_EyeProps = { new(), new() };
        Camera m_LastSortedCamera;
        int m_LastSortedLayout;
        int m_FrameCounter;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
                DisposeSharedBuffers();
            }
        }

        void DisposeSharedBuffers()
        {
            m_GlobalView?.Dispose();
            m_GlobalSortKeys?.Dispose();
            m_GlobalSortPayload?.Dispose();
            m_DrawArgs?.Dispose();
            m_VisibleCount?.Dispose();
            m_QuadIndexBuffer?.Dispose();
            m_SorterArgs.resources.Dispose();
            m_GlobalView = m_GlobalSortKeys = m_GlobalSortPayload = m_DrawArgs = m_VisibleCount = m_QuadIndexBuffer = null;
            m_Sorter = null;
            m_GlobalCapacity = m_GlobalCount = m_GlobalEyeCapacity = 0;
            m_LastSortedCamera = null;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            if (cam.cameraType == CameraType.Reflection)
                return false;

            // Exclude OVROverlayCanvas or other UI/overlay cameras that render to textures,
            // unless the camera opted in (e.g. a render-to-texture "video" camera).
            if (cam.cameraType == CameraType.Game && !cam.CompareTag("MainCamera") &&
                !GaussianSplatRenderer.forceRenderCameras.Contains(cam))
            {
                if (cam.targetTexture != null ||
                    cam.name.Contains("Canvas") || cam.name.Contains("OVR") ||
                    cam.gameObject.name.Contains("Canvas") || cam.gameObject.name.Contains("OVR"))
                {
                    return false;
                }
            }

            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null)
                    continue;
                if (!gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }

            if (m_ActiveSplats.Count == 0)
                return false;

            // Order only matters for objects in a debug render mode, which are still drawn one
            // object at a time; splats in Splats mode are depth-sorted together.
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // Composite material of the gathered objects (they all share the composite shader).
        internal Material FindCompositeMaterial()
        {
            foreach (var (gs, _) in m_ActiveSplats)
            {
                gs.EnsureMaterials();
                if (gs.m_MatComposite != null)
                    return gs.m_MatComposite;
            }
            return null;
        }

        bool EnsureSharedBuffers(int count, int eyeCount, ComputeShader cs)
        {
            m_Sorter ??= new GpuSorting(cs);
            if (!m_Sorter.Valid)
                return false;

            m_QuadIndexBuffer ??= CreateQuadIndexBuffer();
            m_DrawArgs ??= new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, s_DrawArgsInit.Length, 4) { name = "GaussianSplatDrawArgs" };
            m_VisibleCount ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianSplatVisibleCount" };

            if (count > m_GlobalCapacity)
            {
                // Grow with headroom so toggling objects on and off does not reallocate every time.
                int capacity = Mathf.Max(count, m_GlobalCapacity + m_GlobalCapacity / 4);
                m_GlobalSortKeys?.Dispose();
                m_GlobalSortPayload?.Dispose();
                m_SorterArgs.resources.Dispose();
                m_GlobalSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianSplatSortKeys" };
                m_GlobalSortPayload = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianSplatSortPayload" };
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)capacity);
                m_GlobalCapacity = capacity;
                m_GlobalEyeCapacity = 0; // view buffer is sized from the capacity, rebuild it too
            }
            if (m_GlobalEyeCapacity < eyeCount)
            {
                long viewBytes = (long)m_GlobalCapacity * eyeCount * kGpuViewDataSize;
                long maxBuf = SystemInfo.maxGraphicsBufferSize;
                if (maxBuf > 0 && viewBytes > maxBuf)
                {
                    Debug.LogError($"[GaussianSplat] {m_GlobalCapacity:N0} splats x {eyeCount} eyes need a {viewBytes / (1024 * 1024)}MB view buffer, above this device's {maxBuf / (1024 * 1024)}MB limit. Splats will not render; reduce the number or size of active splat assets.");
                    return false;
                }
                m_GlobalView?.Dispose();
                m_GlobalView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_GlobalCapacity * eyeCount, kGpuViewDataSize) { name = "GaussianSplatViewData" };
                m_GlobalEyeCapacity = eyeCount;
            }
            return true;
        }

        // kSplatsPerBatch quads; quad q uses vertex ids 4q..4q+3 (corner = id & 3).
        static GraphicsBuffer CreateQuadIndexBuffer()
        {
            var indices = new ushort[6 * kSplatsPerBatch];
            for (int q = 0; q < kSplatsPerBatch; q++)
            {
                int v = q * 4, i = q * 6;
                indices[i + 0] = (ushort)(v + 0);
                indices[i + 1] = (ushort)(v + 1);
                indices[i + 2] = (ushort)(v + 2);
                indices[i + 3] = (ushort)(v + 1);
                indices[i + 4] = (ushort)(v + 3);
                indices[i + 5] = (ushort)(v + 2);
            }
            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Index, indices.Length, 2) { name = "GaussianSplatQuadIndices" };
            buf.SetData(indices);
            return buf;
        }

        // Computes view data for every active splat object into the shared buffers and, on sort
        // frames, depth-sorts all of them together. Returns the composite material (null when
        // nothing is active). Call DrawSplats afterwards for each eye.
        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material PrepareSplats(CommandBuffer cmb, Camera cam, in SplatViews views)
        {
            Material matComposite = null;
            m_GlobalDrawMaterial = null;
            int total = 0;
            int layout = 17;
            int sortNth = int.MaxValue;
            GaussianSplatRenderer first = null;
            foreach (var (gs, _) in m_ActiveSplats)
            {
                gs.EnsureMaterials();
                matComposite ??= gs.m_MatComposite;
                if (gs.m_RenderMode != GaussianSplatRenderer.RenderMode.Splats || gs.m_MatSplats == null)
                    continue;
                first ??= gs;
                gs.m_GlobalBase = total;
                total += gs.splatCount;
                layout = layout * 31 + gs.GetHashCode();
                layout = layout * 31 + gs.splatCount;
                sortNth = Mathf.Min(sortNth, Mathf.Max(1, gs.m_SortNthFrame));
            }

            m_GlobalCount = 0;
            if (first == null || total == 0)
                return matComposite;
            if (!EnsureSharedBuffers(total, views.eyeCount, first.m_CSSplatUtilities))
                return matComposite;
            m_GlobalCount = total;
            m_GlobalDrawMaterial = first.m_MatSplats;

            // Skipping sorts (m_SortNthFrame > 1) reuses the last order and visible count. That is
            // only valid for the same camera and the same set of objects in the same slots.
            bool sortNow = cam != m_LastSortedCamera || layout != m_LastSortedLayout || m_FrameCounter % sortNth == 0;
            ++m_FrameCounter;
            if (sortNow)
                cmb.SetBufferData(m_DrawArgs, s_DrawArgsInit);

            cmb.BeginSample(s_ProfCalcView);
            foreach (var (gs, _) in m_ActiveSplats)
            {
                if (gs.m_RenderMode != GaussianSplatRenderer.RenderMode.Splats || gs.m_MatSplats == null)
                    continue;
                gs.CalcViewData(cmb, views, m_GlobalView, m_GlobalSortKeys, m_GlobalSortPayload, m_DrawArgs, m_GlobalCount, sortNow);
            }
            if (sortNow)
            {
                var cs = first.m_CSSplatUtilities;
                int finalize = cs.FindKernel("CSFinalizeDrawArgs");
                cmb.SetComputeBufferParam(cs, finalize, GaussianSplatRenderer.Props.DrawArgs, m_DrawArgs);
                cmb.SetComputeBufferParam(cs, finalize, GaussianSplatRenderer.Props.SplatVisibleCount, m_VisibleCount);
                cmb.SetComputeIntParam(cs, GaussianSplatRenderer.Props.SplatsPerBatch, kSplatsPerBatch);
                cmb.DispatchCompute(cs, finalize, 1, 1, 1);
            }
            cmb.EndSample(s_ProfCalcView);

            if (sortNow)
            {
                cmb.BeginSample(s_ProfSort);
                m_SorterArgs.inputKeys = m_GlobalSortKeys;
                m_SorterArgs.inputValues = m_GlobalSortPayload;
                m_SorterArgs.count = (uint)m_GlobalCount;
                m_SorterArgs.keyBits = kSortKeyBits;
                m_Sorter.Dispatch(cmb, m_SorterArgs);
                cmb.EndSample(s_ProfSort);
                m_LastSortedCamera = cam;
                m_LastSortedLayout = layout;
            }
            return matComposite;
        }

        // Draws every splat for one eye into the currently bound target(s), then any objects in
        // a debug render mode. PrepareSplats must have been called for this camera first.
        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void DrawSplats(CommandBuffer cmb, int eye, in SplatViews views)
        {
            cmb.BeginSample(s_ProfDraw);
            if (m_GlobalCount > 0 && m_GlobalDrawMaterial != null)
            {
                var mat = m_GlobalDrawMaterial;
                var mpb = m_EyeProps[eye];
                mpb.Clear();
                var rtSize = new Vector4(views.targetSize.x, views.targetSize.y, 0, 0);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, m_GlobalView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, m_GlobalSortPayload);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatVisibleCount, m_VisibleCount);
                mpb.SetInteger(GaussianSplatRenderer.Props.SplatsPerBatch, kSplatsPerBatch);
                mpb.SetInteger(GaussianSplatRenderer.Props.ViewDataOffset, eye * m_GlobalCount);
                mpb.SetVector(GaussianSplatRenderer.Props.SplatRTSize, rtSize);
                // Also on the material for Vulkan StructuredBuffer compatibility. The per-eye
                // offset stays in the property block, which is captured per draw.
                mat.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, m_GlobalView);
                mat.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, m_GlobalSortPayload);
                mat.SetBuffer(GaussianSplatRenderer.Props.SplatVisibleCount, m_VisibleCount);
                mat.SetInteger(GaussianSplatRenderer.Props.SplatsPerBatch, kSplatsPerBatch);
                mat.SetVector(GaussianSplatRenderer.Props.SplatRTSize, rtSize);
                cmb.DrawProceduralIndirect(m_QuadIndexBuffer, Matrix4x4.identity, mat, 0, MeshTopology.Triangles, m_DrawArgs, 0, mpb);
            }

            foreach (var (gs, mpb) in m_ActiveSplats)
            {
                if (gs.m_RenderMode != GaussianSplatRenderer.RenderMode.Splats)
                    gs.DrawDebug(cmb, mpb);
            }
            cmb.EndSample(s_ProfDraw);
        }

        // Mono render of all splats into the currently bound target; used by the HDRP pass and
        // the built-in pipeline path. The target must be sized like the camera.
        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            var views = SplatViews.ForCamera(cam, false, new Vector2(cam.pixelWidth, cam.pixelHeight));
            Material matComposite = PrepareSplats(cmb, cam, views);
            DrawSplats(cmb, 0, views);
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            Matrix4x4 gpuProjScreen = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
            Matrix4x4 gpuProjTexture = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            bool projectionMismatched = (gpuProjScreen.m11 * gpuProjTexture.m11 < 0.0f);
            float flipY = (cam.cameraType != CameraType.SceneView && cam.cameraType != CameraType.Preview && projectionMismatched) ? 1.0f : 0.0f;
            m_CommandBuffer.SetGlobalFloat(GaussianSplatRenderer.Props.SplatFlipY, flipY);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        /// <summary>
        /// Non-main cameras that render splats even when they target a texture (those are skipped by
        /// default to keep overlay cameras cheap).
        /// </summary>
        public static readonly HashSet<Camera> forceRenderCameras = new();

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }
        public GaussianSplatAsset m_Asset;

        [Tooltip("Only used by the debug render modes. In Splats mode the splats of all objects are depth-sorted together, so overlapping objects occlude each other by position rather than by this order.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0f, 0.5f)]
        [Tooltip("Skip splats whose opacity (after Opacity Scale) is below this. Dense or fused scans often carry " +
                 "many faint splats that cost as much to sort and draw as opaque ones but add little to the image. " +
                 "0 keeps everything visible (splats below 1/255 are always skipped).")]
        public float m_MinOpacity = 0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames. The sort is shared by all splat objects and runs at the smallest N among them.")]
        public int m_SortNthFrame = 1;

        public enum SplatSizeLimitMode
        {
            Shrink,  // cap the splat at the limit, keeping it
            Discard, // remove the splat entirely
        }

        [Header("Splat size limits")]
        [Tooltip("What to do with splats that exceed the limits below.\n\n" +
                 "Discard is usually what you want for reconstruction floaters around blown-out " +
                 "highlights: shrinking such a splat leaves a smaller floater in the same place, " +
                 "whereas discarding removes the haze it contributes.\n\n" +
                 "Shrink is safer on real geometry, since nothing disappears.")]
        public SplatSizeLimitMode m_SizeLimitMode = SplatSizeLimitMode.Discard;

        [Tooltip("Maximum world-space splat radius (1 sigma) in metres. 0 = no limit.\n\n" +
                 "Camera-independent, so it targets genuinely oversized blobs — the large, faint " +
                 "Gaussians reconstruction fits to bloom around lights and windows.")]
        [Min(0f)] public float m_MaxWorldSize = 0f;

        [Tooltip("Maximum on-screen splat footprint diameter, in pixels. 0 = no limit.\n\n" +
                 "Bounds how much of one frame a single splat can cover, e.g. when the camera " +
                 "moves inside a large splat. Note this scales with render resolution.")]
        [Min(0f)] public float m_MaxScreenSizePx = 0f;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        internal GraphicsBuffer m_GpuSortKeys; // identity splat order, only read by the debug box modes
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuIndexBuffer;
        internal int m_GlobalBase; // first slot of this object's splats in the shared view/sort buffers

        // Skinning (LBS)
        GraphicsBuffer m_GpuSkinWeights;
        float[] m_UploadedSkinWeights;
        bool m_SkinningEnabled;
        Matrix4x4 m_BoneMatrixA = Matrix4x4.identity;
        Matrix4x4 m_BoneMatrixB = Matrix4x4.identity;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatMinOpacity = Shader.PropertyToID("_SplatMinOpacity");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            // ── Splat size limiting ──────────────────────────────
            public static readonly int MaxWorldSize = Shader.PropertyToID("_MaxWorldSize");
            public static readonly int MaxScreenSizePx = Shader.PropertyToID("_MaxScreenSizePx");
            public static readonly int SplatSizeLimitMode = Shader.PropertyToID("_SplatSizeLimitMode");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            // ── Shared view data / stereo XR ──────────────────────
            public static readonly int MatrixMVEye = Shader.PropertyToID("_MatrixMVEye");
            public static readonly int MatrixVPEye = Shader.PropertyToID("_MatrixVPEye");
            public static readonly int MatrixPEye = Shader.PropertyToID("_MatrixPEye");
            public static readonly int MatrixSortView = Shader.PropertyToID("_MatrixSortView");
            public static readonly int GlobalBase = Shader.PropertyToID("_GlobalBase");
            public static readonly int GlobalCount = Shader.PropertyToID("_GlobalCount");
            public static readonly int EyeCount = Shader.PropertyToID("_EyeCount");
            public static readonly int WriteSortKeys = Shader.PropertyToID("_WriteSortKeys");
            public static readonly int DrawArgs = Shader.PropertyToID("_DrawArgs");
            public static readonly int SplatVisibleCount = Shader.PropertyToID("_SplatVisibleCount");
            public static readonly int SplatsPerBatch = Shader.PropertyToID("_SplatsPerBatch");
            public static readonly int ViewDataOffset = Shader.PropertyToID("_ViewDataOffset");
            public static readonly int SplatRTSize = Shader.PropertyToID("_SplatRTSize");
            // ── Composite ─────────────────────────────────────────
            public static readonly int GaussianSplatRTEye1 = Shader.PropertyToID("_GaussianSplatRTEye1");
            public static readonly int SplatDepthTest = Shader.PropertyToID("_SplatDepthTest");
            public static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SplatFlipY = Shader.PropertyToID("_SplatFlipY");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            // ── Skinning ─────────────────────────────────────────
            public static readonly int BoneMatrixA = Shader.PropertyToID("_BoneMatrixA");
            public static readonly int BoneMatrixB = Shader.PropertyToID("_BoneMatrixB");
            public static readonly int SplatWeights = Shader.PropertyToID("_SplatWeights");
            public static readonly int SkinWeightsValid = Shader.PropertyToID("_SkinWeightsValid");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;
        public GraphicsBuffer gpuPosData => m_GpuPosData;
        public GraphicsBuffer gpuOtherData => m_GpuOtherData;
        public GraphicsBuffer gpuChunks => m_GpuChunks;
        public bool gpuChunksValid => m_GpuChunksValid;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;

        void CreateResourcesForAsset()
        {
            //Debug.Log($"[GaussianSplatRenderer] CreateResourcesForAsset for {name}: m_Asset={m_Asset != null}, HasValidAsset={HasValidAsset}");
            if (!HasValidAsset)
            {
                if (m_Asset != null)
                {
                    Debug.LogError($"[GaussianSplatRenderer] HasValidAsset is false on {name}! splatCount={m_Asset.splatCount}, formatVersion={m_Asset.formatVersion} (expected {GaussianSplatAsset.kCurrentVersion}), posData={m_Asset.posData != null}, otherData={m_Asset.otherData != null}, shData={m_Asset.shData != null}, colorData={m_Asset.colorData != null}");
                }
                return;
            }

            if (!CheckGpuBudget())
                return;

            m_SplatCount = asset.splatCount;
            try
            {
                CreateBuffersForAsset();
            }
            catch (Exception ex)
            {
                // Without this, a failed allocation part-way through leaves m_GpuChunks null, so
                // HasValidRenderSetup goes false and the object is silently dropped from rendering.
                Debug.LogError($"[GaussianSplatRenderer] Failed to allocate GPU resources for '{name}' (asset '{asset.name}', {asset.splatCount:N0} splats, {DescribeGpuBudget()}). This splat object will not render. Re-import the source PLY at a lower quality preset (Medium/Low) - the fully uncompressed 'VeryHigh' preset is desktop-only. Underlying error: {ex}");
                DisposeResourcesForAsset();
                return;
            }

            InitDebugOrder(splatCount);
            EnsureRegistered();
        }

        // Largest single buffer this asset needs. Exceeding SystemInfo.maxGraphicsBufferSize is the
        // usual reason an asset renders fine in the Editor but silently disappears on Quest/mobile,
        // where the limit is far below the 2GB a desktop GPU reports.
        long LargestBufferSize(out string which)
        {
            long shSize = asset.shData.dataSize;
            long viewSize = (long)asset.splatCount * 2 * kGpuViewDataSize;
            long posSize = asset.posData.dataSize;
            long otherSize = asset.otherData.dataSize;

            which = "SH"; long biggest = shSize;
            if (viewSize > biggest) { which = "view"; biggest = viewSize; }
            if (posSize > biggest) { which = "position"; biggest = posSize; }
            if (otherSize > biggest) { which = "other"; biggest = otherSize; }
            return biggest;
        }

        string DescribeGpuBudget()
        {
            const double kMB = 1024 * 1024;
            long total = asset.posData.dataSize + asset.otherData.dataSize + asset.shData.dataSize +
                         asset.colorData.dataSize + (long)asset.splatCount * 2 * kGpuViewDataSize +
                         (long)asset.splatCount * 16;
            long biggest = LargestBufferSize(out string which);
            return $"~{total / kMB:F0}MB of GPU buffers, largest is the {which} buffer at {biggest / kMB:F0}MB; device max buffer size is {SystemInfo.maxGraphicsBufferSize / kMB:F0}MB";
        }

        bool CheckGpuBudget()
        {
            long maxBuf = SystemInfo.maxGraphicsBufferSize;
            long biggest = LargestBufferSize(out string which);
            if (maxBuf > 0 && biggest > maxBuf)
            {
                Debug.LogError($"[GaussianSplatRenderer] Asset '{asset.name}' on '{name}' needs a {which} buffer larger than this device supports ({DescribeGpuBudget()}). Refusing to allocate; this splat object will not render. Re-import the source PLY at a lower quality preset (Medium/Low) - the fully uncompressed 'VeryHigh' preset is desktop-only.");
                return false;
            }
            return true;
        }

        void CreateBuffersForAsset()
        {
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            // View data lives in GaussianSplatRenderSystem's shared buffer, sized for all objects.
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }

        // Identity splat order for the debug box modes, which read _OrderBuffer. Real splat
        // rendering uses the shared, depth-sorted order owned by GaussianSplatRenderSystem.
        void InitDebugOrder(int count)
        {
            m_GpuSortKeys?.Dispose();
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatDebugOrder" };

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (count + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
            }
        }

        public void EnsureRegistered()
        {
            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            bool supportsCompute = SystemInfo.supportsComputeShaders;
            //Debug.Log($"[GaussianSplatRenderer] OnEnable for {name}: resourcesAreSetUp={resourcesAreSetUp}, supportsCompute={supportsCompute}, m_Asset={m_Asset != null}, m_ShaderSplats={m_ShaderSplats != null}, m_ShaderComposite={m_ShaderComposite != null}, m_ShaderDebugPoints={m_ShaderDebugPoints != null}, m_ShaderDebugBoxes={m_ShaderDebugBoxes != null}, m_CSSplatUtilities={m_CSSplatUtilities != null}");
            
            if (!resourcesAreSetUp)
            {
                Debug.LogError($"[GaussianSplatRenderer] resourcesAreSetUp check failed on {name}! m_ShaderSplats={m_ShaderSplats != null}, m_ShaderComposite={m_ShaderComposite != null}, m_ShaderDebugPoints={m_ShaderDebugPoints != null}, m_ShaderDebugBoxes={m_ShaderDebugBoxes != null}, m_CSSplatUtilities={m_CSSplatUtilities != null}, compute={supportsCompute}");
                return;
            }

            EnsureMaterials();
            EnsureRegistered();

            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        internal void SetAssetDataOnMaterial(Material mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSkinWeights);
            m_UploadedSkinWeights = null;
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SplatCount = 0;
            m_GpuChunksValid = false;
            m_SkinningEnabled = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        void SetSizeLimitsOnCS(CommandBuffer cmb)
        {
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.MaxWorldSize, Mathf.Max(0f, m_MaxWorldSize));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.MaxScreenSizePx, Mathf.Max(0f, m_MaxScreenSizePx));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SplatSizeLimitMode,
                m_SizeLimitMode == SplatSizeLimitMode.Discard ? 1 : 0);
        }

        static readonly Matrix4x4[] s_EyeMV = new Matrix4x4[2];
        static readonly Matrix4x4[] s_EyeVP = new Matrix4x4[2];
        static readonly Matrix4x4[] s_EyeP = new Matrix4x4[2];

        // Writes this object's view data for every eye into the shared view buffer at
        // [m_GlobalBase, m_GlobalBase + splatCount), culling splats no eye can see. On sort frames
        // it also writes the shared draw-order keys and adds its visible splats to drawArgs[1].
        internal void CalcViewData(CommandBuffer cmb, in SplatViews views,
            GraphicsBuffer viewData, GraphicsBuffer sortKeys, GraphicsBuffer sortPayload, GraphicsBuffer drawArgs,
            int globalCount, bool writeSortKeys)
        {
            const int kernel = (int)KernelIndices.CalcViewData;
            var cs = m_CSSplatUtilities;
            var tr = transform;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;

            for (int eye = 0; eye < 2; ++eye)
            {
                Matrix4x4 view = views.View(eye);
                Matrix4x4 proj = views.Proj(eye);
                s_EyeMV[eye] = view * matO2W;
                s_EyeVP[eye] = proj * view;
                s_EyeP[eye] = proj;
            }

            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);
            cmb.SetComputeBufferParam(cs, kernel, Props.SplatViewData, viewData);
            cmb.SetComputeBufferParam(cs, kernel, Props.SplatSortDistances, sortKeys);
            cmb.SetComputeBufferParam(cs, kernel, Props.SplatSortKeys, sortPayload);
            cmb.SetComputeBufferParam(cs, kernel, Props.DrawArgs, drawArgs);
            cmb.SetComputeIntParam(cs, Props.GlobalBase, m_GlobalBase);
            cmb.SetComputeIntParam(cs, Props.GlobalCount, globalCount);
            cmb.SetComputeIntParam(cs, Props.EyeCount, views.eyeCount);
            cmb.SetComputeIntParam(cs, Props.WriteSortKeys, writeSortKeys ? 1 : 0);

            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeMatrixArrayParam(cs, Props.MatrixMVEye, s_EyeMV);
            cmb.SetComputeMatrixArrayParam(cs, Props.MatrixVPEye, s_EyeVP);
            cmb.SetComputeMatrixArrayParam(cs, Props.MatrixPEye, s_EyeP);
            cmb.SetComputeMatrixParam(cs, Props.MatrixSortView, views.sortView);

            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, new Vector4(views.targetSize.x, views.targetSize.y, 0, 0));
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, views.shOrigin);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeFloatParam(cs, Props.SplatMinOpacity, m_MinOpacity);
            cmb.SetComputeIntParam(cs, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, m_SHOnly ? 1 : 0);
            SetSizeLimitsOnCS(cmb);

            // Skinning
            if (m_SkinningEnabled && m_GpuSkinWeights != null)
            {
                cmb.SetComputeIntParam(cs, Props.SkinWeightsValid, 1);
                cmb.SetComputeBufferParam(cs, kernel, Props.SplatWeights, m_GpuSkinWeights);
                cmb.SetComputeMatrixParam(cs, Props.BoneMatrixA, m_BoneMatrixA);
                cmb.SetComputeMatrixParam(cs, Props.BoneMatrixB, m_BoneMatrixB);
            }
            else
            {
                cmb.SetComputeIntParam(cs, Props.SkinWeightsValid, 0);
                cmb.SetComputeBufferParam(cs, kernel, Props.SplatWeights, m_GpuPosData);
            }

            cs.GetKernelThreadGroupSizes(kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, kernel, (m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        // Debug render modes (points, boxes, chunk bounds) draw per object, unsorted, into the
        // currently bound target. They exist for inspecting assets in the editor.
        internal void DrawDebug(CommandBuffer cmb, MaterialPropertyBlock mpb)
        {
            EnsureMaterials();
            Material displayMat = m_RenderMode switch
            {
                RenderMode.DebugPoints => m_MatDebugPoints,
                RenderMode.DebugPointIndices => m_MatDebugPoints,
                RenderMode.DebugBoxes => m_MatDebugBoxes,
                RenderMode.DebugChunkBounds => m_MatDebugBoxes,
                _ => null
            };
            if (displayMat == null)
                return;

            mpb.Clear();
            SetAssetDataOnMaterial(mpb);
            mpb.SetBuffer(Props.SplatChunks, m_GpuChunks);
            mpb.SetBuffer(Props.OrderBuffer, m_GpuSortKeys);
            mpb.SetFloat(Props.SplatScale, m_SplatScale);
            mpb.SetFloat(Props.SplatOpacityScale, m_OpacityScale);
            mpb.SetFloat(Props.SplatSize, m_PointDisplaySize);
            mpb.SetInteger(Props.DisplayIndex, m_RenderMode == RenderMode.DebugPointIndices ? 1 : 0);
            mpb.SetInteger(Props.DisplayChunks, m_RenderMode == RenderMode.DebugChunkBounds ? 1 : 0);

            // Set on Material directly for Vulkan StructuredBuffer compatibility
            SetAssetDataOnMaterial(displayMat);
            displayMat.SetBuffer(Props.SplatChunks, m_GpuChunks);
            displayMat.SetBuffer(Props.OrderBuffer, m_GpuSortKeys);

            int indexCount = m_RenderMode is RenderMode.DebugBoxes or RenderMode.DebugChunkBounds ? 36 : 6;
            int instanceCount = splatCount;
            if (m_RenderMode == RenderMode.DebugChunkBounds)
                instanceCount = m_GpuChunksValid ? m_GpuChunks.count : 0;

            cmb.DrawProcedural(m_GpuIndexBuffer, transform.localToWorldMatrix, displayMat, 0, MeshTopology.Triangles, indexCount, instanceCount, mpb);
        }

        public void Update()
        {
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                //Debug.Log($"[GaussianSplatRenderer] Update on {name}: asset changed! Prev={m_PrevAsset != null}, New={m_Asset != null}, PrevHash={m_PrevHash}, NewHash={curHash}");
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            InitDebugOrder(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;

        // ── Public Skinning API ─────────────────────────────────────

        /// <summary>
        /// Enable LBS with per-splat skin weights and two bone matrices.
        /// </summary>
        /// <param name="weights">Per-splat blend weight [0,1] where 0 = BoneA, 1 = BoneB</param>
        /// <param name="boneA">Primary bone world matrix</param>
        /// <param name="boneB">Blend target bone world matrix</param>
        public void SetSkinningData(float[] weights, Matrix4x4 boneA, Matrix4x4 boneB)
        {
            if (weights == null || weights.Length != m_SplatCount)
            {
                Debug.LogError($"[GaussianSplat] Skin weight count ({weights?.Length ?? 0}) != splat count ({m_SplatCount})");
                return;
            }

            if (m_GpuSkinWeights == null || m_GpuSkinWeights.count != m_SplatCount)
            {
                DisposeBuffer(ref m_GpuSkinWeights);
                m_GpuSkinWeights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4);
                m_UploadedSkinWeights = null;
            }

            // Callers typically pass the same weights every frame with new bone matrices;
            // re-uploading an unchanged per-splat array each frame is pure CPU->GPU traffic.
            // A different array (e.g. after SetCustomWeights) is uploaded; editing an array in
            // place after passing it here requires ClearSkinningData first.
            if (!ReferenceEquals(weights, m_UploadedSkinWeights))
            {
                m_GpuSkinWeights.SetData(weights);
                m_UploadedSkinWeights = weights;
            }
            m_BoneMatrixA = boneA;
            m_BoneMatrixB = boneB;
            m_SkinningEnabled = true;
        }

        /// <summary>Disable LBS and return to standard rigid transform.</summary>
        public void ClearSkinningData()
        {
            m_SkinningEnabled = false;
            DisposeBuffer(ref m_GpuSkinWeights);
            m_UploadedSkinWeights = null;
        }
    }
}