using System.Collections.Generic;
using UnityEngine;

public class BridgeFrames : MonoBehaviour
{
    [Header("Optional prefabs (leave empty to use temporary cubes)")]
    public GameObject beamPrefab;        // assumed: scale 1, pivot centered, long axis = local Y
    public float beamPrefabLength = 1f;  // its length along Y at scale 1
    public GameObject gussetPrefab;      // assumed: scale 1, pivot centered, thin axis = local Z

    [Header("Beam size (m)")]
    public float beamWidth = 0.3f;       // size in the plane of the truss
    public float beamDepth = 0.2f;       // size out of the plane

    [Header("Gusset plates")]
    public float gussetSize = 1.0f;
    public float gussetThickness = 0.05f;

    [Header("Bridge")]
    public float bridgeWidth = 4f;       // distance between the two trusses
    public bool buildDeck = true;
    public float deckThickness = 0.15f;
    public bool frameCamera = true;

    Transform container;
    readonly Color wood = new Color(0.55f, 0.38f, 0.22f);
    readonly Color steel = new Color(0.45f, 0.47f, 0.5f);
    readonly Color deckColor = new Color(0.4f, 0.3f, 0.2f);

    void Start()
    {
        container = new GameObject("BridgeContainer").transform;

        if (frameCamera)
        {
            var cam = Camera.main;
            cam.transform.position = new Vector3(10, 6, -28);
            cam.transform.LookAt(new Vector3(10, 2.5f, bridgeWidth / 2f));
        }

        // ---- TEMPORARY sample data: 20 m, 4-panel Pratt truss ----
        // Replace with your input system later. BuildBridge doesn't care where data comes from.
        var sampleNodes = new List<Vector2> {
            new Vector2(0,0),  new Vector2(5,0),  new Vector2(10,0), new Vector2(15,0), new Vector2(20,0), // 0-4 bottom chord
            new Vector2(5,5),  new Vector2(10,5), new Vector2(15,5)                                        // 5-7 top chord
        };
        var sampleMembers = new List<(int a, int b)> {
            (0,1),(1,2),(2,3),(3,4),   // bottom chord
            (5,6),(6,7),               // top chord
            (0,5),(4,7),               // end posts
            (1,5),(2,6),(3,7),         // verticals
            (5,2),(7,2)                // diagonals sloping toward center
        };
        // ---- end temporary block ----

        BuildBridge(sampleNodes, sampleMembers);
    }

    // Seam for your future input system.
    public void BuildBridge(List<Vector2> nodes, List<(int a, int b)> members)
    {
        foreach (Transform child in container) Destroy(child.gameObject);

        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue;
        foreach (var n in nodes)
        {
            minX = Mathf.Min(minX, n.x);
            maxX = Mathf.Max(maxX, n.x);
            minY = Mathf.Min(minY, n.y);
        }

        // Two identical trusses: front at z = 0, back at z = bridgeWidth
        for (int side = 0; side < 2; side++)
        {
            float z = side * bridgeWidth;

            foreach (var m in members)
            {
                if (m.a >= nodes.Count || m.b >= nodes.Count)
                {
                    Debug.LogWarning($"Member ({m.a},{m.b}) references a missing node, skipped.");
                    continue;
                }
                SpawnBeam(To3D(nodes[m.a], z), To3D(nodes[m.b], z), Vector3.forward);
            }

            foreach (var n in nodes)
                SpawnGussets(To3D(n, z));
        }

        // Cross beams tying the two trusses together at every node
        foreach (var n in nodes)
            SpawnBeam(To3D(n, 0), To3D(n, bridgeWidth), Vector3.up);

        if (buildDeck)
        {
            var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            deck.transform.SetParent(container);
            deck.transform.position = new Vector3(
                (minX + maxX) / 2f,
                minY + beamWidth / 2f + deckThickness / 2f,
                bridgeWidth / 2f);
            deck.transform.localScale = new Vector3(maxX - minX, deckThickness, bridgeWidth);
            Tint(deck, deckColor);
        }
    }

    Vector3 To3D(Vector2 p, float z) => new Vector3(p.x, p.y, z);

    // 'normal' = the direction the beam's thin face points (out of the truss plane).
    // Must not be parallel to the beam itself.
    void SpawnBeam(Vector3 a, Vector3 b, Vector3 normal)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 0.0001f) return;

        GameObject beam = Make(beamPrefab, wood);
        beam.transform.position = (a + b) / 2f;
        beam.transform.rotation = Quaternion.LookRotation(normal, dir); // local Y = along beam, local Z = normal

        if (beamPrefab == null)
            beam.transform.localScale = new Vector3(beamWidth, len, beamDepth);
        else
        {
            Vector3 s = beam.transform.localScale;
            s.y = len / beamPrefabLength;
            beam.transform.localScale = s;
        }
    }

    // One plate on each face of the truss at the node, sandwiching the members
    void SpawnGussets(Vector3 nodePos)
    {
        float offset = beamDepth / 2f + gussetThickness / 2f;
        for (int s = -1; s <= 1; s += 2)
        {
            GameObject g = Make(gussetPrefab, steel);
            g.transform.position = nodePos + new Vector3(0, 0, s * offset);
            g.transform.rotation = Quaternion.identity;
            if (gussetPrefab == null)
                g.transform.localScale = new Vector3(gussetSize, gussetSize, gussetThickness);
        }
    }

    GameObject Make(GameObject prefab, Color fallbackColor)
    {
        if (prefab != null)
            return Instantiate(prefab, container);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(container);
        Tint(go, fallbackColor);
        return go;
    }

    void Tint(GameObject go, Color c) => go.GetComponent<Renderer>().material.color = c;
}