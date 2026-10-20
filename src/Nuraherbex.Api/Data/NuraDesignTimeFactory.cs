using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nuraherbex.Api.Data;

// Schema generation never starts the application, reads local secrets, seeds data,
// or contacts couriers. Supply --connection explicitly when applying migrations.
public sealed class NuraDesignTimeFactory : IDesignTimeDbContextFactory<NuraDbContext>
{
    public NuraDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NuraDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Nuraherbex_DesignOnly;Trusted_Connection=True;TrustServerCertificate=True").Options);
}