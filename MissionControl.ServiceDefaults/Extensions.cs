using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MissionControl.ServiceDefaults;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Placed in the Microsoft.Extensions.Hosting namespace (classic Aspire ServiceDefaults convention)
// so that any project can just call builder.AddServiceDefaults() with no extra using.
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Classic Aspire "ServiceDefaults" shared configuration:
/// OpenTelemetry, health checks, service discovery and resilient HttpClients.
/// This is the single place every service opts into the same observability story.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Wires up everything a service needs to behave well inside Aspire:
    /// telemetry, health checks, service discovery and resilient HttpClients.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience (retries, circuit breaker, timeouts) by default.
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default (resolve "https+http://api" style names).
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry logging, metrics and tracing.
    /// This is the OTel "hub" for the talk: the built-in instrumentation is registered here,
    /// and so are our custom Meter and ActivitySource (see <see cref="MissionTelemetry"/>).
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // ---- LOGS ----
        // Emit ILogger output as OpenTelemetry log records. Because these records flow through
        // the same pipeline as spans, each log automatically carries TraceId / SpanId.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            // ---- METRICS ----
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()   // incoming HTTP request metrics
                    .AddHttpClientInstrumentation()   // outgoing HTTP client metrics
                    .AddRuntimeInstrumentation()      // GC, threadpool, etc.
                    // Register OUR custom meter so the missions_launched counter is exported.
                    .AddMeter(MissionTelemetry.MeterName);
            })
            // ---- TRACES ----
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(builder.Environment.ApplicationName)
                    // Register OUR ActivitySource so manual spans (LaunchMission, db.*) are exported.
                    .AddSource(MissionTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()   // server spans for incoming requests
                    .AddHttpClientInstrumentation()   // client spans + W3C context/baggage propagation
                    .AddEntityFrameworkCoreInstrumentation();  // DB spans for EF Core queries (works with SQLite)
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// Enables the OTLP exporter whenever OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// Aspire sets this environment variable automatically, so all signals flow to the Aspire dashboard.
    /// </summary>
    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            // Exports logs, metrics AND traces over OTLP in one call.
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Adds a default "self" liveness health check.</summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Liveness: the app is up and responding.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps /health (readiness) and /alive (liveness) endpoints.
    /// Only mapped in Development to avoid exposing them publicly by default.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for readiness.
            app.MapHealthChecks(HealthEndpointPath);

            // Only "live"-tagged checks must pass for liveness.
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
