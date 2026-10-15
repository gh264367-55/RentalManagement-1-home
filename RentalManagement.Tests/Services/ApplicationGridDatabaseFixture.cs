// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationGridDatabaseFixture.cs
// Purpose: Real relational databases for the application-list paging/sorting/filtering tests. Always: a shared
//          in-memory SQLite database. Optionally: a throwaway SQL Server database, created with the real migrations
//          when the RENTALMANAGEMENT_TEST_SQLSERVER environment variable holds a connection string, and dropped
//          afterwards. Both get the same 30 applications, and the fixture keeps a plain copy of them as the oracle the
//          tests compare the database's answers against.
// =====================================================================================================================

using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;

namespace RentalManagement.Tests.Services;

/// <summary>An application as seeded, with the values the list shows and sorts by.</summary>
public sealed record GridRow(
    int Id,
    string ApplicantId,
    string FirstName,
    string LastName,
    int PropertyId,
    string PropertyName,
    string UnitNumber,
    ApplicationStatus Status,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClaimedAt);

/// <summary>
/// The seeded databases. Data: applicants Alice, Bob and Cara; properties Birch Hall (units 101, 102), Maple Court
/// (101, 201) and Oak Row (301); 30 applications spread over every status (whatever the enum contains), with
/// submission dates that tie in pairs (to exercise tie-breaking) and never-submitted drafts.
/// </summary>
public sealed class ApplicationGridDatabaseFixture : IAsyncLifetime
{
    /// <summary>Provider name: shared in-memory SQLite (always available).</summary>
    public const string Sqlite = "Sqlite";

    /// <summary>Provider name: a real SQL Server database (only when <see cref="SqlServerVariable"/> is set).</summary>
    public const string SqlServer = "SqlServer";

    /// <summary>
    /// Environment variable with a SQL Server connection string (e.g.
    /// <c>Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True</c>). The database name in it is
    /// replaced by a unique throwaway name.
    /// </summary>
    public const string SqlServerVariable = "RENTALMANAGEMENT_TEST_SQLSERVER";

    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string Cara = "cara";
    public const string Manager = "manager";

    private static readonly DateTimeOffset Start = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly string _sqliteConnectionString = $"Data Source=grid-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _sqliteKeepAlive;
    private string? _sqlServerConnectionString;

