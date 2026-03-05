using Opc.Ua;
using Opc.Ua.Server;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.OpcUa;

/// <summary>
/// OPC-UA NodeManager — erstellt die Vision-Knoten und aktualisiert sie
/// über den gemeinsamen VisionService (kein HTTP-Umweg, kein zweiter DLL-Zugriff).
///
/// Information Model:
///   Objects/Vision
///     ├── Camera/Running       (Boolean)
///     ├── Color/Detected       (Boolean)
///     ├── Color/X              (Int32)
///     ├── Color/Y              (Int32)
///     ├── Color/Width          (Int32)
///     ├── Color/Height         (Int32)
///     ├── Faces/Count          (Int32)
///     └── Circles/Count        (Int32)
/// </summary>
public class VisionNodeManager : CustomNodeManager2
{
    private const string Namespace = "urn:visionbridge:opcua";

    private readonly VisionService _vision;
    private Timer? _pollTimer;

    // Knoten
    private BaseDataVariableState<bool>? _cameraRunning;
    private BaseDataVariableState<bool>? _colorDetected;
    private BaseDataVariableState<int>? _colorX;
    private BaseDataVariableState<int>? _colorY;
    private BaseDataVariableState<int>? _colorWidth;
    private BaseDataVariableState<int>? _colorHeight;
    private BaseDataVariableState<int>? _faceCount;
    private BaseDataVariableState<int>? _circleCount;

    public VisionNodeManager(
        IServerInternal server,
        ApplicationConfiguration config,
        VisionService vision)
        : base(server, config, Namespace)
    {
        _vision = vision;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            base.CreateAddressSpace(externalReferences);

            var root = CreateFolder(null, "Vision", "Vision");
            AddReferenceToRoot(root, externalReferences);

            // Camera
            var cam = CreateFolder(root, "Camera", "Camera");
            _cameraRunning = CreateVariable(cam, "Running", DataTypeIds.Boolean, false);

            // Color
            var color = CreateFolder(root, "Color", "Color");
            _colorDetected = CreateVariable(color, "Detected", DataTypeIds.Boolean, false);
            _colorX = CreateVariable(color, "X", DataTypeIds.Int32, 0);
            _colorY = CreateVariable(color, "Y", DataTypeIds.Int32, 0);
            _colorWidth = CreateVariable(color, "Width", DataTypeIds.Int32, 0);
            _colorHeight = CreateVariable(color, "Height", DataTypeIds.Int32, 0);

            // Faces
            var faces = CreateFolder(root, "Faces", "Faces");
            _faceCount = CreateVariable(faces, "Count", DataTypeIds.Int32, 0);

            // Circles
            var circles = CreateFolder(root, "Circles", "Circles");
            _circleCount = CreateVariable(circles, "Count", DataTypeIds.Int32, 0);
        }

        // Polling — liest den VisionService direkt (gleicher Prozess, gleiche DLL)
        _pollTimer = new Timer(UpdateNodes, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    private void UpdateNodes(object? state)
    {
        try
        {
            var status = _vision.GetStatus();
            var color = _vision.DetectColor();
            var faces = _vision.DetectFaces();
            var circles = _vision.DetectCircles();

            lock (Lock)
            {
                SetValue(_cameraRunning, status.Running);

                if (color != null)
                {
                    SetValue(_colorDetected, color.Detected);
                    SetValue(_colorX, color.BoundingBox?.X ?? 0);
                    SetValue(_colorY, color.BoundingBox?.Y ?? 0);
                    SetValue(_colorWidth, color.BoundingBox?.Width ?? 0);
                    SetValue(_colorHeight, color.BoundingBox?.Height ?? 0);
                }

                if (faces != null)
                    SetValue(_faceCount, faces.Count);

                if (circles != null)
                    SetValue(_circleCount, circles.Count);
            }
        }
        catch
        {
            // Nächster Zyklus versucht es erneut
        }
    }

    // ===== Hilfsmethoden =====

    private FolderState CreateFolder(FolderState? parent, string path, string name)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            TypeDefinitionId = ObjectTypeIds.FolderType
        };

        parent?.AddChild(folder);
        AddPredefinedNode(SystemContext, folder);
        return folder;
    }

    private BaseDataVariableState<T> CreateVariable<T>(
        FolderState parent, string name, NodeId dataType, T defaultValue)
    {
        var variable = new BaseDataVariableState<T>(parent)
        {
            NodeId = new NodeId($"{parent.NodeId.Identifier}.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = defaultValue,
            StatusCode = Opc.Ua.StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private static void SetValue<T>(BaseDataVariableState<T>? node, T value)
    {
        if (node == null) return;
        node.Value = value;
        node.Timestamp = DateTime.UtcNow;
        node.ClearChangeMasks(null, false);
    }

    private static void AddReferenceToRoot(
        FolderState folder, IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
        {
            refs = new List<IReference>();
            externalReferences[ObjectIds.ObjectsFolder] = refs;
        }

        folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _pollTimer?.Dispose();

        base.Dispose(disposing);
    }
}
