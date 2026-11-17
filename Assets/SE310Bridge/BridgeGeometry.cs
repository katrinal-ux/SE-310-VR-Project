using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class BridgeGeometry
{
    private readonly GameObject root;
    private readonly float halfSideDistance;
    private readonly float deckWidth;
    private readonly float section;
    private readonly Mesh beamMesh;
    private readonly Mesh connectionMesh;
    private readonly Material steel;
    private readonly Material deckSteel;
    private readonly Material analysis;
    private readonly Material boardMaterial;
    private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    private readonly Piece[] sides;
    private readonly Piece[] crossbars;
    private readonly Piece[] connections;
    private readonly Piece[] panels;
    private readonly Transform[] floors;
    private readonly Transform board;
    private readonly TextMesh boardText;

    private sealed class Piece
    {
        public Transform Transform;
        public Renderer Renderer;
        public Material NormalMaterial;
        public bool Analysis;
    }

    public BridgeGeometry(Transform parent, Material finish, int memberCount, int jointCount,
        int bayCount, float outerWidth, float section, bool withBoard)
    {
        this.section = section;
        halfSideDistance = (outerWidth - section) * 0.5f;
        deckWidth = outerWidth - section * 0.8f;
        Material standard = Resources.Load<Material>("SE310_WeatheredSteel");
        analysis = Resources.Load<Material>("SE310_ForceColor");
        Material baseFinish = finish != null ? finish : standard;
        if (baseFinish == null || analysis == null)
            throw new System.InvalidOperationException("The bridge materials are missing. Import the complete bridge package.");

        steel = new Material(baseFinish);
        deckSteel = new Material(baseFinish);
        if (deckSteel.HasProperty("_BaseColor"))
            deckSteel.SetColor("_BaseColor", deckSteel.GetColor("_BaseColor") * new Color(0.67f, 0.73f, 0.76f, 1f));

        beamMesh = MakeBeamMesh();
        connectionMesh = MakeConnectionMesh();
        root = new GameObject("Generated Steel Truss");
        root.transform.SetParent(parent, false);

        sides = new Piece[2 * memberCount];
        for (int i = 0; i < memberCount; i++)
        {
            sides[2 * i] = CreatePiece($"Member {i:00} • near", steel, beamMesh);
            sides[2 * i + 1] = CreatePiece($"Member {i:00} • far", steel, beamMesh);
        }
        crossbars = new Piece[jointCount];
        connections = new Piece[2 * jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            crossbars[i] = CreatePiece($"Cross beam {i:00}", deckSteel, beamMesh);
            connections[2 * i] = CreatePiece($"Connection {i:00} • near", steel, connectionMesh);
            connections[2 * i + 1] = CreatePiece($"Connection {i:00} • far", steel, connectionMesh);
        }
        panels = new Piece[bayCount];
        floors = new Transform[bayCount];
        for (int i = 0; i < bayCount; i++)
        {
            panels[i] = CreatePiece($"Deck panel {i:00}", deckSteel);
            var floor = new GameObject($"Walkable deck {i:00}");
            floor.transform.SetParent(root.transform, false);
            floor.AddComponent<BoxCollider>();
            floors[i] = floor.transform;
        }

        if (!withBoard) return;
        boardMaterial = new Material(analysis);
        boardMaterial.SetColor("_BaseColor", new Color(0.045f, 0.055f, 0.072f));
        board = new GameObject("Bridge interpretation panel").transform;
        board.SetParent(root.transform, false);
        Piece backdrop = CreatePiece("Panel backing", boardMaterial);
        backdrop.Renderer.shadowCastingMode = ShadowCastingMode.Off;
        backdrop.Transform.SetParent(board, false);
        backdrop.Transform.localPosition = new Vector3(0f, 0f, 0.035f);
        backdrop.Transform.localScale = new Vector3(3.08f, 0.83f, 0.05f);

        var label = new GameObject("Reading");
        label.transform.SetParent(board, false);
        label.transform.localPosition = new Vector3(-1.41f, 0.30f, 0f);
        boardText = label.AddComponent<TextMesh>();
        boardText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<Renderer>().sharedMaterial = boardText.font.material;
        label.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        boardText.fontSize = 54;
        boardText.characterSize = 0.057f;
        boardText.anchor = TextAnchor.UpperLeft;
        boardText.richText = true;
        boardText.color = new Color(0.94f, 0.96f, 0.99f);
    }

    public void PlaceMember(int index, Vector3 a, Vector3 b, Color color, float blend)
    {
        PlaceSide(sides[2 * index], a, b, -halfSideDistance, color, blend);
        PlaceSide(sides[2 * index + 1], a, b, halfSideDistance, color, blend);
    }

    private void PlaceSide(Piece beam, Vector3 a, Vector3 b, float z, Color color, float blend)
    {
        a.z = b.z = z;
        Vector3 direction = b - a;
        float length = direction.magnitude;
        beam.Transform.localPosition = (a + b) * 0.5f;
        // Keep the flange width aligned with the bridge's cross-span axis.
        beam.Transform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(-direction.x, direction.y) * Mathf.Rad2Deg);
        beam.Transform.localScale = new Vector3(section, Mathf.Max(length, 0.0001f), section);
        Tint(beam, color, blend);
    }

    public void PlaceJoint(int index, Vector3 position, float blend, Color neutral)
    {
        Piece crossbar = crossbars[index];
        crossbar.Transform.localPosition = position;
        crossbar.Transform.localRotation = Quaternion.Euler(90, 0, 0);
        crossbar.Transform.localScale = new Vector3(section * 0.75f, 2 * halfSideDistance, section * 0.75f);
        Tint(crossbar, neutral * 0.70f, blend);

        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? -1 : 1;
            Piece connection = connections[2 * index + side];
            connection.Transform.localPosition = position + new Vector3(0, 0, sign * (halfSideDistance + 0.38f * section));
            connection.Transform.localRotation = Quaternion.Euler(0, side == 0 ? 180 : 0, 0);
            connection.Transform.localScale = Vector3.one * section;
            Tint(connection, neutral * 0.88f, blend);
        }
    }

    public void PlaceDeck(int index, Vector3 a, Vector3 b, Vector3 fixedA, Vector3 fixedB, float blend, Color neutral)
    {
        float thickness = section * 0.37f;
        float offset = section * 0.26f;
        PlacePanel(panels[index].Transform, a, b, thickness, offset, section * 0.06f);
        PlacePanel(floors[index], fixedA, fixedB, thickness, offset, 0);
        Tint(panels[index], neutral * 0.57f, blend);
    }

    private void PlacePanel(Transform panel, Vector3 a, Vector3 b, float depth, float offset, float gap)
    {
        Vector3 direction = b - a;
        float length = direction.magnitude;
        Quaternion rotation = Quaternion.Euler(0, 0, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
        panel.localPosition = (a + b) * 0.5f + rotation * Vector3.up * offset;
        panel.localRotation = rotation;
        panel.localScale = new Vector3(Mathf.Max(length - gap, 0.0001f), depth, deckWidth);
    }

    public void SetBoard(Vector3 entrance, float visualSpan, string title, string mode, string details,
        string controls, bool showLegend, Color compression, Color tension, Color neutral)
    {
        if (board == null) return;
        float scale = Mathf.Clamp(visualSpan / 10f, 0.22f, 1.45f);
        board.localScale = Vector3.one * scale;
        board.localPosition = entrance + new Vector3(-1.45f, 1.00f, -halfSideDistance - 0.68f) * scale
            + new Vector3(0, 0, -halfSideDistance * (1f - scale));
        string legend = showLegend
            ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(tension)}>+ Tension</color>   " +
              $"<color=#{ColorUtility.ToHtmlStringRGB(compression)}>− Compression</color>   " +
              $"<color=#{ColorUtility.ToHtmlStringRGB(neutral)}>Gray: low force / visual-only parts</color>"
            : "\nWeathered steel finish   /   2D balsa truss model";
        boardText.text = $"<b>{title}</b>\n{mode}\n{details}{legend}\n{controls}";
    }

    public void FaceBoard(Camera camera)
    {
        if (board == null || camera == null) return;
        Vector3 away = board.position - camera.transform.position;
        away = Vector3.ProjectOnPlane(away, board.parent.up);
        if (away.sqrMagnitude > 0.0001f)
            board.rotation = Quaternion.LookRotation(away, board.parent.up);
    }

    private Piece CreatePiece(string label, Material material, Mesh mesh = null)
    {
        GameObject obj;
        if (mesh != null)
        {
            obj = new GameObject(label);
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            obj.AddComponent<MeshRenderer>();
        }
        else
        {
            obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = label;
            Collider extraCollider = obj.GetComponent<Collider>();
            extraCollider.enabled = false;
            Object.Destroy(extraCollider);
        }
        obj.transform.SetParent(root.transform, false);
        var renderer = obj.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        return new Piece { Transform = obj.transform, Renderer = renderer, NormalMaterial = material };
    }

    private void Tint(Piece piece, Color target, float blend)
    {
        bool fea = blend > 0;
        if (piece.Analysis != fea)
        {
            piece.Analysis = fea;
            piece.Renderer.sharedMaterial = fea ? analysis : piece.NormalMaterial;
        }
        if (!fea)
        {
            piece.Renderer.SetPropertyBlock(null);
            return;
        }

        properties.SetColor("_BaseColor", Color.Lerp(new Color(0.43f, 0.38f, 0.33f), target, blend));
        piece.Renderer.SetPropertyBlock(properties);
    }

    public void Dispose()
    {
        root.SetActive(false);
        Object.Destroy(root);
        Object.Destroy(beamMesh);
        Object.Destroy(connectionMesh);
        Object.Destroy(steel);
        Object.Destroy(deckSteel);
        if (boardMaterial != null) Object.Destroy(boardMaterial);
    }

    private static Mesh MakeBeamMesh()
    {
        var builder = new MeshBuilder();
        builder.AddBox(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.32f, 0.5f, 0.5f));
        builder.AddBox(new Vector3(0.32f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f));
        builder.AddBox(new Vector3(-0.32f, -0.5f, -0.115f), new Vector3(0.32f, 0.5f, 0.115f));
        return builder.Build("Rolled I-section");
    }

    private static Mesh MakeConnectionMesh()
    {
        var builder = new MeshBuilder();
        builder.AddBox(new Vector3(-0.80f, -0.74f, -0.14f), new Vector3(0.80f, 0.74f, 0.17f));
        foreach (float x in new[] { -0.47f, 0.47f })
            foreach (float y in new[] { -0.40f, 0.40f })
                builder.AddHexHead(x, y);
        return builder.Build("Gusset and hex bolts");
    }

    private sealed class MeshBuilder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int> triangles = new List<int>();

        public void AddBox(Vector3 min, Vector3 max)
        {
            var a = new Vector3(min.x, min.y, min.z);
            var b = new Vector3(max.x, min.y, min.z);
            var c = new Vector3(max.x, max.y, min.z);
            var d = new Vector3(min.x, max.y, min.z);
            var e = new Vector3(min.x, min.y, max.z);
            var f = new Vector3(max.x, min.y, max.z);
            var g = new Vector3(max.x, max.y, max.z);
            var h = new Vector3(min.x, max.y, max.z);
            Quad(a, d, c, b);
            Quad(e, f, g, h);
            Quad(a, e, h, d);
            Quad(b, c, g, f);
            Quad(a, b, f, e);
            Quad(d, h, g, c);
        }

        public void AddHexHead(float x, float y)
        {
            const float radius = 0.145f;
            var center = new Vector3(x, y, 0.27f);
            for (int i = 0; i < 6; i++)
            {
                float first = i * Mathf.PI / 3f, next = (i + 1) * Mathf.PI / 3f;
                Vector3 a = center + radius * new Vector3(Mathf.Cos(first), Mathf.Sin(first), 0);
                Vector3 b = center + radius * new Vector3(Mathf.Cos(next), Mathf.Sin(next), 0);
                Vector3 down = new Vector3(0, 0, -0.11f);
                Quad(a + down, b + down, b, a);
                int start = vertices.Count;
                vertices.AddRange(new[] { center, a, b });
                uv.AddRange(new[] { new Vector2(0.7f, 0.7f), new Vector2(0.72f, 0.70f), new Vector2(0.72f, 0.72f) });
                triangles.AddRange(new[] { start, start + 1, start + 2 });
            }
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = vertices.Count;
            vertices.AddRange(new[] { a, b, c, d });
            uv.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
            triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
