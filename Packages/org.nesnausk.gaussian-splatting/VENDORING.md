# Vendored: UnityGaussianSplatting

This package was previously consumed from `file:d:/Unity/UnityGaussianSplatting/package`
via `Packages/manifest.json`. It is now an **embedded package** living inside this
repository, so the 3DGS renderer is versioned together with the project and can be
modified directly.

## Provenance

- Upstream: https://github.com/aras-p/UnityGaussianSplatting
- Upstream commit at vendor time: see `UPSTREAM_COMMIT.txt`
  (`2c6fed37da67a217367261fcfcd3316d34c73e76`)
- Vendored on: 2026-08-14
- The local (uncommitted) modifications that existed in the external checkout at
  vendor time are captured in `LOCAL_PATCHES_AT_VENDOR_TIME.patch` for reference.
  Those changes are **already applied** to the files here.

## Local divergence from upstream (carried over)

These were made for this project and are not upstream:

| Area | Change |
| --- | --- |
| `Shaders/GaussianComposite.shader` | Rewritten for URP `Blitter`; explicit `SRGBToLinear` on the un-premultiplied colour |
| `Shaders/SplatUtilities.compute` | Per-eye VP/P matrices (`_UseEyeMatrices`, `_ViewDataOffset`) for XR single-pass; linear-blend-skinning (`_BoneMatrixA/B`, `_SplatWeights`) |
| `Shaders/RenderGaussianSplats.shader` | `_ViewDataOffset` indexing for the doubled stereo view-data buffer |
| `Shaders/GaussianSplatting.hlsl` | `FlipProjectionIfBackbuffer` excluded from compute stage |
| `Shaders/SortCommon.hlsl`, `DeviceRadixSort.hlsl`, `Runtime/GpuSorting.cs` | `[unroll]` disabled and 256-thread init dispatch — workarounds for the sort on this hardware |
| `Runtime/GaussianSplatRenderer.cs`, `GaussianSplatURPFeature.cs`, `GaussianSplatHDRPPass.cs` | URP renderer-feature integration + stereo/skinning plumbing |

## Shared sort + depth-tested composite (2026-09-25)

Replaces upstream's per-object sort/draw and the mid-frame unsafe URP pass:

| Area | Change |
| --- | --- |
| `GaussianSplatRenderSystem` | One view-data buffer, one 16-bit depth sort and one indirect draw per eye for **all** objects in Splats mode, so splats of different objects interleave by depth. `m_RenderOrder` only affects debug modes now. |
| `CSCalcViewData` | One dispatch per object covers both eyes (SH once, from the centre eye); frustum + opacity culling; writes sort keys and the visible count (indirect args). |
| `RenderGaussianSplats.shader` | Back-to-front premultiplied "over" (same colour as the old front-to-back "under"); alpha-weighted view depth accumulated for the composite (was a second MRT target, now packed in one target, see below); quad clipped to the alpha >= 1/255 extent. |
| `GaussianSplatURPFeature` | Accumulate pass before camera rendering into per-eye offscreen targets; composite is a raster pass inside the main pass that outputs splat depth and depth-tests against the camera depth (occlusion by opaque geometry, MSAA stays on-tile). `m_ResolutionScale`, `m_DepthTestAgainstScene` settings. |
| `GpuSorting` | `Args.keyBits` to sort fewer key bits (2 passes for 16-bit keys). |

## Performance pass for ~3.7M splats (2026-09-25)

Measured with `Assets/Scripts/Editor/SplatRenderBenchmark.cs` (menu *Tools/Gaussian Splats/Benchmark
Render Stages*) on the Ambulance Scan Test scene: 3.73M splats (room 1M + manikin 2.7M), stereo
1680x1760 per eye, RTX 4060. Before: prepare (view data + sort) 3.9 ms, draw 28-30 ms.

| Area | Change | Effect (desktop) |
| --- | --- | --- |
| `RenderGaussianSplats.shader`, `GaussianComposite.shader`, `GaussianSplatURPFeature` | One RGBA16F accumulation target per eye instead of colour + a second depth target: `(Y, chroma, depth, 1) * alpha`, colour as YCoCg with Co/Cg checkerboarded; the composite rebuilds chroma edge-aware inside a manual 2x2 bilinear fetch. The second render target alone cost ~38% of the draw whatever its format. | Draw 30.3 -> 17.7 ms. Colour PSNR 52-57 dB vs before (fringe only on 1-px strong colour edges), coverage and depth identical. |
| `SplatUtilities.compute` (`CSCalcViewData`) | Chunk frustum culling: per 256-splat chunk, the position bounds padded by 3.5x the chunk's max sigma are tested against every eye; rejected chunks skip decode/projection. SH bands are loaded only for visible splats with `m_SHOrder > 0`. On sort frames culled splats write only their sort key. | Prepare 3.9 -> 3.1 ms (1.2 ms looking away). Image bit-identical. |
| `GaussianSplatRenderSystem`, `RenderGaussianSplats.shader` | Draw as batches of 4096 quads from one index buffer (instance = batch) instead of one 2-triangle instance per splat. Batch size is a C# constant passed as `_SplatsPerBatch` so shader and index buffer can't disagree (a transient mismatch hung the GPU once). | Neutral on NVIDIA; tiny instances are a known bottleneck on mobile GPUs. Image bit-identical (verified vs batch size 1). **Unmeasured on Quest.** |
| `GaussianSplatRenderer.m_MinOpacity` | New per-object cull threshold, default 0 (= old behaviour). | Manikin at 0.05: -19% visible splats, -6% frame, 50-55 dB. A Quest knob, not applied. |

Not changed, deliberately: the `[unroll]`-disabled sort (it sorts 3.7M keys in 0.8 ms here; the
workaround's reason is undocumented and may be Quest-specific), screen-size limits on the room
(even 400 px costs visible quality). Not done yet: sorting only the visible count (needs an
indirect-count sort).

## Fix: right eye showed the left eye's splats on Quest (2026-10-03)

The 9/25 composite picks the eye's splat buffer with `unity_StereoEyeIndex`. Under multiview
(Quest, Vulkan) that index is the placeholder cbuffer `OVR_multiview.gl_ViewID` (URP
`UnityInput.hlsl`), which only Unity's FXC->HLSLcc path turns into the hardware view index. The
composite carried `#pragma use_dxc` from the old per-eye blit (which never read the eye index), so
on device the index was always 0: the right eye composited the left eye's image. Zero disparity
made the model look far away and swim as the head turned. The editor can't show it, because D3D11
ignores `use_dxc` and uses instancing. `GaussianComposite.shader` is now `#pragma never_use_dxc`.
Any shader that reads the stereo eye index must stay off DXC.

## Render-to-texture camera opt-in (2026-10-08, I2Care)

`GatherSplatsForCamera` skips non-main game cameras that target a texture, so overlay cameras
stay cheap. `GaussianSplatRenderer.forceRenderCameras` (public static set) exempts chosen
cameras. I2Care's 2D-video baseline (`VideoBaselineView`) registers its fixed "ambulance
camera" there. This change exists only in the I2Care copy of the package.

## Note on `Packages/manifest.json`

The `org.nesnausk.gaussian-splatting` entry was **removed** from `manifest.json`.
Embedded packages (any folder under `Packages/` with a `package.json`) are discovered
automatically; leaving a `file:` entry pointing at the old external path would conflict.
