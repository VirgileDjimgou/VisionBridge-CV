using REST_API_NeuroC_Prep.OpcUa;
using REST_API_NeuroC_Prep.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "VisionBridge Runtime",
        Version = "v1",
        Description = "Vision Controller — REST API + OPC-UA Server. " +
                      "Ein Prozess, eine Kamera, zwei Protokolle."
    });
});

// VisionService als Singleton — eine Kamera, eine Instanz
builder.Services.AddSingleton<VisionService>();

// OPC-UA Server als HostedService — teilt sich den VisionService
builder.Services.AddHostedService<OpcUaHostedService>();

var app = builder.Build();

// --- Pipeline ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "NeuroC Vision API";
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Kamera beim Beenden sauber freigeben
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<VisionService>().Dispose();
});

app.Run();