    /// <summary>The providers to run the tests on: SQLite, plus SQL Server when configured.</summary>
    public static IReadOnlyList<string> Providers =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerVariable)) ? [Sqlite] : [Sqlite, SqlServer];

    /// <summary>The seeded applications per provider (ids are assigned by each database).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GridRow>> Rows { get; private set; } = new Dictionary<string, IReadOnlyList<GridRow>>();

    /// <summary>A new context on the given provider's database.</summary>
    /// <param name="provider"><see cref="Sqlite"/> or <see cref="SqlServer"/>.</param>
    /// <returns>The context; dispose it after use.</returns>
    public ApplicationDbContext CreateContext(string provider) => provider switch
    {
        Sqlite => new SqliteTestDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_sqliteConnectionString).Options),
        SqlServer => new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(_sqlServerConnectionString ?? throw new InvalidOperationException($"{SqlServerVariable} is not set."))
            .Options),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>Creates and seeds the databases.</summary>
    public async Task InitializeAsync()
    {
        var rows = new Dictionary<string, IReadOnlyList<GridRow>>();

        _sqliteKeepAlive = new SqliteConnection(_sqliteConnectionString);
        await _sqliteKeepAlive.OpenAsync();
        await using (var db = CreateContext(Sqlite))
        {
            await db.Database.EnsureCreatedAsync();
            rows[Sqlite] = await SeedAsync(db);
        }

        var server = Environment.GetEnvironmentVariable(SqlServerVariable);
        if (!string.IsNullOrWhiteSpace(server))
        {
            _sqlServerConnectionString = new SqlConnectionStringBuilder(server)
            {
                InitialCatalog = $"RentalManagement_GridTests_{Guid.NewGuid():N}",
            }.ConnectionString;
            await using var db = CreateContext(SqlServer);
            await db.Database.MigrateAsync();   // the real migrations, as on application start
            rows[SqlServer] = await SeedAsync(db);
        }

        Rows = rows;
    }

    /// <summary>Drops the SQL Server database (if one was created) and closes SQLite.</summary>
    public async Task DisposeAsync()
    {
        if (_sqlServerConnectionString is not null)
        {
            await using var db = CreateContext(SqlServer);
            await db.Database.EnsureDeletedAsync();
        }

        if (_sqliteKeepAlive is not null)
        {
            await _sqliteKeepAlive.DisposeAsync();
        }
    }

    /// <summary>Adds the users, properties and 30 applications, and returns them as plain rows.</summary>
    private static async Task<IReadOnlyList<GridRow>> SeedAsync(ApplicationDbContext db)
    {
        var users = new[]
        {
            new ApplicationUser { Id = Alice, UserName = "alice@test", Email = "alice@test", FirstName = "Alice", LastName = "Archer" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", Email = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Cara, UserName = "cara@test", Email = "cara@test", FirstName = "Cara", LastName = "Cole" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", Email = "pm@test", FirstName = "Pat", LastName = "Manager" },
        };
        db.Users.AddRange(users);

        var type = new UnitType { Name = "Apartment", IsActive = true };
        var properties = new[]
        {
            NewProperty("Birch Hall", type, "101", "102"),
            NewProperty("Maple Court", type, "101", "201"),
            NewProperty("Oak Row", type, "301"),
        };
        db.Properties.AddRange(properties);
        await db.SaveChangesAsync();

        var units = properties.SelectMany(p => p.Units).OrderBy(u => u.Id).ToList();
        var statuses = Enum.GetValues<ApplicationStatus>();
        var applicants = new[] { Alice, Bob, Cara };
        var applications = new List<RentalApplication>();
        for (var i = 0; i < 30; i++)
        {
            var status = statuses[i % statuses.Length];
            var application = RentalApplication.StartDraft(units[(i * 3) % units.Count].Id, applicants[i % applicants.Length], Start);
            application.UpdatedAt = Start.AddDays((i * 11) % 30).AddMinutes(i);
            // (i * 7) % 20 repeats for i and i + 20, so some submissions share a timestamp and need the tie-breakers.
            application.SubmittedAt = status == ApplicationStatus.Draft ? null : Start.AddDays((i * 7) % 20);
            db.RentalApplications.Add(application);
            // Any status, legal path or not: these tests are about listing, and this way a status added to the enum
            // later is seeded and tested without changes here.
            db.Entry(application).Property(a => a.Status).CurrentValue = status;
            if (status == ApplicationStatus.UnderReview)
            {
                // The database requires a claim with Under Review (CK_RentalApplications_Claim). Distinct times, so the
                // claimed-date sort is checked on real values.
                db.Entry(application).Property(a => a.ClaimedByUserId).CurrentValue = Manager;
                db.Entry(application).Property(a => a.ClaimedAt).CurrentValue = Start.AddDays(20).AddHours(i);
            }
            applications.Add(application);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var userById = users.ToDictionary(u => u.Id);
        var unitById = properties.SelectMany(p => p.Units.Select(u => (Unit: u, Property: p))).ToDictionary(x => x.Unit.Id);
        return applications
            .Select(a => new GridRow(
                a.Id,
                a.ApplicantId,
                userById[a.ApplicantId].FirstName,
                userById[a.ApplicantId].LastName,
                unitById[a.UnitId].Property.Id,
                unitById[a.UnitId].Property.Name,
                unitById[a.UnitId].Unit.UnitNumber,
                a.Status,
                a.SubmittedAt,
                a.UpdatedAt,
                a.ClaimedAt))
            .ToList();
    }

    private static Property NewProperty(string name, UnitType type, params string[] unitNumbers)
    {
        var property = new Property
        {
            Name = name,
            Address = new Address { Street = "1 Main St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        foreach (var number in unitNumbers)
        {
            property.Units.Add(new Unit { UnitNumber = number, Bedrooms = 1, MonthlyRent = 1000m, UnitType = type });
        }

        return property;
    }

    /// <summary>
    /// The application's context for SQLite. SQLite has no DateTimeOffset type, so EF can't ORDER BY one there; this
    /// stores them as sortable 64-bit numbers (EF's documented workaround). Everything else is the production model.
    /// </summary>
    private sealed class SqliteTestDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }
}
