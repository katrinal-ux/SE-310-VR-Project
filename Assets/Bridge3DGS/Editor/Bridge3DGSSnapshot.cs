using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

// Renders a few fixed views of the 3DGS bridge site to PNGs (in <project>/_Bridge3DGSBackup/shots) to check
// that the panoramic skybox lines up with the cropped splats at the edges of the bridge area.
public static class Bridge3DGSSnapshot
{
    static readonly (string name, Vector3 pos, Vector3 target)[] kViews =
    {
        ("deck_along_+x", new Vector3(0, 15.5f, 0), new Vector3(100, 12, 0)),
        ("deck_along_-x", new Vector3(0, 15.5f, 0), new Vector3(-100, 12, 0)),
        ("deck_across_+z", new Vector3(0, 16, 0), new Vector3(0, 8, 100)),
        ("deck_across_-z", new Vector3(0, 16, 0), new Vector3(0, 8, -100)),
        ("overview", new Vector3(-170, 80, -150), new Vector3(0, 10, 0)),
        // From the skybox centre (the Main Camera position): panorama and splats should line up here.
        ("skyeye_upstream", new Vector3(7.4f, 38.49f, -75.4f), new Vector3(7.4f, 10, -180)),
        ("skyeye_downstream", new Vector3(7.4f, 38.49f, -75.4f), new Vector3(7.4f, 10, 40)),
        ("skyeye_west", new Vector3(7.4f, 38.49f, -75.4f), new Vector3(-130, 10, -60)),
        ("skyeye_east", new Vector3(7.4f, 38.49f, -75.4f), new Vector3(150, 10, -60)),
        ("wt_site_above", new Vector3(-22, 70, -115), new Vector3(-22, 5, -36)),
        // The two Walter Taylor approach roads from mirrored viewpoints (west one is the rotated copy of the east one).
        ("road_east", new Vector3(70.75f, 26, -50.8f), new Vector3(101.8f, 12, -26.15f)),
        ("road_west", new Vector3(-116.25f, 20, -22.2f), new Vector3(-147.3f, 6.5f, -46.85f)),
        // The BridgeFrames road bridge (built in Play mode): overview, side, and from the west road at eye height.
        ("roadbridge_overview", new Vector3(-90, 70, -170), new Vector3(-3.8f, 15, -35)),
        ("roadbridge_side", new Vector3(-3.8f, 20, -190), new Vector3(-3.8f, 15, -35)),
        ("roadbridge_from_west_road", new Vector3(-122.4f, 7.6f, -44.8f), new Vector3(-3.8f, 12, -35)),
    };

    [MenuItem("Tools/Bridge 3DGS/Render Check Snapshots")]
    public static void Render()
    {
        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "_Bridge3DGSBackup", "shots");
        Directory.CreateDirectory(dir);
        var go = new GameObject("Bridge3DGSSnapshotCam") { hideFlags = HideFlags.HideAndDontSave };
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 75; cam.nearClipPlane = 0.1f; cam.farClipPlane = 2000; cam.clearFlags = CameraClearFlags.Skybox;
        var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        GaussianSplatRenderer.forceRenderCameras.Add(cam);
        try
        {
            foreach (var v in kViews)
            {
                go.transform.position = v.pos;
                go.transform.LookAt(v.target);
                cam.Render();
                cam.Render(); // splat sort uses the previous frame's view
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(dir, v.name + ".png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }
            Debug.Log("[Bridge3DGS] Snapshots written to " + dir);
        }
        finally
        {
            GaussianSplatRenderer.forceRenderCameras.Remove(cam);
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(go);
        }
    }
}
