using VisionOpcUaServer;

const int opcPort = 4840;

Console.WriteLine("=== VisionBridge OPC-UA Server ===");
Console.WriteLine();
Console.WriteLine($"  Endpoint:  opc.tcp://localhost:{opcPort}/visionbridge");
Console.WriteLine($"  DLL:       NeuroCComVision.dll (P/Invoke direct)");
Console.WriteLine();

try
{
    var server = await VisionServerBootstrap.StartAsync(opcPort, manageCamera: true);

    Console.WriteLine("[OK] OPC-UA Server gestartet.");
    Console.WriteLine();
    Console.WriteLine("Information Model:");
    Console.WriteLine("  Vision/Camera/Running       (Boolean)");
    Console.WriteLine("  Vision/Color/Detected       (Boolean)");
    Console.WriteLine("  Vision/Color/X              (Int32)");
    Console.WriteLine("  Vision/Color/Y              (Int32)");
    Console.WriteLine("  Vision/Color/Width          (Int32)");
    Console.WriteLine("  Vision/Color/Height         (Int32)");
    Console.WriteLine("  Vision/Faces/Count          (Int32)");
    Console.WriteLine("  Vision/Circles/Count        (Int32)");
    Console.WriteLine();
    Console.WriteLine("Polling DLL alle 250ms...");
    Console.WriteLine("Enter drücken zum Beenden.");
    Console.ReadLine();

    server.Stop();
    Console.WriteLine("[OK] Server gestoppt.");
}
catch (Exception ex)
{
    Console.WriteLine($"[FEHLER] {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Prüfen Sie:");
    Console.WriteLine("  - NeuroCComVision.dll im Ausgabeverzeichnis vorhanden?");
    Console.WriteLine("  - OpenCV-DLLs erreichbar?");
}
