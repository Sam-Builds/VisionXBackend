using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UIBackend.Infrastructure.Persistence;

public class UIBackendDbContextFactory : IDesignTimeDbContextFactory<UIBackendDbContext>
{
    public UIBackendDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UIBackendDbContext>();
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__UIBackendDatabase")
            ?? "Host=localhost;Port=5436;Database=visionx_uibackend;Username=postgres;Password=postgres";

        optionsBuilder.UseNpgsql(connStr, b => b.MigrationsAssembly(typeof(UIBackendDbContext).Assembly.FullName));

        return new UIBackendDbContext(optionsBuilder.Options);
    }
}
