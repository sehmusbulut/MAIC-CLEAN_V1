using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MAIC.Infrastructure.Persistence;

public sealed class MaicDbContextFactory : IDesignTimeDbContextFactory<MaicDbContext>
{
    public MaicDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Maic")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Maic for EF tooling.");
        return new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
    }
}
