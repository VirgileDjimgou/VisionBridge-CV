using Opc.Ua;
using Opc.Ua.Server;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.OpcUa;

/// <summary>
/// OPC-UA StandardServer — nutzt den gemeinsamen VisionService.
/// </summary>
public class VisionOpcUaServer : StandardServer
{
    private readonly VisionService _vision;

    public VisionOpcUaServer(VisionService vision)
    {
        _vision = vision;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager>
        {
            new VisionNodeManager(server, configuration, _vision)
        };

        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }

    protected override ServerProperties LoadServerProperties()
    {
        return new ServerProperties
        {
            ManufacturerName = "VisionBridge",
            ProductName = "VisionBridge Runtime",
            ProductUri = "urn:visionbridge:runtime",
            SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
            BuildNumber = "1.0.0",
            BuildDate = DateTime.UtcNow
        };
    }
}
