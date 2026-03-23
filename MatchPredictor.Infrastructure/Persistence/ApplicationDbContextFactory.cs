using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatchPredictor.Infrastructure.Persistence;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string DefaultConnectionString = "Host=localhost;Database=MatchPredictor;Username=postgres;Password=postgres";
    private const string WebUserSecretsId = "ac60258b-53bf-4c53-a156-c1fdc2c27a4a";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(ResolveConnectionString());
        return new ApplicationDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString()
    {
        var environmentConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(environmentConnectionString))
        {
            return environmentConnectionString;
        }

        var userSecretsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".microsoft",
            "usersecrets",
            WebUserSecretsId,
            "secrets.json");

        if (File.Exists(userSecretsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(userSecretsPath));
            if (document.RootElement.TryGetProperty("ConnectionStrings:DefaultConnection", out var connectionStringElement))
            {
                var secretConnectionString = connectionStringElement.GetString();
                if (!string.IsNullOrWhiteSpace(secretConnectionString))
                {
                    return secretConnectionString;
                }
            }
        }

        return DefaultConnectionString;
    }
}
