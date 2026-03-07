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
///     ├── Camera/Running             (Boolean, read)
///     ├── Camera/Start               (Method)
///     ├── Camera/Stop                (Method)
///     ├── Color/Detected             (Boolean)
///     ├── Color/X / Y / Width / Height (Int32)
///     ├── Color/Confidence           (Double)
///     ├── Color/Timestamp            (DateTime)
///     ├── Faces/Count                (Int32)
///     ├── Faces/Confidence           (Double)
///     ├── Circles/Count              (Int32)
///     ├── Circles/Confidence         (Double)
///     ├── Control/ConveyorSpeed      (Double, read/write)
///     ├── Control/InspectionEnabled  (Boolean, read/write)
///     ├── Control/RejectGateOpen     (Boolean, read/write)
///     ├── Diagnostics/Uptime         (String)
///     ├── Diagnostics/BackendMode    (String)
///     ├── Diagnostics/TotalInspections (Int64)
///     └── Diagnostics/CurrentFps     (Double)
/// </summary>
public class VisionNodeManager : CustomNodeManager2
{
    private const string Namespace = "urn:visionbridge:opcua";

    private readonly VisionService _vision;
    private Timer? _pollTimer;

    // Detection (read-only)
    private BaseDataVariableState<bool>? _cameraRunning;
    private BaseDataVariableState<bool>? _colorDetected;
    private BaseDataVariableState<int>? _colorX;
    private BaseDataVariableState<int>? _colorY;
    private BaseDataVariableState<int>? _colorWidth;
    private BaseDataVariableState<int>? _colorHeight;
    private BaseDataVariableState<double>? _colorConfidence;
    private BaseDataVariableState<DateTime>? _colorTimestamp;
    private BaseDataVariableState<int>? _faceCount;
    private BaseDataVariableState<double>? _faceConfidence;
    private BaseDataVariableState<int>? _circleCount;
    private BaseDataVariableState<double>? _circleConfidence;

    // Control (read/write)
    private BaseDataVariableState<double>? _conveyorSpeed;
    private BaseDataVariableState<bool>? _inspectionEnabled;
    private BaseDataVariableState<bool>? _rejectGateOpen;

    // Diagnostics (read-only)
    private BaseDataVariableState<string>? _diagUptime;
    private BaseDataVariableState<string>? _diagBackendMode;
    private BaseDataVariableState<long>? _diagTotalInspections;
    private BaseDataVariableState<double>? _diagCurrentFps;

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

            // ===== Camera (read + Methods) =====
            var cam = CreateFolder(root, "Camera", "Camera");
            _cameraRunning = CreateReadOnlyVariable(cam, "Running", DataTypeIds.Boolean, false);
            CreateMethod(cam, "Start", "Start", (context, method, inputArguments, outputArguments) =>
            {
                var (success, _) = _vision.Start();
                return success ? ServiceResult.Good : new ServiceResult(Opc.Ua.StatusCodes.BadInternalError);
            });
            CreateMethod(cam, "Stop", "Stop", (context, method, inputArguments, outputArguments) =>
            {
                _vision.Stop();
                return ServiceResult.Good;
            });

            // ===== Color (read-only + Timestamp/Confidence) =====
            var color = CreateFolder(root, "Color", "Color");
            _colorDetected = CreateReadOnlyVariable(color, "Detected", DataTypeIds.Boolean, false);
            _colorX = CreateReadOnlyVariable(color, "X", DataTypeIds.Int32, 0);
            _colorY = CreateReadOnlyVariable(color, "Y", DataTypeIds.Int32, 0);
            _colorWidth = CreateReadOnlyVariable(color, "Width", DataTypeIds.Int32, 0);
            _colorHeight = CreateReadOnlyVariable(color, "Height", DataTypeIds.Int32, 0);
            _colorConfidence = CreateReadOnlyVariable(color, "Confidence", DataTypeIds.Double, 0.0);
            _colorTimestamp = CreateReadOnlyVariable(color, "Timestamp", DataTypeIds.DateTime, DateTime.MinValue);

            // ===== Faces (read-only + Confidence) =====
            var faces = CreateFolder(root, "Faces", "Faces");
            _faceCount = CreateReadOnlyVariable(faces, "Count", DataTypeIds.Int32, 0);
            _faceConfidence = CreateReadOnlyVariable(faces, "Confidence", DataTypeIds.Double, 0.0);

            // ===== Circles (read-only + Confidence) =====
            var circles = CreateFolder(root, "Circles", "Circles");
            _circleCount = CreateReadOnlyVariable(circles, "Count", DataTypeIds.Int32, 0);
            _circleConfidence = CreateReadOnlyVariable(circles, "Confidence", DataTypeIds.Double, 0.0);

