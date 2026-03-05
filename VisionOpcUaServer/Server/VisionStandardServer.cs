using Opc.Ua;
using Opc.Ua.Server;

namespace VisionOpcUaServer.Server;

/// <summary>
/// OPC-UA StandardServer mit VisionNodeManager.
/// Kommuniziert direkt mit der C++ DLL — kein HTTP-Umweg.
/// </summary>
public class VisionStandardServer : StandardServer
{
    private readonly bool _manageCamera;

    /// <param name="manageCamera">
    /// true = Server startet/stoppt die Kamera selbst.
    /// false = Kamera wird extern verwaltet.
    /// </param>
    public VisionStandardServer(bool manageCamera = true)
    {
        _manageCamera = manageCamera;
    }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager>
        {
            new VisionNodeManager(server, configuration, _manageCamera)
        };

        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }

    protected override ServerProperties LoadServerProperties()
    {
        return new ServerProperties
        {
            ManufacturerName = "VisionBridge",
            ProductName = "VisionBridge OPC-UA Server",
            ProductUri = "urn:visionbridge:opcua:server",
            SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
            BuildNumber = "1.0.0",
            BuildDate = DateTime.UtcNow
        };
    }
}
