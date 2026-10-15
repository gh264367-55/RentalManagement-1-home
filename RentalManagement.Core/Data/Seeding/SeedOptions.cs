// =====================================================================================================================
// Module: RentalManagement.Core / Data / Seeding / SeedOptions.cs
// Purpose: Strongly typed settings for demo-data seeding, bound in Program.cs from the "Seed" configuration section
//          (Enabled, DefaultPassword).
// =====================================================================================================================

namespace RentalManagement.Core.Data.Seeding;

/// <summary>Settings for <see cref="DatabaseSeeder"/>, bound from the <c>Seed</c> configuration section.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>
    /// Seed demo data (users with a known password, sample properties and applications) on start. Off unless a
    /// configuration turns it on: appsettings.Development.json does, appsettings.json doesn't, so a deployed site
    /// never gets demo accounts or sample data and never fails to start for lack of a seed password.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Password given to every seeded user. Set it only in development configuration (appsettings.Development.json
    /// or user secrets), never in appsettings.json, so a deployed site cannot end up with known passwords.
    /// </summary>
    public string DefaultPassword { get; set; } = string.Empty;
}
