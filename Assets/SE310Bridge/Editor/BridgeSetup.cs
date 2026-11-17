using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class BridgeSetup
{
    private const string Menu = "Tools/SE 310 Bridge/";
    private const string ModelPath = "Assets/SE310Bridge/SE310_SymmetricExample.json";

    [MenuItem(Menu + "Set Up in Open Scene", priority = 2000)]
    private static void SetUp()
    {
        Scene scene = SceneManager.GetActiveScene();
        BridgeFEAViewer viewer = FindViewer(scene);
        if (viewer == null)
        {
            Transform anchor = Selection.activeTransform;
            var root = new GameObject("BridgeRoot");
            Undo.RegisterCreatedObjectUndo(root, "Add bridge");
            if (anchor != null && anchor.gameObject.scene == scene)
                root.transform.SetParent(anchor, false);
            viewer = Undo.AddComponent<BridgeFEAViewer>(root);
        }
        Undo.RecordObject(viewer, "Set weathered steel material");
        viewer.steelMaterial = Resources.Load<Material>("SE310_WeatheredSteel");
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = viewer.gameObject;
    }

    [MenuItem(Menu + "Use Symmetric Example Model", priority = 2001)]
    private static void UseExample()
    {
        BridgeFEAViewer viewer = FindViewer(SceneManager.GetActiveScene());
        if (viewer == null)
        {
            Debug.LogError("Run Set Up in Open Scene first.");
            return;
        }
        Undo.RecordObject(viewer, "Assign symmetric example");
        viewer.modelJson = AssetDatabase.LoadAssetAtPath<TextAsset>(ModelPath);
        EditorSceneManager.MarkSceneDirty(viewer.gameObject.scene);
        Selection.activeGameObject = viewer.gameObject;
    }

    [MenuItem(Menu + "Frame Desktop Camera on Bridge", priority = 2002)]
    private static void FrameDesktopCamera()
    {
        Scene scene = SceneManager.GetActiveScene();
        BridgeFEAViewer viewer = FindViewer(scene);
        if (viewer == null)
        {
            Debug.LogError("Run Set Up in Open Scene first.");
            return;
        }
        Camera camera = null;
        foreach (var root in scene.GetRootGameObjects())
            if (root.CompareTag("MainCamera") && root.TryGetComponent(out camera)) break;

        if (camera == null)
        {
            Debug.LogError("Select a desktop scene with a root-level MainCamera. Position an XR Origin separately.");
            return;
        }

        Bounds localBounds = ReadModelBounds(viewer);
        float span = Mathf.Max(localBounds.size.x, 0.5f);
        Vector3 target = viewer.transform.TransformPoint(localBounds.center);
        Vector3 direction = viewer.transform.TransformDirection(new Vector3(-0.77f, 0.51f, -0.93f));
        Undo.RecordObject(camera.transform, "Frame camera on bridge");
        camera.transform.SetPositionAndRotation(target + direction * span,
            Quaternion.LookRotation(-direction, viewer.transform.up));
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static BridgeFEAViewer FindViewer(Scene scene)
    {
        var selected = Selection.activeGameObject;
        if (selected != null && selected.scene == scene &&
            selected.TryGetComponent(out BridgeFEAViewer viewer)) return viewer;
        foreach (var root in scene.GetRootGameObjects())
        {
            var candidate = root.GetComponentInChildren<BridgeFEAViewer>(true);
            if (candidate != null) return candidate;
        }
        return null;
    }

    private static Bounds ReadModelBounds(BridgeFEAViewer viewer)
    {
        try
        {
            string json = viewer.modelJson != null ? viewer.modelJson.text :
                File.ReadAllText(Path.Combine(Application.streamingAssetsPath, viewer.jsonFileName));
            var model = JsonUtility.FromJson<BridgeFEAViewer.Model>(json);
            float scale = viewer.displayScale * 0.001f;
            var first = model.nodes[0];
            var bounds = new Bounds(new Vector3(first.x, first.y, 0) * scale, Vector3.zero);
            foreach (var n in model.nodes)
                bounds.Encapsulate(new Vector3(n.x, n.y, 0) * scale);
            return bounds;
        }
        catch (System.Exception)
        {
            return new Bounds(new Vector3(5, 1.3f, 0), new Vector3(10, 2.6f, 3));
        }
    }
}
