using MissionControl.Api.Models;

namespace MissionControl.Api.Data;

/// <summary>
/// Seeds and reconciles the starship roster (Star Trek themed).
/// Runs on every startup: adds any missing ships and removes any that are no longer part of the
/// roster, so swapping ships takes effect even against an existing database.
/// </summary>
public static class MissionSeeder
{
    private static readonly Mission[] Roster =
    [
        new() { Name = "Enterprise",   Registry = "NCC-1701",   Destination = "The Final Frontier",  Crew = 430,  Status = "Docked" },
        new() { Name = "Voyager",      Registry = "NCC-74656",  Destination = "Delta Quadrant",      Crew = 141,  Status = "Docked" },
        new() { Name = "Defiant",      Registry = "NX-74205",   Destination = "The Gamma Quadrant",  Crew = 50,   Status = "Docked" },
        new() { Name = "Enterprise-D", Registry = "NCC-1701-D", Destination = "Sector 001 & Beyond", Crew = 1014, Status = "Docked" },
        new() { Name = "Titan",        Registry = "NCC-80102",  Destination = "Deep-Space Survey",   Crew = 350,  Status = "Docked" }
    ];

    public static void Seed(MissionDbContext db)
    {
        var desiredNames = Roster.Select(r => r.Name).ToHashSet();

        // Remove ships no longer on the roster (e.g. Discovery, Deep Space Nine).
        var toRemove = db.Missions.Where(m => !desiredNames.Contains(m.Name)).ToList();
        if (toRemove.Count > 0)
        {
            db.Missions.RemoveRange(toRemove);
        }

        // Add any ships that aren't present yet.
        var existingNames = db.Missions.Select(m => m.Name).ToHashSet();
        foreach (var ship in Roster)
        {
            if (!existingNames.Contains(ship.Name))
            {
                db.Missions.Add(new Mission
                {
                    Name = ship.Name,
                    Registry = ship.Registry,
                    Destination = ship.Destination,
                    Crew = ship.Crew,
                    Status = "Docked"
                });
            }
        }

        db.SaveChanges();
    }
}
