// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ReviewQueueSqlServerTests.cs
// Purpose: Integration tests of the review queue on a real SQL Server, in throwaway databases built with the real
//          migrations (skipped unless RENTALMANAGEMENT_TEST_SQLSERVER holds a connection string): many managers claiming
//          one application at the same instant (exactly one wins), the claim check constraint, the queue queries, the
//          AddReviewClaim migration upgrading a database that holds pre-Under-Review data, and the seeder being
//          idempotent on SQL Server.
// =====================================================================================================================

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core;
using RentalManagement.Core.Data;
using RentalManagement.Core.Data.Seeding;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>A fact that runs only when <see cref="ApplicationGridDatabaseFixture.SqlServerVariable"/> is set.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    /// <summary>Skips the test, with the reason, when no SQL Server connection string is configured.</summary>
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApplicationGridDatabaseFixture.SqlServerVariable)))
        {
            Skip = $"Set {ApplicationGridDatabaseFixture.SqlServerVariable} to a SQL Server connection string to run this test.";
        }
    }
}

/// <summary>
/// Review queue tests on SQL Server. Each test creates its own database (unique name), migrates it with the real
/// migrations, and drops it afterwards.
/// </summary>
public sealed class ReviewQueueSqlServerTests : IAsyncLifetime
{
    private const int ManagerCount = 6;
    private const string Applicant = "applicant";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<string> _databases = [];

    /// <summary>Nothing to prepare: each test creates its database.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Drops every database the test created.</summary>
    public async Task DisposeAsync()
    {
        foreach (var connectionString in _databases)
        {
            await using var db = Context(connectionString);
            await db.Database.EnsureDeletedAsync();
        }
    }

    /// <summary>
    /// Six managers claim the same Submitted application at the same instant: every one of them has loaded it (as
    /// Submitted, unclaimed) before any of them saves. SQL Server lets exactly one conditional UPDATE match; the others
    /// get a Conflict, and none of their status, owner or history row is stored.
    /// </summary>
    [SqlServerFact]
    public async Task Simultaneous_claims_let_exactly_one_manager_win()
    {
        var connectionString = await NewDatabaseAsync();
        var (managers, applicationId) = await SeedQueueDataAsync(connectionString);

        // Every claim waits here, after its read and just before its UPDATE, until all of them have arrived.
        using var allLoaded = new CountdownEvent(ManagerCount);
        var results = await Task.WhenAll(managers.Select(manager => Task.Run(async () =>
        {
            await using var db = Context(connectionString, new WaitForAll(allLoaded));
            return (Manager: manager, Result: await new ApplicationReviewService(db, _clock).ClaimAsync(applicationId, manager));
        })));

        var winners = results.Where(r => r.Result.Succeeded).ToList();
        var winner = Assert.Single(winners).Manager;
        Assert.All(results.Where(r => !r.Result.Succeeded), r => Assert.Equal(FailureKind.Conflict, r.Result.Kind));

        await using var check = Context(connectionString);
        var application = await check.RentalApplications.Include(a => a.StatusHistory).AsNoTracking().SingleAsync(a => a.Id == applicationId);
        Assert.Equal((ApplicationStatus.UnderReview, winner), (application.Status, application.ClaimedByUserId));
        Assert.NotNull(application.ClaimedAt);
        Assert.Single(application.StatusHistory, h => h.ToStatus == ApplicationStatus.UnderReview);
    }

