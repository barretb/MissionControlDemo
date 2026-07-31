namespace MissionControl.Api.Models;

/// <summary>A starship mission on the roster.</summary>
public class Mission
{
    public int Id { get; set; }

    /// <summary>Ship / mission name, e.g. "Enterprise".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Starfleet registry, e.g. "NCC-1701".</summary>
    public string Registry { get; set; } = string.Empty;

    /// <summary>Where the mission is headed.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Crew complement.</summary>
    public int Crew { get; set; }

    /// <summary>Current mission status, e.g. "Docked", "Launched", "Launch Failure".</summary>
    public string Status { get; set; } = "Docked";
}
