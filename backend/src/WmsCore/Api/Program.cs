using Lagerkraft.WmsCore.Api.Internal;
using Lagerkraft.WmsCore.Api.Tenancy;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHttpClient<IPlatformTenantClient, HttpPlatformTenantClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Platform:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        throw new InvalidOperationException("Platform:BaseUrl is required");
    }

    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var token = sp.GetRequiredService<IConfiguration>()["Internal:Token"] ?? "";
    client.DefaultRequestHeaders.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
});

var app = builder.Build();
app.MapDefaultEndpoints("wms-core");
app.MapInternalApi();
app.Run();

public partial class Program;
