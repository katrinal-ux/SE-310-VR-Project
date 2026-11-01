using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

// Play-mode design loop for BridgeFrames. With showAnalysis on, every change re-runs TrussSolver and colours the
// members (blue = tension, red = compression, stronger colour = closer to the allowable stress, full colour = over it)
// and an on-screen readout shows the results. It is off by default: tick it in the Inspector to use it.
//   Left click a member:          select it (and show its force when the analysis is on)
//   Left drag a joint:            move it in the truss plane (the two supports stay fixed)
//   Shift + left click 2 joints:  add a member between them
//   Delete / Backspace:           remove the selected member
//   R:                            back to the starting design
[DefaultExecutionOrder(100)]   // after BridgeFrames.Start has built the bridge
[RequireComponent(typeof(BridgeFrames))]
public class BridgeDesigner : MonoBehaviour
{
    [Header("Analysis display")]
    [Tooltip("Run TrussSolver on every change, colour members by tension/compression and utilisation, and show the readout.")]
    public bool showAnalysis = false;

    [Header("Picking")]
    public Camera viewCamera;              // defaults to Camera.main
    public float pickRadius = 14f;         // pixels

    [Header("Material and loads (SI units)")]
    public double youngsModulus = 11e9;    // Pa (timber)
    public double allowableStress = 10e6;  // Pa, tension and compression
    public float density = 500f;           // kg/m^3, member self-weight
    public float deckLoad = 5000f;         // Pa on the deck (dead + live), half to each truss

    BridgeFrames frames;
    List<Vector2> startNodes;
    List<(int a, int b)> startMembers;
    int supportLeft, supportRight;
    readonly HashSet<int> deckNodes = new HashSet<int>();   // bottom-chord joints that carry the deck

    int selectedMember = -1, selectedNode = -1, dragNode = -1;
    float dragZ;
    TrussResult result;
    string summary = "";
    bool analysisShown;                    // state the colours were last applied for

    static readonly Color Neutral = new Color(0.55f, 0.55f, 0.55f);
    static readonly Color Tension = new Color(0.15f, 0.45f, 1f);
    static readonly Color Compression = new Color(1f, 0.2f, 0.15f);
    static readonly Color Selected = new Color(1f, 0.9f, 0.1f);

    void Update()
    {
        if (frames == null) frames = GetComponent<BridgeFrames>();
        if (!frames.IsBuilt) return;
        if (startNodes == null) Init();
        if (showAnalysis != analysisShown) Analyze();   // toggled in the Inspector during Play

        var mouse = Mouse.current; var kb = Keyboard.current;
        if (mouse == null || kb == null) return;
        var cam = viewCamera != null ? viewCamera : Camera.main;
        Vector2 mp = mouse.position.ReadValue();

        if (kb.rKey.wasPressedThisFrame)
        {
            selectedMember = selectedNode = dragNode = -1;
            frames.BuildBridge(startNodes, startMembers);
            Analyze();
        }

        if ((kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame) && selectedMember >= 0)
        {
            var members = new List<(int a, int b)>(frames.Members);
            members.RemoveAt(selectedMember);
            selectedMember = -1;
            frames.BuildBridge(frames.Nodes, members);
            Analyze();
        }

        if (mouse.leftButton.wasPressedThisFrame && cam != null)
        {
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            int n = PickNode(cam, mp, out float z);
            if (n >= 0 && shift && selectedNode >= 0 && selectedNode != n)
            {
                AddMember(selectedNode, n);
                selectedNode = -1;
            }
            else if (n >= 0)
            {
                selectedNode = n; selectedMember = -1;
                if (n != supportLeft && n != supportRight) { dragNode = n; dragZ = z; }
            }
            else
            {
                selectedMember = PickMember(cam, mp);
                selectedNode = -1;
            }
            ApplyColors();
        }

        if (dragNode >= 0 && mouse.leftButton.isPressed && cam != null)
        {
            var plane = new Plane(transform.forward, transform.TransformPoint(new Vector3(0, 0, dragZ)));
            Ray ray = cam.ScreenPointToRay(mp);
            if (plane.Raycast(ray, out float t))
            {
                Vector3 local = transform.InverseTransformPoint(ray.GetPoint(t));
                var p = new Vector2(local.x, local.y);
                if ((p - frames.Nodes[dragNode]).sqrMagnitude > 1e-6f)
                {
                    frames.Nodes[dragNode] = p;
                    frames.Layout();
                    Analyze();
                }
            }
        }
        if (mouse.leftButton.wasReleasedThisFrame) dragNode = -1;
    }

