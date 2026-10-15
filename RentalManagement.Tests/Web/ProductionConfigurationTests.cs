// =====================================================================================================================
// Module: RentalManagement.Tests / Web / ProductionConfigurationTests.cs
// Purpose: Guards that development and test data never reach a deployed site: demo seeding is off by default and is
//          turned on (with the known demo password) only by appsettings.Development.json, and the production
//          configuration carries no connection string, seed password or seeding switch.
// =====================================================================================================================

using Microsoft.Extensions.Configuration;
using RentalManagement.Core.Data.Seeding;

namespace RentalManagement.Tests.Web;

/// <summary>Tests of the shipped configuration files and seeding defaults.</summary>
public class ProductionConfigurationTests
{
    /// <summary>Seeding is opt-in: a configuration without a Seed section seeds nothing.</summary>
    [Fact]
    public void Seeding_is_off_unless_configured() =>
        Assert.False(new SeedOptions().Enabled);

    /// <summary>
    /// The configuration a deployed site gets (appsettings.json only, environment = Production) doesn't seed, has no
    /// demo password, and has no connection string (that must come from the environment or a secret store).
    /// </summary>
    [Fact]
    public void Production_configuration_has_no_seeding_password_or_connection_string()
    {
        var production = Load("appsettings.json");

        Assert.False(production.GetSection(SeedOptions.SectionName).Get<SeedOptions>()?.Enabled ?? new SeedOptions().Enabled);
        Assert.True(string.IsNullOrEmpty(production[$"{SeedOptions.SectionName}:{nameof(SeedOptions.DefaultPassword)}"]));
        Assert.Null(production.GetConnectionString("DefaultConnection"));
    }

    /// <summary>Development is where the demo data lives: seeding on, with a password, and a local default database.</summary>
    [Fact]
    public void Development_configuration_seeds_demo_data()
    {
        var development = Load("appsettings.json", "appsettings.Development.json");
        var seed = development.GetSection(SeedOptions.SectionName).Get<SeedOptions>()!;

        Assert.True(seed.Enabled);
        Assert.False(string.IsNullOrEmpty(seed.DefaultPassword));
        Assert.Contains("(localdb)", development.GetConnectionString("DefaultConnection"));
    }

    /// <summary>Loads the Web project's JSON settings files, later files overriding earlier ones.</summary>
    private static IConfiguration Load(params string[] files)
    {
        var builder = new ConfigurationBuilder().SetBasePath(WebProjectDirectory());
        foreach (var file in files)
        {
            builder.AddJsonFile(file, optional: false);
        }

        return builder.Build();
    }

    /// <summary>Finds RentalManagement.Web next to the solution file, walking up from the test output folder.</summary>
    private static string WebProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RentalManagement-1.slnx")))
            {
                return Path.Combine(dir.FullName, "RentalManagement.Web");
            }
        }

        throw new InvalidOperationException("RentalManagement-1.slnx not found above " + AppContext.BaseDirectory);
    }
}
