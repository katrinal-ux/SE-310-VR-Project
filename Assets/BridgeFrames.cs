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

    [Header("Pratt truss used until an input system calls BuildBridge")]
    public float span = 20f;             // along the bridge's local X
    public int panels = 4;               // even, so the diagonals meet at mid-span
    public float trussHeight = 5f;

    // Current design (local truss plane coordinates). Change Nodes in place and call Layout() to move joints;
    // call BuildBridge for a new topology.
    public List<Vector2> Nodes { get; } = new List<Vector2>();
    public List<(int a, int b)> Members { get; } = new List<(int a, int b)>();
    public bool IsBuilt => container != null;

    Transform container;
    readonly Color wood = new Color(0.55f, 0.38f, 0.22f);
    readonly Color steel = new Color(0.45f, 0.47f, 0.5f);
    readonly Color deckColor = new Color(0.4f, 0.3f, 0.2f);

    // Spawned parts, indexed like the design: [side][member], [side][node][face], [node]
    readonly List<GameObject>[] memberBeams = { new List<GameObject>(), new List<GameObject>() };
    readonly List<GameObject[]>[] nodeGussets = { new List<GameObject[]>(), new List<GameObject[]>() };
    readonly List<GameObject> crossBeams = new List<GameObject>();
    GameObject deck;
    readonly Dictionary<Renderer, Color> originalColors = new Dictionary<Renderer, Color>();

    void Start()
    {
        // The bridge is built in this object's local space: local X along the span, Y up, Z across.
        container = new GameObject("BridgeContainer").transform;
        container.SetParent(transform, false);

        if (frameCamera)
        {
            var cam = Camera.main;
            cam.transform.position = transform.TransformPoint(new Vector3(10, 6, -28));
            cam.transform.LookAt(transform.TransformPoint(new Vector3(10, 2.5f, bridgeWidth / 2f)));
        }

        // ---- TEMPORARY data until the input system exists ----
        // Replace with your input system later. BuildBridge doesn't care where data comes from.
        var nodes = new List<Vector2>();
        var members = new List<(int a, int b)>();
        PrattTruss(span, panels, trussHeight, nodes, members);
        // ---- end temporary block ----

        BuildBridge(nodes, members);
    }

    // Pratt truss: bottom chord nodes 0..n, top chord nodes n+1..2n-1 (above bottom nodes 1..n-1).
    // The defaults (20 m, 4 panels, 5 m) give the original 20 m sample exactly.
    public static void PrattTruss(float span, int panels, float height, List<Vector2> nodes, List<(int a, int b)> members)
    {
        int n = Mathf.Max(2, panels + panels % 2);
        if (n != panels) Debug.LogWarning($"Pratt truss needs an even panel count; using {n}.");
        float p = span / n;
        int Top(int j) => n + j;

        for (int i = 0; i <= n; i++) nodes.Add(new Vector2(i * p, 0));
        for (int j = 1; j < n; j++) nodes.Add(new Vector2(j * p, height));

        for (int i = 0; i < n; i++) members.Add((i, i + 1));              // bottom chord
        for (int j = 1; j < n - 1; j++) members.Add((Top(j), Top(j + 1))); // top chord
        members.Add((0, Top(1))); members.Add((n, Top(n - 1)));          // end posts
        for (int j = 1; j < n; j++) members.Add((j, Top(j)));             // verticals
        for (int j = 1; j < n; j++)                                       // diagonals sloping toward center
        {
            if (j < n / 2) members.Add((Top(j), j + 1));
            else if (j > n / 2) members.Add((Top(j), j - 1));
        }
    }

    // Seam for your future input system.
    public void BuildBridge(List<Vector2> nodes, List<(int a, int b)> members)
    {
        var nodeCopy = new List<Vector2>(nodes);
        var memberCopy = new List<(int a, int b)>();
        foreach (var m in members)
        {
            if (m.a >= nodeCopy.Count || m.b >= nodeCopy.Count)
            {
                Debug.LogWarning($"Member ({m.a},{m.b}) references a missing node, skipped.");
                continue;
            }
            memberCopy.Add(m);
        }
        Nodes.Clear(); Nodes.AddRange(nodeCopy);
        Members.Clear(); Members.AddRange(memberCopy);

        foreach (Transform child in container) Destroy(child.gameObject);
        originalColors.Clear();
        crossBeams.Clear();
        deck = null;

        // Two identical trusses: front at z = 0, back at z = bridgeWidth
        for (int side = 0; side < 2; side++)
        {
            memberBeams[side].Clear();
            nodeGussets[side].Clear();
            foreach (var _ in Members) memberBeams[side].Add(Make(beamPrefab, wood));
            foreach (var _ in Nodes) nodeGussets[side].Add(new[] { Make(gussetPrefab, steel), Make(gussetPrefab, steel) });
        }

        // Cross beams tying the two trusses together at every node
        foreach (var _ in Nodes) crossBeams.Add(Make(beamPrefab, wood));

        if (buildDeck)
        {
            deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            deck.transform.SetParent(container, false);
            Tint(deck, deckColor);
        }

        Layout();
    }

    // Places every spawned part from the current Nodes (joints can move without respawning anything).
    public void Layout()
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue;
        foreach (var n in Nodes)
        {
            minX = Mathf.Min(minX, n.x);
            maxX = Mathf.Max(maxX, n.x);
            minY = Mathf.Min(minY, n.y);
        }

        for (int side = 0; side < 2; side++)
        {
            float z = side * bridgeWidth;
            for (int m = 0; m < Members.Count; m++)
                PlaceBeam(memberBeams[side][m], To3D(Nodes[Members[m].a], z), To3D(Nodes[Members[m].b], z), Vector3.forward);
            for (int n = 0; n < Nodes.Count; n++)
                PlaceGussets(nodeGussets[side][n], To3D(Nodes[n], z));
        }

        for (int n = 0; n < Nodes.Count; n++)
            PlaceBeam(crossBeams[n], To3D(Nodes[n], 0), To3D(Nodes[n], bridgeWidth), Vector3.up);

        if (deck != null)
        {
            deck.transform.localPosition = new Vector3(
                (minX + maxX) / 2f,
                minY + beamWidth / 2f + deckThickness / 2f,
                bridgeWidth / 2f);
            deck.transform.localScale = new Vector3(maxX - minX, deckThickness, bridgeWidth);
        }
    }

    // Tints a member on both trusses; null restores its own colour.
    public void TintMember(int m, Color? c)
    {
        for (int side = 0; side < 2; side++) TintParts(memberBeams[side][m], c);
    }

    // Tints a joint's gusset plates on both trusses; null restores their own colour.
    public void TintNode(int n, Color? c)
    {
        for (int side = 0; side < 2; side++)
            foreach (var g in nodeGussets[side][n]) TintParts(g, c);
    }

    Vector3 To3D(Vector2 p, float z) => new Vector3(p.x, p.y, z);

    // 'normal' = the direction the beam's thin face points (out of the truss plane).
    // Must not be parallel to the beam itself.
    void PlaceBeam(GameObject beam, Vector3 a, Vector3 b, Vector3 normal)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        beam.SetActive(len >= 0.0001f);
        if (len < 0.0001f) return;

        beam.transform.localPosition = (a + b) / 2f;
        beam.transform.localRotation = Quaternion.LookRotation(normal, dir); // local Y = along beam, local Z = normal

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
    void PlaceGussets(GameObject[] plates, Vector3 nodePos)
    {
        float offset = beamDepth / 2f + gussetThickness / 2f;
        for (int i = 0; i < 2; i++)
        {
            GameObject g = plates[i];
            g.transform.localPosition = nodePos + new Vector3(0, 0, (i * 2 - 1) * offset);
            g.transform.localRotation = Quaternion.identity;
            if (gussetPrefab == null)
                g.transform.localScale = new Vector3(gussetSize, gussetSize, gussetThickness);
        }
    }

    GameObject Make(GameObject prefab, Color fallbackColor)
    {
        if (prefab != null)
            return Instantiate(prefab, container);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(container, false);
        Tint(go, fallbackColor);
        return go;
    }

    void Tint(GameObject go, Color c) => go.GetComponent<Renderer>().material.color = c;

    void TintParts(GameObject go, Color? c)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            if (!originalColors.ContainsKey(r)) originalColors[r] = r.material.color;
            r.material.color = c ?? originalColors[r];
        }
    }
}
