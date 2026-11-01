using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Builds the 3DGS bridge site (Indooroopilly): the 3DGS bridge area (bridges + 30 m around them, metres,
// Y up, X along the bridges, y = 0 at the river surface) and a panoramic skybox of everything further away.
// The bridge area has the Walter Taylor road bridge removed and its river-surface splats pruned. The skybox is the
// whole scan minus the bridges and the river reflections (river and banks included), rendered from the scene's
// Main Camera position (7.4, 38.49, -75.4), so it carries the river; the flat water plane is created switched off.
// Batch: unity run <project> -- -executeMethod Bridge3DGSSetup.Run -quit
public static class Bridge3DGSSetup
{
    // v4 = v2 plus the east Walter Taylor approach road copied (rotated 180 deg) onto the west bank and extended
    // by repeated copies of its river end until it reaches the water.
    const string kPly = "D:/Workshop/Akademik/2025 UIUC/HXRI/Misc/Kat Bridge Scan/unity_bridge_1m/bridge_area_v4.ply";
    const string kDir = "Assets/Bridge3DGS";
    const string kSplatDir = kDir + "/Splats";
    const string kSkyTex = kDir + "/Bridge3DGS_Skybox.png";
    const string kSkyMat = kDir + "/Bridge3DGS_Skybox.mat";
    const string kWaterMat = kDir + "/Bridge3DGS_Water.mat";
    const string kPrefab = kDir + "/Bridge3DGS.prefab";
    const string kScene = kDir + "/Bridge3DGS.unity";
    static readonly string[] kRenderers = { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Mobile_Renderer.asset" };

    // Bridge-area box from the crop (metres): the water plane covers its footprint.
    static readonly Vector2 kBoxX = new Vector2(-146.3f, 144.6f);
    static readonly Vector2 kBoxZ = new Vector2(-75.6f, 57.4f);
    // Ends of the Walter Taylor approach roads (asphalt surface, centreline) that the designed bridge connects,
    // and the road width between the kerbs. The west road is the rotated copy of the east one.
    static readonly Vector3 kRoadWest = new Vector3(-88.523f, 4.556f, -41.966f);
    static readonly Vector3 kRoadEast = new Vector3(80.893f, 10.552f, -27.887f);
    const float kRoadWidth = 10f;
    // Starting Pratt layout for the 170 m gap: ~9.5 m panels, depth ~span/9.5.
    const int kRoadBridgePanels = 18;
    const float kRoadBridgeHeight = 18f;

    // River colour of the skybox just beyond the box edge, so the plane meets the panorama without a seam.
    static readonly Color32 kWaterColor = new Color32(144, 142, 131, 255);

    [MenuItem("Tools/Bridge 3DGS/Set Up Site")]
    public static void Run()
    {
        AddSplatFeatureToRenderers();
        var asset = ImportSplats();
        var sky = MakeSkyboxMaterial();
        var prefab = MakePrefab(asset);
        MakeScene(prefab, sky);
        AssetDatabase.SaveAssets();
        Debug.Log("[Bridge3DGS] Setup done.");
    }

    static void AddSplatFeatureToRenderers()
    {
        // The feature class is internal to the package, so it is created by name.
        var featureType = typeof(GaussianSplatRenderer).Assembly.GetType("GaussianSplatting.Runtime.GaussianSplatURPFeature", true);
        foreach (var path in kRenderers)
        {
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (data == null) { Debug.LogWarning($"[Bridge3DGS] No renderer at {path}"); continue; }
            if (data.rendererFeatures.Exists(f => f != null && f.GetType() == featureType)) continue;

            var feature = ScriptableObject.CreateInstance(featureType);
            feature.name = featureType.Name;
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
            Debug.Log($"[Bridge3DGS] Added GaussianSplatURPFeature to {path}");
        }
    }

    // Same reflection route as I2Care's ManikinSetupTool: Medium quality (VeryHigh fails to render on Quest).
    static GaussianSplatAsset ImportSplats()
    {
        string assetPath = $"{kSplatDir}/{Path.GetFileNameWithoutExtension(kPly)}.asset";
        // Drop splat assets imported from earlier versions of the PLY (asset + its *_chk/_col/_oth/_pos/_shs data).
        foreach (var guid in AssetDatabase.FindAssets("t:GaussianSplatAsset", new[] { kSplatDir }))
        {
            string old = AssetDatabase.GUIDToAssetPath(guid);
            if (old == assetPath) continue;
            string prefix = old.Substring(0, old.Length - ".asset".Length) + "_";
            foreach (var dataGuid in AssetDatabase.FindAssets("", new[] { kSplatDir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(dataGuid);
                if (p.StartsWith(prefix) && p.EndsWith(".bytes")) AssetDatabase.DeleteAsset(p);
            }
            AssetDatabase.DeleteAsset(old);
            Debug.Log($"[Bridge3DGS] Removed old splat asset {old}");
        }
        var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
        if (existing != null) return existing;

        Directory.CreateDirectory(kSplatDir);
        var type = typeof(GaussianSplatAssetCreator);
        const BindingFlags kF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            type.GetField("m_InputFile", kF).SetValue(creator, kPly);
            type.GetField("m_OutputFolder", kF).SetValue(creator, kSplatDir);
            var fQuality = type.GetField("m_Quality", kF);
            fQuality.SetValue(creator, Enum.Parse(fQuality.FieldType, "Medium"));
            type.GetField("m_ImportCameras", kF)?.SetValue(creator, false);
            type.GetMethod("ApplyQualityLevel", kF).Invoke(creator, null);
            type.GetMethod("CreateAsset", kF).Invoke(creator, null);
            var err = type.GetField("m_ErrorMessage", kF).GetValue(creator) as string;
            if (!string.IsNullOrEmpty(err)) throw new Exception("[Bridge3DGS] Splat import failed: " + err);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(creator);
        }
        AssetDatabase.Refresh();
        return AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath)
               ?? throw new FileNotFoundException("[Bridge3DGS] Splat asset not created at " + assetPath);
    }

    static Material MakeSkyboxMaterial()
    {
        var ti = (TextureImporter)AssetImporter.GetAtPath(kSkyTex);
        ti.textureShape = TextureImporterShape.Texture2D;
        ti.sRGBTexture = true;
        ti.mipmapEnabled = false; // mips give a seam where the panorama wraps
        ti.wrapModeU = TextureWrapMode.Repeat;
        ti.wrapModeV = TextureWrapMode.Clamp;
        ti.maxTextureSize = 8192;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
        ti.SaveAndReimport();

        var mat = AssetDatabase.LoadAssetAtPath<Material>(kSkyMat);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Skybox/Panoramic"));
            AssetDatabase.CreateAsset(mat, kSkyMat);
        }
        // Panorama is authored for Skybox/Panoramic's latitude-longitude lookup with no rotation.
        mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(kSkyTex));
        mat.SetFloat("_Mapping", 1);
        mat.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
        mat.DisableKeyword("_MAPPING_6_FRAMES_LAYOUT");
        mat.SetFloat("_ImageType", 0);
        mat.SetFloat("_MirrorOnBack", 0);
        mat.SetFloat("_Rotation", 0);
        mat.SetFloat("_Exposure", 1);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static GameObject MakePrefab(GaussianSplatAsset asset)
    {
        var root = new GameObject("Bridge3DGS");
        try
        {
            var splats = new GameObject("BridgeArea_Splats");
            splats.transform.SetParent(root.transform, false);
            var sr = splats.AddComponent<GaussianSplatRenderer>();
            sr.m_Asset = asset;
            AssignDefaultShaders(sr);

            var water = GameObject.CreatePrimitive(PrimitiveType.Quad);
            water.name = "Water";
            water.transform.SetParent(root.transform, false);
            water.transform.localRotation = Quaternion.Euler(90, 0, 0);
            water.transform.localPosition = new Vector3((kBoxX.x + kBoxX.y) / 2, -0.05f, (kBoxZ.x + kBoxZ.y) / 2);
            water.transform.localScale = new Vector3(kBoxX.y - kBoxX.x, kBoxZ.y - kBoxZ.x, 1);
            var wmr = water.GetComponent<MeshRenderer>();
            wmr.sharedMaterial = MakeWaterMaterial();
            wmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            water.SetActive(false);   // opaque: it would hide the skybox river and banks under the bridge area

            return PrefabUtility.SaveAsPrefabAsset(root, kPrefab);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    // Default references from the script's .meta are normally applied on AddComponent in the editor;
    // fall back to the package GUIDs if they were not.
    static void AssignDefaultShaders(GaussianSplatRenderer sr)
    {
        T Load<T>(string guid) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
        if (sr.m_ShaderSplats == null) sr.m_ShaderSplats = Load<Shader>("ed800126ae8844a67aad1974ddddd59c");
        if (sr.m_ShaderComposite == null) sr.m_ShaderComposite = Load<Shader>("7e184af7d01193a408eb916d8acafff9");
        if (sr.m_ShaderDebugPoints == null) sr.m_ShaderDebugPoints = Load<Shader>("b44409fc67214394f8f47e4e2648425e");
        if (sr.m_ShaderDebugBoxes == null) sr.m_ShaderDebugBoxes = Load<Shader>("4006f2680fd7c8b4cbcb881454c782be");
        if (sr.m_CSSplatUtilities == null) sr.m_CSSplatUtilities = Load<ComputeShader>("ec84f78b836bd4f96a105d6b804f08bd");
    }

    static Material MakeWaterMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(kWaterMat);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(mat, kWaterMat);
        }
        mat.SetColor("_BaseColor", kWaterColor);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Puts the scene's BridgeFrames design system on the approach roads: its local X runs from the west road end
    // to the east one (span = their distance), local Z across the road, and the deck top meets the asphalt.
    static void PlaceRoadBridge()
    {
        var bf = UnityEngine.Object.FindAnyObjectByType<BridgeFrames>();
        if (bf == null) { Debug.LogWarning("[Bridge3DGS] No BridgeFrames in the scene; road bridge not placed."); return; }

        Vector3 along = kRoadEast - kRoadWest;
        Vector3 across = Vector3.Cross(along, Vector3.up).normalized;
        Quaternion rot = Quaternion.LookRotation(across, Vector3.Cross(across, along));
        float deckTop = bf.beamWidth / 2f + bf.deckThickness;

        bf.gameObject.name = "RoadBridge (BridgeFrames)";
        bf.transform.SetPositionAndRotation(kRoadWest - rot * new Vector3(0, deckTop, kRoadWidth / 2f), rot);
        bf.transform.localScale = Vector3.one;
        bf.span = along.magnitude;
        bf.panels = kRoadBridgePanels;
        bf.trussHeight = kRoadBridgeHeight;
        bf.bridgeWidth = kRoadWidth;
        bf.frameCamera = false;
        EditorUtility.SetDirty(bf);
        EditorUtility.SetDirty(bf.transform);
        Debug.Log($"[Bridge3DGS] Road bridge: span {bf.span:F1} m, {bf.panels} panels, {bf.trussHeight} m deep, {bf.bridgeWidth} m wide");

        // Play-mode interaction: left click designs the bridge, right drag looks around.
        if (bf.GetComponent<BridgeDesigner>() == null) bf.gameObject.AddComponent<BridgeDesigner>();
        var cam = Camera.main;
        if (cam != null && cam.GetComponent<FlyCamera>() == null) { cam.gameObject.AddComponent<FlyCamera>(); EditorUtility.SetDirty(cam.gameObject); }
    }

    static void MakeScene(GameObject prefab, Material sky)
    {
        if (!File.Exists(kScene))
            AssetDatabase.CopyAsset("Assets/Scenes/SampleScene.unity", kScene);
        var scene = EditorSceneManager.OpenScene(kScene, OpenSceneMode.Single);
        if (GameObject.Find("Bridge3DGS") == null)
            PrefabUtility.InstantiatePrefab(prefab, scene);
        RenderSettings.skybox = sky;
        PlaceRoadBridge();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
