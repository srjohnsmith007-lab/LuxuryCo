using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace LuxuryCo.Database.Data;

public class LuxuryCoDbContextFactory : IDesignTimeDbContextFactory<LuxuryCoDbContext>
{
    public LuxuryCoDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LuxuryCoDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=luxuryco_dev;Username=postgres;Password=postgres");

        return new LuxuryCoDbContext(optionsBuilder.Options);
    }
}
