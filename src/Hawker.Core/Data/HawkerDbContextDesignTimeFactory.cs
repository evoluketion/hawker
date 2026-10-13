using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hawker.Core.Data;

/// <summary>
/// Lets the EF tools create migrations without starting the app, which would otherwise collide with a running copy of
/// Hawker (the single-instance lock) and needs no real database.
/// </summary>
public sealed class HawkerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HawkerDbContext>
{
    public HawkerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