    void Init()
    {
        startNodes = new List<Vector2>(frames.Nodes);
        startMembers = new List<(int a, int b)>(frames.Members);

        // Supports: the two ends of the lowest chord (pin on the left, roller on the right)
        float minY = float.MaxValue;
        foreach (var p in startNodes) minY = Mathf.Min(minY, p.y);
        supportLeft = supportRight = -1;
        for (int i = 0; i < startNodes.Count; i++)
        {
            if (startNodes[i].y > minY + 1e-3f) continue;
            deckNodes.Add(i);
            if (supportLeft < 0 || startNodes[i].x < startNodes[supportLeft].x) supportLeft = i;
            if (supportRight < 0 || startNodes[i].x > startNodes[supportRight].x) supportRight = i;
        }
        Analyze();
    }

    void AddMember(int a, int b)
    {
        foreach (var m in frames.Members)
            if ((m.a == a && m.b == b) || (m.a == b && m.b == a)) return;
        var members = new List<(int a, int b)>(frames.Members) { (a, b) };
        frames.BuildBridge(frames.Nodes, members);
        Analyze();
    }

    // ---- analysis ----

    void Analyze()
    {
        analysisShown = showAnalysis;
        if (!showAnalysis)
        {
            result = null; summary = "";
            ApplyColors();
            return;
        }

        var nodes = frames.Nodes; var members = frames.Members;
        int nm = members.Count;
        double area = frames.beamWidth * frames.beamDepth;
        var elems = new int[nm, 2];
        var modE = new double[nm]; var areas = new double[nm];
        var fy = new double[nodes.Count];

        for (int m = 0; m < nm; m++)
        {
            elems[m, 0] = members[m].a; elems[m, 1] = members[m].b;
            modE[m] = youngsModulus; areas[m] = area;
            double weight = density * area * Vector2.Distance(nodes[members[m].a], nodes[members[m].b]) * 9.81;
            fy[members[m].a] -= weight / 2; fy[members[m].b] -= weight / 2;
        }

        // Deck load to the deck joints by tributary length along the span
        var deck = new List<int>(deckNodes);
        deck.Sort((i, j) => nodes[i].x.CompareTo(nodes[j].x));
        for (int k = 0; k < deck.Count; k++)
        {
            float left = k > 0 ? nodes[deck[k]].x - nodes[deck[k - 1]].x : 0;
            float right = k < deck.Count - 1 ? nodes[deck[k + 1]].x - nodes[deck[k]].x : 0;
            fy[deck[k]] -= deckLoad * (frames.bridgeWidth / 2f) * (left + right) / 2f;
        }

        var loads = new List<(int node, int dir, double value)>();
        for (int n = 0; n < nodes.Count; n++)
            if (fy[n] != 0) loads.Add((n, 2, fy[n]));
        var supports = new[] { (supportLeft, 1, 0.0), (supportLeft, 2, 0.0), (supportRight, 2, 0.0) };

        result = TrussSolver.Solve(nodes.ToArray(), elems, modE, areas, supports, loads.ToArray());
        summary = result.success ? Summarize() : "UNSTABLE: " + result.message;
        ApplyColors();
    }

    string Summarize()
    {
        double maxT = 0, maxC = 0, maxU = 0, maxD = 0; int over = 0;
        for (int m = 0; m < result.axialForce.Length; m++)
        {
            maxT = System.Math.Max(maxT, result.axialForce[m]);
            maxC = System.Math.Min(maxC, result.axialForce[m]);
            double u = System.Math.Abs(result.stress[m]) / allowableStress;
            maxU = System.Math.Max(maxU, u);
            if (u > 1) over++;
        }
        for (int n = 0; n < frames.Nodes.Count; n++)
            maxD = System.Math.Max(maxD, System.Math.Sqrt(result.displacements[2 * n] * result.displacements[2 * n] + result.displacements[2 * n + 1] * result.displacements[2 * n + 1]));
        return $"Max tension {maxT / 1e3:F0} kN   max compression {-maxC / 1e3:F0} kN\n" +
               $"Max utilisation {maxU * 100:F0}%   members over allowable: {over}   max deflection {maxD * 1e3:F0} mm";
    }