            // ===== Control (read/write — PLC kann schreiben) =====
            var ctrl = CreateFolder(root, "Control", "Control");
            _conveyorSpeed = CreateWritableVariable(ctrl, "ConveyorSpeed", DataTypeIds.Double, 1.2);
            _conveyorSpeed.OnSimpleWriteValue = OnWriteConveyorSpeed;
            _inspectionEnabled = CreateWritableVariable(ctrl, "InspectionEnabled", DataTypeIds.Boolean, true);
            _inspectionEnabled.OnSimpleWriteValue = OnWriteInspectionEnabled;
            _rejectGateOpen = CreateWritableVariable(ctrl, "RejectGateOpen", DataTypeIds.Boolean, false);
            _rejectGateOpen.OnSimpleWriteValue = OnWriteRejectGate;

            // ===== Diagnostics (read-only) =====
            var diag = CreateFolder(root, "Diagnostics", "Diagnostics");
            _diagUptime = CreateReadOnlyVariable(diag, "Uptime", DataTypeIds.String, "00:00:00");
            _diagBackendMode = CreateReadOnlyVariable(diag, "BackendMode", DataTypeIds.String, "Unknown");
            _diagTotalInspections = CreateReadOnlyVariable(diag, "TotalInspections", DataTypeIds.Int64, 0L);
            _diagCurrentFps = CreateReadOnlyVariable(diag, "CurrentFps", DataTypeIds.Double, 0.0);
        }

        // Polling — liest den VisionService direkt (gleicher Prozess, gleiche DLL)
        _pollTimer = new Timer(UpdateNodes, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    // ===== Write-Callbacks für Control-Knoten =====

    private ServiceResult OnWriteConveyorSpeed(
        ISystemContext context, NodeState node, ref object value)
    {
        _vision.ConveyorSpeed = Convert.ToDouble(value);
        value = _vision.ConveyorSpeed;
        return ServiceResult.Good;
    }

    private ServiceResult OnWriteInspectionEnabled(
        ISystemContext context, NodeState node, ref object value)
    {
        _vision.InspectionEnabled = Convert.ToBoolean(value);
        value = _vision.InspectionEnabled;
        return ServiceResult.Good;
    }

    private ServiceResult OnWriteRejectGate(
        ISystemContext context, NodeState node, ref object value)
    {
        _vision.RejectGateOpen = Convert.ToBoolean(value);
        value = _vision.RejectGateOpen;
        return ServiceResult.Good;
    }

    // ===== Polling =====

    private void UpdateNodes(object? state)
    {
        try
        {
            var status = _vision.GetStatus();
            var colorDto = _vision.DetectColor();
            var facesDto = _vision.DetectFaces();
            var circlesDto = _vision.DetectCircles();
            var diag = _vision.GetDiagnostics();
            var plant = _vision.GetPlantControl();

            lock (Lock)
            {
                SetValue(_cameraRunning, status.Running);

                // Color + Confidence + Timestamp
                if (colorDto != null)
                {
                    SetValue(_colorDetected, colorDto.Detected);
                    SetValue(_colorX, colorDto.BoundingBox?.X ?? 0);
                    SetValue(_colorY, colorDto.BoundingBox?.Y ?? 0);
                    SetValue(_colorWidth, colorDto.BoundingBox?.Width ?? 0);
                    SetValue(_colorHeight, colorDto.BoundingBox?.Height ?? 0);
                    SetValue(_colorConfidence, colorDto.Confidence);
                    SetValue(_colorTimestamp, colorDto.Timestamp ?? DateTime.MinValue);
                }

                // Faces + Confidence
                if (facesDto != null)
                {
                    SetValue(_faceCount, facesDto.Count);
                    SetValue(_faceConfidence, facesDto.Confidence);
                }

                // Circles + Confidence
                if (circlesDto != null)
                {
                    SetValue(_circleCount, circlesDto.Count);
                    SetValue(_circleConfidence, circlesDto.Confidence);
                }

                // Control — liest den aktuellen Zustand zurück (falls via REST geändert)
                SetValue(_conveyorSpeed, plant.ConveyorSpeed);
                SetValue(_inspectionEnabled, plant.InspectionEnabled);
                SetValue(_rejectGateOpen, plant.RejectGateOpen);

                // Diagnostics
                SetValue(_diagUptime, diag.Uptime);
                SetValue(_diagBackendMode, diag.BackendMode);
                SetValue(_diagTotalInspections, diag.TotalInspections);
                SetValue(_diagCurrentFps, diag.CurrentFps);
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

    private BaseDataVariableState<T> CreateReadOnlyVariable<T>(
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

    private BaseDataVariableState<T> CreateWritableVariable<T>(
        FolderState parent, string name, NodeId dataType, T defaultValue)
    {
        var variable = new BaseDataVariableState<T>(parent)
        {
            NodeId = new NodeId($"{parent.NodeId.Identifier}.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Value = defaultValue,
            StatusCode = Opc.Ua.StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private void CreateMethod(FolderState parent, string path, string name,
        GenericMethodCalledEventHandler handler)
    {
        var method = new MethodState(parent)
        {
            NodeId = new NodeId($"{parent.NodeId.Identifier}.{path}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            Executable = true,
            UserExecutable = true
        };

        method.OnCallMethod = handler;
        parent.AddChild(method);
        AddPredefinedNode(SystemContext, method);
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
