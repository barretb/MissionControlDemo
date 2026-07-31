var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL resource + a "missionsdb" database. Runs as a container (Docker required).
// WithDataVolume persists data across runs; pgAdmin gives a quick DB UI in the dashboard.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();

var missionsdb = postgres.AddDatabase("missionsdb");

// The API references the database. WaitFor ensures Postgres is healthy before the API starts.
var api = builder.AddProject<Projects.MissionControl_Api>("missioncontrol-api")
    .WithReference(missionsdb)
    .WaitFor(missionsdb);

// The Web front-end references the API (for service discovery: "https+http://missioncontrol-api").
// WithExternalHttpEndpoints exposes it outside the app network so you can open it in a browser.
builder.AddProject<Projects.MissionControl_Web>("missioncontrol-web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