    /// <summary>
    /// The full queue cycle on SQL Server: claim, a second manager refused (claim, release and review), release, a
    /// claim by the second manager, and approval with a twelve-month lease; the history has every step.
    /// </summary>
    [SqlServerFact]
    public async Task Claim_release_and_review_cycle_is_recorded()
    {
        var connectionString = await NewDatabaseAsync();
        var (managers, id) = await SeedQueueDataAsync(connectionString);
        var (first, second) = (managers[0], managers[1]);

        Assert.True((await Service(connectionString).ClaimAsync(id, first)).Succeeded);
        Assert.Equal(FailureKind.Conflict, (await Service(connectionString).ClaimAsync(id, second)).Kind);
        Assert.Equal(FailureKind.Conflict, (await Service(connectionString).ReleaseAsync(id, second)).Kind);
        Assert.Equal(FailureKind.Conflict,
            (await Service(connectionString).ReviewAsync(id, second, new ReviewInput(ReviewOutcome.Deny, "No.", null))).Kind);
        Assert.True((await Service(connectionString).ReleaseAsync(id, first)).Succeeded);
        Assert.True((await Service(connectionString).ClaimAsync(id, second)).Succeeded);
        var start = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.True((await Service(connectionString).ReviewAsync(id, second, new ReviewInput(ReviewOutcome.Approve, null, start))).Succeeded);

        await using var db = Context(connectionString);
        var application = await db.RentalApplications.Include(a => a.StatusHistory).Include(a => a.Lease).AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.Null(application.ClaimedByUserId);
        Assert.Equal(Lease.EndDateFor(start), application.Lease!.EndDate);
        Assert.Equal(
            [ApplicationStatus.Draft, ApplicationStatus.Submitted, ApplicationStatus.UnderReview, ApplicationStatus.Submitted,
             ApplicationStatus.UnderReview, ApplicationStatus.Approved],
            application.StatusHistory.OrderBy(h => h.Id).Select(h => h.ToStatus));
    }

    /// <summary>
    /// The database itself refuses a claim without Under Review, and Under Review without a claim
    /// (CK_RentalApplications_Claim), whatever code writes the row.
    /// </summary>
    [SqlServerFact]
    public async Task Check_constraint_ties_the_claim_to_under_review()
    {
        var connectionString = await NewDatabaseAsync();
        var (managers, id) = await SeedQueueDataAsync(connectionString);
        await using var db = Context(connectionString);

        var underReviewWithoutClaim = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE RentalApplications SET Status = 'UnderReview' WHERE Id = {id}"));
        var claimWithoutUnderReview = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE RentalApplications SET ClaimedByUserId = {managers[0]}, ClaimedAt = SYSDATETIMEOFFSET() WHERE Id = {id}"));

        Assert.Contains("CK_RentalApplications_Claim", underReviewWithoutClaim.Message);
        Assert.Contains("CK_RentalApplications_Claim", claimWithoutUnderReview.Message);
    }

    /// <summary>
    /// The queue on SQL Server: Available holds the unclaimed Submitted application, My claims holds only the manager's
    /// own claims (another manager's queue stays empty), and an applicant is refused.
    /// </summary>
    [SqlServerFact]
    public async Task Queues_are_filtered_per_manager_in_the_database()
    {
        var connectionString = await NewDatabaseAsync();
        var (managers, id) = await SeedQueueDataAsync(connectionString);
        var query = ReviewQueueService.DefaultQuery(ReviewQueue.MyClaims);

        Assert.Equal([id], (await Queue(connectionString, managers[0], ReviewQueue.Available)).Items.Select(i => i.Id));
        Assert.True((await Service(connectionString).ClaimAsync(id, managers[0])).Succeeded);

        Assert.Empty((await Queue(connectionString, managers[0], ReviewQueue.Available)).Items);
        var mine = Assert.Single((await Queue(connectionString, managers[0], ReviewQueue.MyClaims)).Items);
        Assert.Equal((id, ApplicationStatus.UnderReview), (mine.Id, mine.Status));
        Assert.NotNull(mine.ClaimedAt);
        Assert.Empty((await Queue(connectionString, managers[1], ReviewQueue.MyClaims)).Items);

        await using var db = Context(connectionString);
        var refused = await new ReviewQueueService(db).ListPageAsync(Applicant, ReviewQueue.Available, query);
        Assert.Equal(FailureKind.NotFound, refused.Kind);
    }

