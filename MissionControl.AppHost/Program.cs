var builder = DistributedApplication.CreateBuilder(args);

// The API keeps its data in an in-memory SQLite database, so there is no database resource to
// orchestrate: no Docker, and every run starts from a clean "all ships Docked" state.
var api = builder.AddProject<Projects.MissionControl_Api>("missioncontrol-api");

// The Web front-end references the API (for service discovery: "https+http://missioncontrol-api").
// WithExternalHttpEndpoints exposes it outside the app network so you can open it in a browser.
builder.AddProject<Projects.MissionControl_Web>("missioncontrol-web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
