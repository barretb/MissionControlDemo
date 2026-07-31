using System.Net.Http.Json;
using OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults: OpenTelemetry, health checks, resilient + service-discovering HttpClients.
builder.AddServiceDefaults();

// Typed/named HttpClient pointed at the API via Aspire service discovery.
// "https+http://missioncontrol-api" resolves to the API resource named in the AppHost.
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri("https+http://missioncontrol-api");
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Serve the single-page dashboard from wwwroot/index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

// -------------------------------------------------------------------------------------------------
// Thin proxy: GET /api/missions -> API GET /api/missions
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/missions", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("api");
    var missions = await client.GetFromJsonAsync<List<MissionDto>>("/api/missions");
    return Results.Ok(missions);
});

// -------------------------------------------------------------------------------------------------
// Thin proxy: GET /api/missions/{id}/launches -> API launch history (Live Telemetry panel).
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/missions/{id:int}/launches", async (int id, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("api");
    var response = await client.GetAsync($"/api/missions/{id}/launches");
    var payload = await response.Content.ReadAsStringAsync();
    return Results.Content(payload, "application/json", statusCode: (int)response.StatusCode);
});

// -------------------------------------------------------------------------------------------------
// Thin proxy: POST /api/missions/reset -> API reset roster.
// -------------------------------------------------------------------------------------------------
app.MapPost("/api/missions/reset", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("api");
    var response = await client.PostAsync("/api/missions/reset", content: null);
    var payload = await response.Content.ReadAsStringAsync();
    return Results.Content(payload, "application/json", statusCode: (int)response.StatusCode);
});

// -------------------------------------------------------------------------------------------------
// GET /api/baggage - Baggage Inspector.
// We set W3C Baggage HERE (Web tier) from the supplied commander/priority, then call the API's
// /api/baggage. The HttpClient OpenTelemetry instrumentation injects it into the outgoing
// "baggage" header, so the API sees it with zero manual plumbing - and we return what it saw.
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/baggage", async (string? commander, string? priority, IHttpClientFactory factory) =>
{
    Baggage.SetBaggage("mission.commander", string.IsNullOrWhiteSpace(commander) ? "Unknown" : commander);
    Baggage.SetBaggage("mission.priority", string.IsNullOrWhiteSpace(priority) ? "Routine" : priority);

    var client = factory.CreateClient("api");
    var payload = await client.GetStringAsync("/api/baggage");
    return Results.Content(payload, "application/json");
});

// -------------------------------------------------------------------------------------------------
// Thin proxy: POST /api/missions/{id}/launch -> API launch.
// BAGGAGE ORIGIN: we set W3C Baggage here (in the Web tier). The HttpClient OpenTelemetry
// instrumentation injects it into the outgoing "baggage" header, so the API receives it
// automatically - no manual header plumbing required.
// -------------------------------------------------------------------------------------------------
app.MapPost("/api/missions/{id:int}/launch", async (
    int id,
    LaunchProxyRequest request,
    IHttpClientFactory factory) =>
{
    // Set baggage at the start of the outgoing operation so it propagates UI -> API.
    Baggage.SetBaggage("mission.commander", string.IsNullOrWhiteSpace(request.Commander) ? "Unknown" : request.Commander);
    Baggage.SetBaggage("mission.priority", string.IsNullOrWhiteSpace(request.Priority) ? "Routine" : request.Priority);

    var client = factory.CreateClient("api");

    var response = await client.PostAsJsonAsync($"/api/missions/{id}/launch", request);
    var payload = await response.Content.ReadAsStringAsync();

    // Forward the API's status + JSON body straight back to the browser.
    return Results.Content(payload, "application/json", statusCode: (int)response.StatusCode);
});

app.Run();

// DTOs for the proxy layer.
record MissionDto(int Id, string Name, string Registry, string Destination, int Crew, string Status);
record LaunchProxyRequest(string? Commander, string? Priority, bool ForceFailure = false);
