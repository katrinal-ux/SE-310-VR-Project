using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Serialization;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class BridgeFEAViewer : MonoBehaviour
{
    [Serializable] public class Node { public float x; public float y; }
    [Serializable] public class Member { public int a; public int b; }
    [Serializable] public class Displacement { public float x; public float y; }
    [Serializable] public class Support { public int node; public int dir; public float valueMm; }
    [Serializable] public class Load { public int node; public int dir; public float forceN; }
    [Serializable] public class Model
    {
        public Node[] nodes;
        public Member[] members;
        public Displacement[] displacementsMm;
        public float[] memberForcesN;
        public Support[] supports;
        public Load[] loads;
        public float youngsModulusPa = 3e9f;
        public float memberSideMm = 3.18f;
        public float modelWidthMm; // Overall width, including the side beams.
        public int[] deckNodes;    // Optional list of deck joints, in span order.
        public string title;
    }

    [Header("Source")]
    public TextAsset modelJson;
    public string jsonFileName = "bridge.json";
    public bool solveOnLoad = true;
    [Header("Geometry")]
    [Min(0.01f)] public float displayScale = 50f;
    [Min(0.01f)] public float widthMeters = 3f;
    [Min(0.001f)] public float beamThicknessMeters = 0.12f;
    [FormerlySerializedAs("woodMaterial"), Tooltip("Optional material override. Use Weathered Steel for the standard finish.")]
    public Material steelMaterial;
    public bool showStatusBoard = true;
    [Header("FEA view")]
    public bool showFEA;
    [Min(0f)] public float displacementExaggeration = 30f;
    public bool animateTransition = true;
    [Min(0.01f)] public float transitionSpeed = 2f;
    public Color compressionColor = new Color(0.97f, 0.24f, 0.12f);
    public Color tensionColor = new Color(0.18f, 0.60f, 1f);
    public Color neutralColor = new Color(0.65f, 0.67f, 0.71f);
    [Min(0f)] public float neutralForceThresholdN = 0.01f;

    public bool HasModel => model != null;
    public bool HasFEA => model?.memberForcesN != null;
    public string Status { get; private set; }
    public float MaximumDisplacementMm { get; private set; }

    private Model model;
    private int[] deck;
    private BridgeGeometry geometry;
    private Coroutine loading;
    private float blend;
    private float lastExaggeration;
    private float maximumForce;
    private bool previousButton;
    private bool previousMode;
    private readonly List<UnityEngine.XR.InputDevice> controllers = new List<UnityEngine.XR.InputDevice>();

    private void Start() => LoadModel();

    private void OnDestroy()
    {
        geometry?.Dispose();
    }

    private void Update()
    {
        bool desktopToggle = false, desktopReload = false;
#if ENABLE_INPUT_SYSTEM
        desktopToggle = Keyboard.current?.fKey.wasPressedThisFrame == true;
        desktopReload = Keyboard.current?.rKey.wasPressedThisFrame == true;
#elif ENABLE_LEGACY_INPUT_MANAGER
        desktopToggle = Input.GetKeyDown(KeyCode.F);
        desktopReload = Input.GetKeyDown(KeyCode.R);
#endif
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, controllers);
        bool xrButton = false;
        foreach (var controller in controllers)
            xrButton |= controller.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool pressed) && pressed;
        if (desktopToggle || (xrButton && !previousButton)) ToggleFEA();
        previousButton = xrButton;
        if (desktopReload) LoadModel();
        if (geometry == null) return;

        float target = showFEA && HasFEA ? 1f : 0f;
        float next = animateTransition
            ? Mathf.MoveTowards(blend, target, Time.unscaledDeltaTime * transitionSpeed)
            : target;
        bool exaggerationChanged = displacementExaggeration != lastExaggeration;
        if (next != blend || exaggerationChanged)
        {
            blend = next;
            RefreshGeometry();
        }
        if (previousMode != showFEA || exaggerationChanged) RefreshBoard();
        geometry.FaceBoard(Camera.main);
    }

    public void ToggleFEA() => SetFEA(!showFEA);

    public void SetFEA(bool enabled)
    {
        if (enabled && !HasFEA)
        {
            Debug.LogWarning("Load a bridge with FEA results, or with supports and loads to solve.", this);
            return;
        }
        showFEA = enabled;
    }

    [ContextMenu("Load configured model")]
    public void LoadModel()
    {
        StopLoading();
        if (modelJson != null)
        {
            ApplyJson(modelJson.text);
            return;
        }

        string path = Path.Combine(Application.streamingAssetsPath, jsonFileName);
        if (path.Contains("://"))
        {
            loading = StartCoroutine(ReadStreamingModel(path.Replace('\\', '/')));
            return;
        }

        try { ApplyJson(File.ReadAllText(path)); }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private IEnumerator ReadStreamingModel(string uri)
    {
        using (var request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();
            loading = null;
            if (request.result == UnityWebRequest.Result.Success) ApplyJson(request.downloadHandler.text);
            else ReportError(request.error);
        }
    }

    // Can also be called by a world-space input field or an imported TextAsset.
    public void LoadModelJson(string json)
    {
        StopLoading();
        ApplyJson(json);
    }

    [ContextMenu("Load JSON from clipboard (Play mode)")]
    private void LoadClipboard()
    {
        if (Application.isPlaying) LoadModelJson(GUIUtility.systemCopyBuffer);
    }

    private void StopLoading()
    {
        if (loading == null) return;
        StopCoroutine(loading);
        loading = null;
    }

    private void ApplyJson(string json)
    {
        BridgeGeometry candidate = null;
        try
        {
            var incoming = JsonUtility.FromJson<Model>(json);
            int[] incomingDeck = Validate(incoming);
            if (solveOnLoad && incoming.supports != null) Solve(incoming);

            float meterPerMm = displayScale * 0.001f;
            float outerWidth = incoming.modelWidthMm > 0 ? incoming.modelWidthMm * meterPerMm : widthMeters;
            float section = beamThicknessMeters;
            if (!Finite(meterPerMm) || meterPerMm <= 0 || !Finite(outerWidth) || !Finite(section) ||
                section <= 0 || outerWidth <= section * 2)
                throw new ArgumentException("Use a positive display scale and a bridge width exceeding two beam widths.");

            candidate = new BridgeGeometry(transform, steelMaterial, incoming.members.Length,
                incoming.nodes.Length, incomingDeck.Length - 1, outerWidth, section, showStatusBoard);
            model = incoming;
            deck = incomingDeck;
            geometry?.Dispose();
            geometry = candidate;
            candidate = null;
            blend = showFEA && HasFEA && !animateTransition ? 1f : 0f;
            MaximumDisplacementMm = 0f;
            maximumForce = 0f;
            if (HasFEA)
            {
                foreach (var d in model.displacementsMm)
                    MaximumDisplacementMm = Mathf.Max(MaximumDisplacementMm, new Vector2(d.x, d.y).magnitude);
                foreach (float f in model.memberForcesN)
                    maximumForce = Mathf.Max(maximumForce, Mathf.Abs(f));
            }
            Status = FormattableString.Invariant($"{incoming.nodes.Length} joints and {incoming.members.Length} members per side");
            RefreshGeometry();
            RefreshBoard();
            Debug.Log($"Bridge loaded. {Status}. FEA available: {HasFEA}.", this);
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            ReportError(ex.Message);
        }
    }

    private void ReportError(string message)
    {
        Status = message;
        Debug.LogError("Bridge model: " + message, this);
    }

    private static int[] Validate(Model m)
    {
        if (m?.nodes == null || m.nodes.Length < 2 || m.members == null || m.members.Length == 0)
            throw new ArgumentException("Provide at least two nodes and one member.");
        if (m.nodes.Length > 256 || m.members.Length > 1024)
            throw new ArgumentException("The VR viewer supports up to 256 joints and 1024 members per side.");
        foreach (Node n in m.nodes)
            if (n == null || !Finite(n.x) || !Finite(n.y))
                throw new ArgumentException("Every node needs finite x and y coordinates.");

        var unique = new HashSet<(int, int)>();
        foreach (Member e in m.members)
        {
            if (e == null || e.a < 0 || e.b < 0 || e.a >= m.nodes.Length || e.b >= m.nodes.Length)
                throw new ArgumentException("A member refers to a missing node.");
            Node a = m.nodes[e.a], b = m.nodes[e.b];
            if (a.x == b.x && a.y == b.y)
                throw new ArgumentException($"Member {e.a}–{e.b} has zero length.");
            if (!unique.Add((Math.Min(e.a, e.b), Math.Max(e.a, e.b))))
                throw new ArgumentException($"Member {e.a}–{e.b} is duplicated.");
        }

        if ((m.supports == null) != (m.loads == null) ||
            (m.displacementsMm == null) != (m.memberForcesN == null))
            throw new ArgumentException("Provide supports with loads, and displacementsMm with memberForcesN.");
        if (m.displacementsMm != null)
        {
            if (m.displacementsMm.Length != m.nodes.Length || m.memberForcesN.Length != m.members.Length)
                throw new ArgumentException("FEA result arrays must match the node and member counts.");
            foreach (var d in m.displacementsMm)
                if (d == null || !Finite(d.x) || !Finite(d.y))
                    throw new ArgumentException("Displacements must be finite.");
            foreach (float f in m.memberForcesN)
                if (!Finite(f)) throw new ArgumentException("Member forces must be finite.");
        }

        if (!Finite(m.modelWidthMm) || m.modelWidthMm < 0)
            throw new ArgumentException("The model width must be finite and nonnegative.");

        int[] indices = m.deckNodes;
        if (indices == null || indices.Length == 0)
        {
            float floor = float.PositiveInfinity;
            foreach (Node n in m.nodes) floor = Mathf.Min(n.y, floor);
            var baseNodes = new List<int>();
            for (int i = 0; i < m.nodes.Length; i++)
                if (Mathf.Abs(m.nodes[i].y - floor) <= 0.001f) baseNodes.Add(i);
            baseNodes.Sort((a, b) => m.nodes[a].x.CompareTo(m.nodes[b].x));
            indices = baseNodes.ToArray();
        }
        if (indices.Length < 2)
            throw new ArgumentException("Provide at least two deckNodes in increasing x order.");
        float lastX = float.NegativeInfinity;
        foreach (int i in indices)
        {
            if (i < 0 || i >= m.nodes.Length || m.nodes[i].x <= lastX)
                throw new ArgumentException("deckNodes must refer to existing joints with strictly increasing x.");
            lastX = m.nodes[i].x;
        }
        return indices;
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static void Solve(Model m)
    {
        if (!Finite(m.youngsModulusPa) || !Finite(m.memberSideMm) ||
            m.youngsModulusPa <= 0 || m.memberSideMm <= 0)
            throw new ArgumentException("Young’s modulus and member side must be positive.");

        var nodes = new Vector2[m.nodes.Length];
        var members = new int[m.members.Length, 2];
        var modulus = new double[m.members.Length];
        var area = new double[m.members.Length];
        double side = m.memberSideMm * 0.001;
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = new Vector2(m.nodes[i].x * 0.001f, m.nodes[i].y * 0.001f);
        for (int i = 0; i < m.members.Length; i++)
        {
            members[i, 0] = m.members[i].a;
            members[i, 1] = m.members[i].b;
            modulus[i] = m.youngsModulusPa;
            area[i] = side * side;
        }

        var supports = new (int, int, double)[m.supports.Length];
        var loads = new (int, int, double)[m.loads.Length];
        for (int i = 0; i < supports.Length; i++)
        {
            Support s = m.supports[i];
            if (s == null || !Finite(s.valueMm)) throw new ArgumentException("Invalid support displacement.");
            supports[i] = (s.node, s.dir, s.valueMm * 0.001);
        }
        for (int i = 0; i < loads.Length; i++)
        {
            Load l = m.loads[i];
            if (l == null || !Finite(l.forceN)) throw new ArgumentException("Invalid load.");
            loads[i] = (l.node, l.dir, l.forceN);
        }

        TrussResult result = TrussSolver.Solve(nodes, members, modulus, area, supports, loads);
        if (!result.success) throw new ArgumentException("FEA failed. " + result.message);
        m.displacementsMm = new Displacement[nodes.Length];
        m.memberForcesN = new float[m.members.Length];
        for (int i = 0; i < nodes.Length; i++)
            m.displacementsMm[i] = new Displacement
            {
                x = (float)(result.displacements[2 * i] * 1000),
                y = (float)(result.displacements[2 * i + 1] * 1000)
            };
        for (int i = 0; i < m.members.Length; i++)
            m.memberForcesN[i] = (float)result.axialForce[i];
    }

    private Vector3 Position(int i, bool deformed)
    {
        Node n = model.nodes[i];
        float x = n.x, y = n.y;
        if (deformed && HasFEA && blend != 0)
        {
            float factor = blend * displacementExaggeration;
            x += model.displacementsMm[i].x * factor;
            y += model.displacementsMm[i].y * factor;
        }
        return new Vector3(x, y, 0) * (displayScale * 0.001f);
    }

    private void RefreshGeometry()
    {
        lastExaggeration = displacementExaggeration;
        for (int i = 0; i < model.members.Length; i++)
        {
            Member m = model.members[i];
            float force = HasFEA ? model.memberForcesN[i] : 0;
            float intensity = Mathf.Abs(force) <= neutralForceThresholdN ? 0 :
                Mathf.Sqrt(Mathf.Abs(force) / maximumForce);
            Color forceColor = Color.Lerp(neutralColor, force > 0 ? tensionColor : compressionColor, intensity);
            geometry.PlaceMember(i, Position(m.a, true), Position(m.b, true), forceColor, blend);
        }
        for (int i = 0; i < model.nodes.Length; i++)
            geometry.PlaceJoint(i, Position(i, true), blend, neutralColor);
        for (int i = 0; i < deck.Length - 1; i++)
            geometry.PlaceDeck(i, Position(deck[i], true), Position(deck[i + 1], true),
                Position(deck[i], false), Position(deck[i + 1], false), blend, neutralColor);
    }

    private void RefreshBoard()
    {
        previousMode = showFEA;
        Vector3 first = Position(deck[0], false);
        float visualSpan = Position(deck[deck.Length - 1], false).x - first.x;
        string title = string.IsNullOrWhiteSpace(model.title) ? "SE 310  /  TRUSS BRIDGE" : model.title;
        string controls = "F / right primary button: switch view";
        string detail = HasFEA
            ? FormattableString.Invariant($"Max node movement {MaximumDisplacementMm:F3} mm  |  peak |F| {maximumForce:F2} N per side")
            : "No analysis results loaded.";
        geometry.SetBoard(first, visualSpan, title, showFEA && HasFEA
            ? FormattableString.Invariant($"FEA VIEW   /   {displacementExaggeration:g}x displacement")
            : "ORIGINAL BRIDGE",
            detail, controls, showFEA && HasFEA, compressionColor, tensionColor, neutralColor);
    }
}