    /// <summary>
    /// Upgrading a database that already holds data from before the review queue: migrate to InitialCreate, insert
    /// Submitted and Approved applications the old way, then apply AddReviewClaim. The upgrade succeeds, the old rows
    /// keep their statuses and get no claim, and the upgraded data can be claimed. Rolling back to InitialCreate puts
    /// an Under Review application back to Submitted.
    /// </summary>
    [SqlServerFact]
    public async Task Review_claim_migration_upgrades_existing_data_and_rolls_back()
    {
        var connectionString = await NewDatabaseAsync(migrate: false);
        await using (var db = Context(connectionString))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261008175457_InitialCreate");
            await db.Database.ExecuteSqlRawAsync(LegacyDataSql);
            Assert.Contains("20261010180209_AddReviewClaim", await db.Database.GetPendingMigrationsAsync());

            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        await using (var db = Context(connectionString))
        {
            var rows = await db.RentalApplications.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
            Assert.Equal([ApplicationStatus.Submitted, ApplicationStatus.Approved], rows.Select(r => r.Status));
            Assert.All(rows, r => Assert.Null(r.ClaimedByUserId));

            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "legacy-pm", RoleId = "role-pm" });
            await db.SaveChangesAsync();
            Assert.True((await Service(connectionString).ClaimAsync(rows[0].Id, "legacy-pm")).Succeeded);
        }

        await using (var db = Context(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261008175457_InitialCreate");
            var status = await db.Database
                .SqlQueryRaw<string>("SELECT TOP 1 Status AS Value FROM RentalApplications ORDER BY Id")
                .SingleAsync();
            Assert.Equal("Submitted", status);
        }
    }

    /// <summary>
    /// The seeder on a migrated SQL Server database: two applications in every status (Under Review included, each
    /// claimed by a seeded manager), and a second start adds nothing.
    /// </summary>
    [SqlServerFact]
    public async Task Seeder_is_idempotent_on_SQL_Server()
    {
        var connectionString = await NewDatabaseAsync();
        await using var services = SeederServices(connectionString);

        await SeedOnceAsync(services);
        var first = await CountAsync(connectionString);
        await SeedOnceAsync(services);
        var second = await CountAsync(connectionString);

        Assert.Equal(first, second);
        await using var db = Context(connectionString);
        var byStatus = await db.RentalApplications.GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Assert.All(Enum.GetValues<ApplicationStatus>(), s => Assert.Equal(2, byStatus.Single(x => x.Key == s).Count));
        var underReview = await db.RentalApplications.Where(a => a.Status == ApplicationStatus.UnderReview)
            .Select(a => new { a.ClaimedAt, Manager = a.ClaimedBy!.Email }).ToListAsync();
        Assert.All(underReview, a => Assert.NotNull(a.ClaimedAt));
        Assert.Equal(["manager1@rental.test", "manager2@rental.test"], underReview.Select(a => a.Manager!).Order());
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Old-model data (no claim columns): users, a property with two units, a Submitted and an Approved application.</summary>
    private const string LegacyDataSql = """
        INSERT INTO AspNetRoles (Id, Name, NormalizedName) VALUES ('role-pm', 'PropertyManager', 'PROPERTYMANAGER');
        INSERT INTO AspNetUsers (Id, UserName, FirstName, LastName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
        VALUES ('legacy-app', 'old@test', 'Old', 'Applicant', 1, 0, 0, 0, 0),
               ('legacy-pm', 'pm@test', 'Old', 'Manager', 1, 0, 0, 0, 0);
        INSERT INTO UnitTypes (Name, IsActive, SortOrder) VALUES ('Apartment', 1, 1);
        INSERT INTO Properties (Name, Street, City, State, PostalCode, ConcurrencyStamp)
        VALUES ('Legacy Court', '1 Old St', 'Troy', 'NY', '12180', NEWID());
        INSERT INTO Units (PropertyId, UnitNumber, Bedrooms, MonthlyRent, UnitTypeId, ConcurrencyStamp)
        SELECT p.Id, n.Number, 1, 1200, t.Id, NEWID() FROM Properties p CROSS JOIN UnitTypes t CROSS JOIN (VALUES ('101'), ('102')) n(Number);
        INSERT INTO RentalApplications (UnitId, ApplicantId, Status, ApplicantInfoCompleted, ResidenceHistoryCompleted, CreatedAt, UpdatedAt, SubmittedAt, ConcurrencyStamp)
        SELECT Id, 'legacy-app', CASE UnitNumber WHEN '101' THEN 'Submitted' ELSE 'Approved' END, 1, 1,
               SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), NEWID()
        FROM Units;
        INSERT INTO ApplicationStatusHistory (RentalApplicationId, FromStatus, ToStatus, ChangedById, ChangedAt)
        SELECT Id, 'Draft', Status, 'legacy-app', SYSDATETIMEOFFSET() FROM RentalApplications;
        """;

    /// <summary>Creates a uniquely named database (migrated unless <paramref name="migrate"/> is false); dropped at the end.</summary>
    private async Task<string> NewDatabaseAsync(bool migrate = true)
    {
        var connectionString = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable(ApplicationGridDatabaseFixture.SqlServerVariable))
        {
            InitialCatalog = $"RentalManagement_QueueTests_{Guid.NewGuid():N}",
        }.ConnectionString;
        _databases.Add(connectionString);
        if (migrate)
        {
            await using var db = Context(connectionString);
            await db.Database.MigrateAsync();
        }

        return connectionString;
    }

    /// <summary>The roles, <see cref="ManagerCount"/> property managers, an applicant, one unit, and one Submitted application.</summary>
    private async Task<(IReadOnlyList<string> Managers, int ApplicationId)> SeedQueueDataAsync(string connectionString)
    {
        await using var db = Context(connectionString);
        var role = new IdentityRole(Roles.PropertyManager) { Id = "role-pm", NormalizedName = "PROPERTYMANAGER" };
        db.Roles.Add(role);
        var managers = Enumerable.Range(1, ManagerCount).Select(i => $"manager-{i}").ToList();
        foreach (var manager in managers)
        {
            db.Users.Add(new ApplicationUser { Id = manager, UserName = $"{manager}@test", Email = $"{manager}@test", FirstName = "Manager", LastName = manager });
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = manager, RoleId = role.Id });
        }

        db.Users.Add(new ApplicationUser { Id = Applicant, UserName = "applicant@test", FirstName = "Alice", LastName = "Archer" });
        var property = new Property
        {
            Name = "Maple Court",
            Address = new Address { Street = "1 Maple St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        property.Units.Add(new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1250m, UnitType = new UnitType { Name = "Apartment", IsActive = true } });
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var application = RentalApplication.StartDraft(property.Units.Single().Id, Applicant, Now.AddDays(-2));
        application.TransitionTo(ApplicationStatus.Submitted, Applicant, Now.AddDays(-1));
        db.RentalApplications.Add(application);
        await db.SaveChangesAsync();
        return (managers, application.Id);
    }

    /// <summary>One page of a queue, read through the service with a fresh context.</summary>
    private static async Task<PagedResult<ApplicationListItem>> Queue(string connectionString, string managerId, ReviewQueue queue)
    {
        await using var db = Context(connectionString);
        var result = await new ReviewQueueService(db).ListPageAsync(managerId, queue, ReviewQueueService.DefaultQuery(queue));
        Assert.True(result.Succeeded, result.Error);
        return result.Value!;
    }

    /// <summary>A review service on a new context (each call is its own "request").</summary>
    private ApplicationReviewService Service(string connectionString) => new(Context(connectionString), _clock);

    private static ApplicationDbContext Context(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).AddInterceptors(interceptors).Options);

    /// <summary>Services like the app's (Identity + Core) on the given database, for running the seeder.</summary>
    private ServiceProvider SeederServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(connectionString));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.Configure<SeedOptions>(o => o.DefaultPassword = "Test-Passw0rd!");
        services.AddSingleton<TimeProvider>(_clock);
        services.AddRentalManagementCore();
        return services.BuildServiceProvider();
    }

    private static async Task SeedOnceAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
    }

    private static async Task<(int Users, int Applications, int History, int Leases)> CountAsync(string connectionString)
    {
        await using var db = Context(connectionString);
        return (await db.Users.CountAsync(), await db.RentalApplications.CountAsync(),
            await db.ApplicationStatusHistory.CountAsync(), await db.Leases.CountAsync());
    }

    /// <summary>Holds each save until every participant has reached its save, so all reads happen before any write.</summary>
    private sealed class WaitForAll(CountdownEvent arrivals) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            arrivals.Signal();
            await Task.Run(() => Assert.True(arrivals.Wait(TimeSpan.FromSeconds(30)), "Not every claim reached its save."), cancellationToken);
            return result;
        }
    }
}
