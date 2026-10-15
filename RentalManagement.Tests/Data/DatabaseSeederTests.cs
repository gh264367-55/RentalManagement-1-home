// =====================================================================================================================
// Module: RentalManagement.Tests / Data / DatabaseSeederTests.cs
// Purpose: Tests of the Bogus demo-data seeder against an EF Core in-memory database with real Identity managers:
//          what it seeds (roles, users, lookups, properties, two applications per status, leases), that a second run
//          adds nothing (idempotency), that seeded histories are legal, and that a missing password fails clearly.
// =====================================================================================================================

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core;
using RentalManagement.Core.Data;
using RentalManagement.Core.Data.Seeding;
using RentalManagement.Core.Domain;

namespace RentalManagement.Tests.Data;

/// <summary>Tests of <see cref="DatabaseSeeder"/>.</summary>
public class DatabaseSeederTests
{
    /// <summary>The fixed clock value, so lease dates relative to "today" are predictable.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One run seeds both roles with their users, the lookups, the properties and two applications per status.</summary>
    [Fact]
    public async Task Seeds_roles_users_lookups_properties_and_two_applications_per_status()
    {
        await using var services = BuildServices();
        await SeedAsync(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.Equal(DatabaseSeeder.ManagerCount, (await users.GetUsersInRoleAsync(Roles.PropertyManager)).Count);
        Assert.Equal(DatabaseSeeder.ApplicantCount, (await users.GetUsersInRoleAsync(Roles.Applicant)).Count);
        Assert.Equal(5, await db.UnitTypes.CountAsync());
        Assert.Equal(5, await db.Properties.CountAsync());

        var byStatus = await db.RentalApplications.GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Assert.All(Enum.GetValues<ApplicationStatus>(), status => Assert.Equal(2, byStatus.Single(s => s.Key == status).Count));
    }

    /// <summary>Idempotency: a second run on the same database leaves every table's row count unchanged.</summary>
    [Fact]
    public async Task Running_twice_adds_nothing_the_second_time()
    {
        await using var services = BuildServices();
        await SeedAsync(services);
        var first = await CountEverythingAsync(services);

        await SeedAsync(services);
        var second = await CountEverythingAsync(services);

        Assert.True(first.Applications > 0, "The first run should have seeded applications.");
        Assert.Equal(first, second);
    }

    /// <summary>Both approved applications have a twelve-month lease: one covering today, one already expired.</summary>
    [Fact]
    public async Task Approved_applications_get_one_active_and_one_expired_lease()
    {
        await using var services = BuildServices();
        await SeedAsync(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var leases = await db.Leases.Include(l => l.RentalApplication).ToListAsync();

        Assert.Equal(2, leases.Count);
        Assert.All(leases, l => Assert.Equal(ApplicationStatus.Approved, l.RentalApplication.Status));
        Assert.All(leases, l => Assert.Equal(Lease.EndDateFor(l.StartDate), l.EndDate));
        Assert.Single(leases, l => l.Covers(today));
        Assert.Single(leases, l => l.EndDate < today);
    }

    /// <summary>The seed demonstrates the unit-type rule: one inactive type, still used by exactly one unit.</summary>
    [Fact]
    public async Task Inactive_unit_type_is_used_by_exactly_one_unit()
    {
        await using var services = BuildServices();
        await SeedAsync(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var inactive = await db.UnitTypes.SingleAsync(t => !t.IsActive);
        Assert.Equal(DatabaseSeeder.InactiveUnitTypeName, inactive.Name);
        Assert.Equal(1, await db.Units.CountAsync(u => u.UnitTypeId == inactive.Id));
    }

    /// <summary>
    /// Every history starts with the draft's creation and ends in the current status, and every Return/Deny has a
    /// comment, as the real workflow would produce.
    /// </summary>
    [Fact]
    public async Task Every_seeded_application_has_a_history_that_ends_in_its_status()
    {
        await using var services = BuildServices();
        await SeedAsync(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var applications = await db.RentalApplications.Include(a => a.StatusHistory).ToListAsync();

        Assert.All(applications, a =>
        {
            var history = a.StatusHistory.OrderBy(h => h.ChangedAt).ToList();
            Assert.Null(history[0].FromStatus);
            Assert.Equal(a.Status, history[^1].ToStatus);
            Assert.All(history.Where(h => h.Outcome is ReviewOutcome.Return or ReviewOutcome.Deny),
                h => Assert.False(string.IsNullOrWhiteSpace(h.Comment)));
        });
    }

    /// <summary>
    /// Under Review applications are claimed by seeded property managers with a claim time, and their history ends
    /// with that manager's claim.
    /// </summary>
    [Fact]
    public async Task Under_review_applications_are_claimed_by_seeded_managers()
    {
        await using var services = BuildServices();
        await SeedAsync(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var managers = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .GetUsersInRoleAsync(Roles.PropertyManager)).Select(u => u.Id).ToHashSet();
        var underReview = await db.RentalApplications.Include(a => a.StatusHistory)
            .Where(a => a.Status == ApplicationStatus.UnderReview).ToListAsync();

        Assert.Equal(2, underReview.Select(a => a.ClaimedByUserId).Distinct().Count());
        Assert.All(underReview, a =>
        {
            Assert.Contains(a.ClaimedByUserId!, managers);
            Assert.True(a.ClaimedAt <= Now);
            var last = a.StatusHistory.OrderBy(h => h.ChangedAt).Last();
            Assert.Equal((ApplicationStatus.UnderReview, a.ClaimedByUserId, a.ClaimedAt), (last.ToStatus, last.ChangedById, (DateTimeOffset?)last.ChangedAt));
        });
    }

    /// <summary>
    /// A database seeded before Under Review existed (no claim ever recorded) gets the two Under Review applications on
    /// the next start, once; further starts add nothing.
    /// </summary>
    [Fact]
    public async Task Database_seeded_before_under_review_gets_them_once()
    {
        await using var services = BuildServices();
        await SeedAsync(services);
        await using (var scope = services.CreateAsyncScope())
        {
            // Make it look like the old seed: no Under Review applications and no claim in any history.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.RentalApplications.RemoveRange(await db.RentalApplications.Where(a => a.Status == ApplicationStatus.UnderReview).ToListAsync());
            db.ApplicationStatusHistory.RemoveRange(await db.ApplicationStatusHistory.Where(h => h.ToStatus == ApplicationStatus.UnderReview).ToListAsync());
            await db.SaveChangesAsync();
        }

        await SeedAsync(services);
        var afterUpgrade = await CountEverythingAsync(services);
        await SeedAsync(services);

        Assert.Equal(afterUpgrade, await CountEverythingAsync(services));
        await using var check = services.CreateAsyncScope();
        var underReview = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .RentalApplications.CountAsync(a => a.Status == ApplicationStatus.UnderReview);
        Assert.Equal(2, underReview);
    }

    /// <summary>Without a configured password the seeder stops with a message naming the missing setting.</summary>
    [Fact]
    public async Task Missing_default_password_fails_with_a_clear_message()
    {
        await using var services = BuildServices(password: "");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));
        Assert.Contains("Seed:DefaultPassword", error.Message);
    }

    /// <summary>
    /// A service provider like the app's, but with a fresh in-memory database, Identity managers, a fake clock and the
    /// given seed password.
    /// </summary>
    /// <param name="password">The seed password; empty to simulate a missing setting.</param>
    /// <returns>The provider; dispose it at the end of the test.</returns>
    private static ServiceProvider BuildServices(string password = "Test-Passw0rd!")
    {
        // One database name per provider (not per context), so every scope in a test sees the same data.
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.Configure<SeedOptions>(o => o.DefaultPassword = password);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now) { });
        services.AddRentalManagementCore();
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the seeder once in its own scope, as one application start would.</summary>
    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
    }

    /// <summary>Row counts of every seeded table, for comparing two runs.</summary>
    private static async Task<(int Roles, int Users, int UnitTypes, int Properties, int Units, int Applications, int History, int Leases)>
        CountEverythingAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (
            await db.Roles.CountAsync(),
            await db.Users.CountAsync(),
            await db.UnitTypes.CountAsync(),
            await db.Properties.CountAsync(),
            await db.Units.CountAsync(),
            await db.RentalApplications.CountAsync(),
            await db.ApplicationStatusHistory.CountAsync(),
            await db.Leases.CountAsync());
    }
}
