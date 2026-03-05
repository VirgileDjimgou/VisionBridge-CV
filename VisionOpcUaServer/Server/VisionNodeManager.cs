using Opc.Ua;
using Opc.Ua.Server;
using VisionOpcUaServer.Interop;

namespace VisionOpcUaServer.Server;

/// <summary>
/// OPC-UA NodeManager — erstellt und aktualisiert die Vision-Knoten.
/// Kommuniziert direkt mit der C++ DLL via P/Invoke (kein HTTP-Umweg).
///
/// Information Model:
///
///   Objects
///   └── Vision
///       ├── Camera
///       │   └── Running          (Boolean)
///       ├── Color
///       │   ├── Detected         (Boolean)
///       │   ├── X                (Int32)
///       │   ├── Y                (Int32)
///       │   ├── Width            (Int32)
///       │   └── Height           (Int32)
///       ├── Faces
///       │   └── Count            (Int32)
///       └── Circles
///           └── Count            (Int32)
/// </summary>
public class VisionNodeManager : CustomNodeManager2
{
    private const string Namespace = "urn:visionbridge:opcua";

    // Kamera
    private BaseDataVariableState<bool>? _cameraRunning;

    // Farberkennung
    private BaseDataVariableState<bool>? _colorDetected;
    private BaseDataVariableState<int>? _colorX;
    private BaseDataVariableState<int>? _colorY;
    private BaseDataVariableState<int>? _colorWidth;
    private BaseDataVariableState<int>? _colorHeight;

    // Gesichter
    private BaseDataVariableState<int>? _faceCount;

    // Kreise
    private BaseDataVariableState<int>? _circleCount;

    private readonly bool _manageCamera;
    private Timer? _pollTimer;

    /// <param name="manageCamera">
    /// true = Server startet/stoppt die Kamera selbst.
    /// false = Kamera wird extern verwaltet (z.B. WPF Client läuft parallel).
    /// </param>
    public VisionNodeManager(IServerInternal server, ApplicationConfiguration config, bool manageCamera)
        : base(server, config, Namespace)
    {
        _manageCamera = manageCamera;
    }

    // ===== Knoten erstellen =====

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            base.CreateAddressSpace(externalReferences);

            // Stammordner: Vision
            var visionFolder = CreateFolder(null, "Vision", "Vision");
            AddReferenceToRoot(visionFolder, externalReferences);

            // Camera
            var cameraFolder = CreateFolder(visionFolder, "Camera", "Camera");
            _cameraRunning = CreateVariable(cameraFolder, "Running", "Running", DataTypeIds.Boolean, false);

            // Color
            var colorFolder = CreateFolder(visionFolder, "Color", "Color");
            _colorDetected = CreateVariable(colorFolder, "Detected", "Detected", DataTypeIds.Boolean, false);
            _colorX = CreateVariable(colorFolder, "X", "X", DataTypeIds.Int32, 0);
            _colorY = CreateVariable(colorFolder, "Y", "Y", DataTypeIds.Int32, 0);
            _colorWidth = CreateVariable(colorFolder, "Width", "Width", DataTypeIds.Int32, 0);
            _colorHeight = CreateVariable(colorFolder, "Height", "Height", DataTypeIds.Int32, 0);

            // Faces
            var facesFolder = CreateFolder(visionFolder, "Faces", "Faces");
            _faceCount = CreateVariable(facesFolder, "Count", "Count", DataTypeIds.Int32, 0);

            // Circles
            var circlesFolder = CreateFolder(visionFolder, "Circles", "Circles");
            _circleCount = CreateVariable(circlesFolder, "Count", "Count", DataTypeIds.Int32, 0);
        }

        // Kamera starten falls gewünscht
        if (_manageCamera)
        {
            if (NativeInterop.StartCamera())
            {
                Console.WriteLine("[OK] Kamera gestartet (via DLL)");
                TryLoadCascade();
            }
            else
            {
                Console.WriteLine("[WARN] Kamera konnte nicht gestartet werden");
            }
        }

        // Polling starten — direkt auf die DLL
        _pollTimer = new Timer(PollNativeCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    // ===== DLL direkt pollen =====

    private void PollNativeCallback(object? state)
    {
        try
        {
            // Farberkennung
            bool colorOk = NativeInterop.GetFrame(out var colorResult);

            // Gesichtserkennung
            bool facesOk = NativeInterop.DetectFaces(out var facesResult);

            // Kreiserkennung
            bool circlesOk = NativeInterop.DetectCircles(out var circlesResult);

            // Kamera läuft wenn mindestens ein Aufruf Daten liefert
            bool running = colorOk || facesOk || circlesOk;

            lock (Lock)
            {
                UpdateVariable(_cameraRunning, running);

                if (colorOk)
                {
                    UpdateVariable(_colorDetected, colorResult.detected);
                    UpdateVariable(_colorX, colorResult.detected ? colorResult.x : 0);
                    UpdateVariable(_colorY, colorResult.detected ? colorResult.y : 0);
                    UpdateVariable(_colorWidth, colorResult.detected ? colorResult.width : 0);
                    UpdateVariable(_colorHeight, colorResult.detected ? colorResult.height : 0);
                }

                if (facesOk)
                {
                    UpdateVariable(_faceCount, facesResult.count);
                }

                if (circlesOk)
                {
                    UpdateVariable(_circleCount, circlesResult.count);
                }
            }
        }
        catch
        {
            // Polling-Fehler ignorieren — nächster Zyklus versucht es erneut
        }
    }

    // ===== Cascade laden =====

    private static void TryLoadCascade()
    {
        string cascadePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "haarcascade_frontalface_default.xml");

        if (File.Exists(cascadePath) && NativeInterop.LoadFaceCascade(cascadePath))
        {
            Console.WriteLine("[OK] Haar-Cascade geladen");
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
        FolderState parent, string path, string name, NodeId dataType, T defaultValue)
    {
        var variable = new BaseDataVariableState<T>(parent)
        {
            NodeId = new NodeId($"{parent.NodeId.Identifier}.{path}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = defaultValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private void UpdateVariable<T>(BaseDataVariableState<T>? variable, T value)
    {
        if (variable == null) return;

        variable.Value = value;
        variable.Timestamp = DateTime.UtcNow;
        variable.ClearChangeMasks(SystemContext, false);
    }

    private void AddReferenceToRoot(FolderState folder, IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
        {
            references = new List<IReference>();
            externalReferences[ObjectIds.ObjectsFolder] = references;
        }

        folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer?.Dispose();

            if (_manageCamera)
            {
                NativeInterop.StopCamera();
                Console.WriteLine("[OK] Kamera gestoppt");
            }
        }
        base.Dispose(disposing);
    }
}
