using Microsoft.EntityFrameworkCore;
using MissionControl.Api.Models;

namespace MissionControl.Api.Data;

public class MissionDbContext(DbContextOptions<MissionDbContext> options) : DbContext(options)
{
    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<Launch> Launches => Set<Launch>();
}
