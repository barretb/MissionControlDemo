using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MissionControl.ServiceDefaults;

/// <summary>
/// The single, well-known home for Mission Control's custom telemetry primitives.
///
/// TALK HIGHLIGHT #1 - shared names:
///   Both the <see cref="ActivitySource"/> (traces) and the <see cref="Meter"/> (metrics) use the
///   same well-known name "MissionControl.Telemetry". ServiceDefaults registers that name with
///   OpenTelemetry (AddSource / AddMeter), so anything created here is automatically exported.
/// </summary>
public static class MissionTelemetry
{
    /// <summary>Well-known name registered via <c>.WithTracing(t =&gt; t.AddSource(...))</c>.</summary>
    public const string ActivitySourceName = "MissionControl.Telemetry";

    /// <summary>Well-known name registered via <c>.WithMetrics(m =&gt; m.AddMeter(...))</c>.</summary>
    public const string MeterName = "MissionControl.Telemetry";

    /// <summary>
    /// The shared ActivitySource used to create manual spans (e.g. "LaunchMission", "db.launch.insert").
    /// Create spans from this anywhere in the app; ServiceDefaults ensures they are exported.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// TALK HIGHLIGHT #2 - reusable span factory.
    ///
    /// Starts an <see cref="ActivityKind.Internal"/> span and auto-tags it with the public,
    /// readable properties of <paramref name="entity"/> using the convention <c>db.entity.{property}</c>.
    /// This turns "add a span around a DB write" into a one-liner that also captures the entity shape.
    ///
    /// Safety: returns the (possibly null) activity if the source has no listeners, skips null values,
    /// skips indexers/write-only props, and never lets reflection throw into the caller.
    /// </summary>
    /// <typeparam name="T">The entity type whose properties become span tags.</typeparam>
    /// <param name="source">The ActivitySource to start the span on (typically <see cref="ActivitySource"/>).</param>
    /// <param name="name">The span name, e.g. "db.launch.insert".</param>
    /// <param name="entity">The entity whose public properties are tagged onto the span.</param>
    /// <returns>The started <see cref="Activity"/>, or <c>null</c> when nobody is listening.</returns>
    public static Activity? StartDatabaseSpan<T>(this ActivitySource source, string name, T entity)
    {
        // If no listener is registered (not sampled), StartActivity returns null - fast no-op.
        var activity = source.StartActivity(name, ActivityKind.Internal);
        if (activity is null || entity is null)
        {
            return activity;
        }

        // Only pay the reflection cost when the span is actually being recorded.
        if (!activity.IsAllDataRequested)
        {
            return activity;
        }

        try
        {
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in properties)
            {
                // Skip write-only properties and indexers.
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(entity);
                }
                catch
                {
                    // A misbehaving getter should never break telemetry.
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                // Convention: db.entity.{lowercasedPropertyName}
                activity.SetTag($"db.entity.{property.Name.ToLowerInvariant()}", value);
            }
        }
        catch
        {
            // Reflection is best-effort: telemetry must never affect business behavior.
        }

        return activity;
    }
}

/// <summary>
/// TALK HIGHLIGHT #3 - a custom metric wrapped in a DI singleton.
///
/// Wraps a <see cref="Counter{T}"/> named "missions_launched". Using <see cref="IMeterFactory"/>
/// (the recommended pattern) keeps the Meter tied to DI lifetime and testable.
/// </summary>
public sealed class MissionMetrics
{
    private readonly Counter<long> _missionsLaunched;

    public MissionMetrics(IMeterFactory meterFactory)
    {
        // The meter name matches MissionTelemetry.MeterName, which ServiceDefaults registered via AddMeter.
        var meter = meterFactory.Create(MissionTelemetry.MeterName);

        _missionsLaunched = meter.CreateCounter<long>(
            name: "missions_launched",
            unit: "{mission}",
            description: "Number of starship missions launched from Mission Control.");
    }

    /// <summary>
    /// Increments the missions_launched counter, tagged with the ship/mission name and outcome
    /// so the Aspire dashboard can slice the metric by ship and success/failure.
    /// </summary>
    public void MissionLaunched(string missionName, bool success)
    {
        _missionsLaunched.Add(
            1,
            new KeyValuePair<string, object?>("mission.name", missionName),
            new KeyValuePair<string, object?>("mission.success", success));
    }
}

/// <summary>DI helpers for the custom metrics service.</summary>
public static class MissionMetricsExtensions
{
    /// <summary>
    /// Registers <see cref="MissionMetrics"/> as a singleton. Call this from a service that
    /// increments the counter (the API). The underlying meter is exported because ServiceDefaults
    /// already registered <see cref="MissionTelemetry.MeterName"/> with OpenTelemetry.
    /// </summary>
    public static IServiceCollection AddMissionMetrics(this IServiceCollection services)
    {
        services.AddSingleton<MissionMetrics>();
        return services;
    }
}
