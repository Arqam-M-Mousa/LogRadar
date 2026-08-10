using LogRadar.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogRadar.Infrastructure.Persistence;

public class LogRadarDbContext : DbContext
{
    public LogRadarDbContext(DbContextOptions<LogRadarDbContext> options) : base(options)
    {

    }

    public DbSet<Log> Logs { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LogRadarDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

}
