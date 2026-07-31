namespace MissionControl.Api.Models;

/// <summary>Body for POST /api/missions/{id}/launch.</summary>
/// <param name="Commander">Fallback commander if Baggage is absent.</param>
/// <param name="Priority">Fallback priority if Baggage is absent.</param>
/// <param name="ForceFailure">When true, simulates a failed launch (error span + error log + 500).</param>
public record LaunchRequest(string? Commander, string? Priority, bool ForceFailure = false);