    void ApplyColors()
    {
        for (int m = 0; m < frames.Members.Count; m++)
        {
            Color? c = null;                       // analysis off: members keep their own colour
            if (showAnalysis)
            {
                c = Neutral;
                if (result != null && result.success && m < result.stress.Length)
                {
                    double u = System.Math.Abs(result.stress[m]) / allowableStress;
                    c = Color.Lerp(Neutral, result.axialForce[m] >= 0 ? Tension : Compression, (float)System.Math.Min(u, 1));
                }
            }
            frames.TintMember(m, m == selectedMember ? Selected : c);
        }
        for (int n = 0; n < frames.Nodes.Count; n++)
            frames.TintNode(n, n == selectedNode ? Selected : (Color?)null);
    }

    // ---- picking in screen space (members are too thin to click with physics rays) ----

    Vector3 World(Vector2 p, float z) => transform.TransformPoint(new Vector3(p.x, p.y, z));

    int PickNode(Camera cam, Vector2 mp, out float z)
    {
        int best = -1; float bestD = pickRadius; z = 0;
        for (int side = 0; side < 2; side++)
        {
            float sz = side * frames.bridgeWidth;
            for (int n = 0; n < frames.Nodes.Count; n++)
            {
                Vector3 s = cam.WorldToScreenPoint(World(frames.Nodes[n], sz));
                if (s.z <= 0) continue;
                float d = Vector2.Distance(mp, s);
                if (d < bestD) { bestD = d; best = n; z = sz; }
            }
        }
        return best;
    }

    int PickMember(Camera cam, Vector2 mp)
    {
        int best = -1; float bestD = pickRadius;
        for (int side = 0; side < 2; side++)
        {
            float sz = side * frames.bridgeWidth;
            for (int m = 0; m < frames.Members.Count; m++)
            {
                Vector3 a = cam.WorldToScreenPoint(World(frames.Nodes[frames.Members[m].a], sz));
                Vector3 b = cam.WorldToScreenPoint(World(frames.Nodes[frames.Members[m].b], sz));
                if (a.z <= 0 || b.z <= 0) continue;
                Vector2 ab = (Vector2)b - (Vector2)a;
                float t = Mathf.Clamp01(Vector2.Dot(mp - (Vector2)a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                float d = Vector2.Distance(mp, (Vector2)a + t * ab);
                if (d < bestD) { bestD = d; best = m; }
            }
        }
        return best;
    }

    // ---- HUD ----

    void OnGUI()
    {
        if (!showAnalysis || frames == null || !frames.IsBuilt) return;
        var sb = new StringBuilder();
        sb.AppendLine("<b>Bridge designer</b>   right-drag: look   WASD/QE: move   wheel: speed");
        sb.AppendLine("Left click: select member / joint   left drag joint: move   Shift+click 2 joints: add member");
        sb.AppendLine("Delete: remove member   R: reset");
        sb.AppendLine($"{frames.Nodes.Count} joints, {frames.Members.Count} members   deck load {deckLoad / 1e3:F1} kPa");
        sb.AppendLine(summary);

        if (selectedMember >= 0 && selectedMember < frames.Members.Count)
        {
            var m = frames.Members[selectedMember];
            float len = Vector2.Distance(frames.Nodes[m.a], frames.Nodes[m.b]);
            sb.Append($"\nMember {selectedMember} (joints {m.a}-{m.b})  length {len:F2} m");
            if (result != null && result.success)
            {
                double f = result.axialForce[selectedMember];
                sb.Append($"\n{(f >= 0 ? "Tension" : "Compression")} {System.Math.Abs(f) / 1e3:F1} kN   stress {result.stress[selectedMember] / 1e6:F1} MPa" +
                          $"   utilisation {System.Math.Abs(result.stress[selectedMember]) / allowableStress * 100:F0}%");
            }
        }
        else if (selectedNode >= 0 && selectedNode < frames.Nodes.Count)
        {
            var p = frames.Nodes[selectedNode];
            string role = selectedNode == supportLeft ? " (pin support)" : selectedNode == supportRight ? " (roller support)" : "";
            sb.Append($"\nJoint {selectedNode}{role}  x {p.x:F2} m  y {p.y:F2} m");
            if (result != null && result.success)
                sb.Append($"   deflection {result.displacements[2 * selectedNode + 1] * 1e3:F1} mm");
        }

        var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true, wordWrap = true };
        style.normal.textColor = Color.white;
        GUI.Box(new Rect(10, 10, 620, 190), sb.ToString(), style);
    }
}
