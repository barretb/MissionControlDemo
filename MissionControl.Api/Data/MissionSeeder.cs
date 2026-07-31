using MissionControl.Api.Models;

namespace MissionControl.Api.Data;

/// <summary>Seeds the initial starship roster (Star Trek themed).</summary>
public static class MissionSeeder
{
    public static void Seed(MissionDbContext db)
    {
        if (db.Missions.Any())
        {
            return;
        }

        db.Missions.AddRange(
            new Mission { Name = "Enterprise",      Registry = "NCC-1701",  Destination = "The Final Frontier",  Crew = 430, Status = "Docked" },
            new Mission { Name = "Voyager",         Registry = "NCC-74656", Destination = "Delta Quadrant",      Crew = 141, Status = "Docked" },
            new Mission { Name = "Discovery",       Registry = "NCC-1031",  Destination = "The Mycelial Network", Crew = 136, Status = "Docked" },
            new Mission { Name = "Defiant",         Registry = "NX-74205",  Destination = "The Gamma Quadrant",  Crew = 50,  Status = "Docked" },
            new Mission { Name = "Deep Space Nine", Registry = "DS9",       Destination = "The Bajoran Wormhole", Crew = 300, Status = "Docked" }
        );

        db.SaveChanges();
    }
}
