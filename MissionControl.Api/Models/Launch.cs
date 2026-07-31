namespace MissionControl.Api.Models;

/// <summary>A single launch attempt for a mission.</summary>
public class Launch
{
    public int Id { get; set; }

    public int MissionId { get; set; }

    /// <summary>Commanding officer, typically read from W3C Baggage (mission.commander).</summary>
    public string Commander { get; set; } = string.Empty;

    /// <summary>Priority, typically read from W3C Baggage (mission.priority).</summary>
    public string Priority { get; set; } = "Routine";

    public DateTime LaunchedAtUtc { get; set; }

    public bool Success { get; set; }
}
