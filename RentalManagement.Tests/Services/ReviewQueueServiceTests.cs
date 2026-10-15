// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ReviewQueueServiceTests.cs
// Purpose: Tests of ReviewQueueService on an EF Core in-memory database: "Available" holds only unclaimed Submitted
//          applications, "My claims" only the signed-in manager's own claims, filters can only narrow a queue, paging
//          and the claimed-date sort, non-managers are refused, the queue keys, and the SQL Server translation (the
//          queue condition is in the WHERE clause, with paging as OFFSET/FETCH).
// =====================================================================================================================

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>
/// Tests of <see cref="ReviewQueueService"/>. Data: two managers (Pat, Sam), applicant Alice, two properties; at Maple
/// Court two Submitted applications and one claimed by each manager; at Oak Row one Submitted, one claimed by Pat,
/// and a Draft and a Returned application that belong to no queue.
/// </summary>
public sealed class ReviewQueueServiceTests : IDisposable
{
    private const string Pat = "pat";
    private const string Sam = "sam";
    private const string Alice = "alice";
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ApplicationDbContext _db;
    private readonly ReviewQueueService _service;
    private readonly int _mapleId;
    private readonly int _oakId;

    /// <summary>Creates the per-test database.</summary>
    public ReviewQueueServiceTests()
    {
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new ReviewQueueService(_db);

        var role = new IdentityRole(Roles.PropertyManager) { Id = "role-pm" };
        _db.Roles.Add(role);
        _db.Users.AddRange(
            new ApplicationUser { Id = Pat, UserName = "pat@test", FirstName = "Pat", LastName = "Manager" },
            new ApplicationUser { Id = Sam, UserName = "sam@test", FirstName = "Sam", LastName = "Second" },
            new ApplicationUser { Id = Alice, UserName = "alice@test", FirstName = "Alice", LastName = "Archer" });
        _db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = Pat, RoleId = role.Id },
            new IdentityUserRole<string> { UserId = Sam, RoleId = role.Id });
        var type = new UnitType { Name = "Apartment", IsActive = true };
        var maple = NewProperty("Maple Court", type);
        var oak = NewProperty("Oak Row", type);
        _db.Properties.AddRange(maple, oak);
        _db.SaveChanges();
        (_mapleId, _oakId) = (maple.Id, oak.Id);
        var (mapleUnit, oakUnit) = (maple.Units.Single().Id, oak.Units.Single().Id);

        var hour = 0;
        RentalApplication Make(int unitId, string? claimant = null, ApplicationStatus? status = null)
        {
            var at = Start.AddHours(hour++);
            var application = RentalApplication.StartDraft(unitId, Alice, at);
            if (status == ApplicationStatus.Draft)
            {
                return application;
            }

            application.TransitionTo(ApplicationStatus.Submitted, Alice, at);
            if (claimant is not null || status == ApplicationStatus.Returned)
            {
                application.Claim(claimant ?? Pat, at.AddMinutes(30));
            }

            if (status == ApplicationStatus.Returned)
            {
                application.TransitionTo(ApplicationStatus.Returned, Pat, at.AddMinutes(40), ReviewOutcome.Return, "Fix.");
            }

            return application;
        }

        _db.RentalApplications.AddRange(
            Make(mapleUnit), Make(mapleUnit), Make(mapleUnit, Pat), Make(mapleUnit, Sam),
            Make(oakUnit), Make(oakUnit, Pat), Make(oakUnit, status: ApplicationStatus.Draft), Make(oakUnit, status: ApplicationStatus.Returned));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>Available: every unclaimed Submitted application (any property), and nothing else.</summary>
    [Fact]
    public async Task Available_holds_only_unclaimed_submitted_applications()
    {
        var page = await PageAsync(Pat, ReviewQueue.Available);

        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal((ApplicationStatus.Submitted, (string?)null), (i.Status, i.ClaimedByName)));
    }

    /// <summary>
    /// The default Available order is first come, first served (oldest submission first); My claims shows the newest
    /// claim first.
    /// </summary>
    [Fact]
    public async Task Default_orders_are_oldest_submission_and_newest_claim_first()
    {
        var available = await PageAsync(Pat, ReviewQueue.Available);
        var mine = await PageAsync(Pat, ReviewQueue.MyClaims);

        Assert.Equal(available.Items.Select(i => i.SubmittedAt).Order(), available.Items.Select(i => i.SubmittedAt));
        Assert.Equal(mine.Items.Select(i => i.ClaimedAt).OrderDescending(), mine.Items.Select(i => i.ClaimedAt));
    }

    /// <summary>My claims: only the signed-in manager's own Under Review applications, with claim owner and time.</summary>
    [Fact]
    public async Task My_claims_holds_only_the_managers_own_claims()
    {
        var pats = await PageAsync(Pat, ReviewQueue.MyClaims);
        var sams = await PageAsync(Sam, ReviewQueue.MyClaims);

        Assert.Equal(2, pats.TotalCount);
        Assert.All(pats.Items, i => Assert.Equal((ApplicationStatus.UnderReview, "Pat Manager"), (i.Status, i.ClaimedByName)));
        Assert.All(pats.Items, i => Assert.NotNull(i.ClaimedAt));
        Assert.Equal("Sam Second", Assert.Single(sams.Items).ClaimedByName);
    }

    /// <summary>Filters only narrow a queue: a property filter, and a status filter can't pull in other statuses.</summary>
    [Fact]
    public async Task Filters_only_narrow_the_queue()
    {
        var oakOnly = await PageAsync(Pat, ReviewQueue.MyClaims, propertyId: _oakId);
        var returnedAsked = await PageAsync(Pat, ReviewQueue.Available, status: "Returned");
        var underReviewAsked = await PageAsync(Pat, ReviewQueue.Available, status: "Under Review");
        var mapleAvailable = await PageAsync(Sam, ReviewQueue.Available, propertyId: _mapleId);

        Assert.Equal("Oak Row", Assert.Single(oakOnly.Items).PropertyName);
        Assert.Equal(0, returnedAsked.TotalCount);
        Assert.Equal(0, underReviewAsked.TotalCount);
        Assert.Equal(2, mapleAvailable.TotalCount);
    }

    /// <summary>Paging works as in the application list: one-row pages, the real total, an empty page past the end.</summary>
    [Fact]
    public async Task Queue_is_paged()
    {
        var all = await PageAsync(Pat, ReviewQueue.Available, pageSize: 10);
        var second = (await _service.ListPageAsync(Pat, ReviewQueue.Available,
            ApplicationListQuery.Create(page: 2, pageSize: 10, sortBy: "id").Value!)).Value!;

        Assert.Equal(3, all.Items.Count);
        Assert.Empty(second.Items);
        Assert.Equal(3, second.TotalCount);
    }

    /// <summary>Applicants (and unknown users) are refused by the service itself, whatever the controller checked.</summary>
    [Fact]
    public async Task Non_managers_are_refused()
    {
        var result = await _service.ListPageAsync(Alice, ReviewQueue.Available, ApplicationListQuery.Default);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureKind.NotFound, result.Kind);
        Assert.Null(result.Value);
    }

    /// <summary>The queue keys are a whitelist, case-insensitive, and round-trip.</summary>
    [Theory]
    [InlineData("available", ReviewQueue.Available)]
    [InlineData("MYCLAIMS", ReviewQueue.MyClaims)]
    [InlineData(" myClaims ", ReviewQueue.MyClaims)]
    [InlineData("mine", null)]
    [InlineData("", null)]
    public void Queue_keys_are_whitelisted(string value, ReviewQueue? expected)
    {
        Assert.Equal(expected, ReviewQueueService.ParseQueue(value));
        if (expected is { } queue)
        {
            Assert.Equal(queue, ReviewQueueService.ParseQueue(ReviewQueueService.KeyOf(queue)));
        }
    }

    /// <summary>
    /// On SQL Server the queue condition (status, and for My claims the claim owner as a parameter) is in the WHERE
    /// clause, before the user's filters; nothing is filtered in memory.
    /// </summary>
    [Fact]
    public void Queue_conditions_are_translated_to_SQL()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True").Options);
        var service = new ReviewQueueService(db);

        var mine = service.Filter(Pat, ReviewQueue.MyClaims, new ApplicationListFilter(PropertyId: 7)).ToQueryString();
        var available = service.Filter(Pat, ReviewQueue.Available, new ApplicationListFilter()).ToQueryString();

        Assert.Matches(@"WHERE \[\w+\]\.\[Status\] = N'UnderReview' AND \[\w+\]\.\[ClaimedByUserId\] = @\w+ AND \[\w+\]\.\[PropertyId\] = @\w+", mine);
        Assert.Matches(@"WHERE \[\w+\]\.\[Status\] = N'Submitted' AND \[\w+\]\.\[ClaimedByUserId\] IS NULL", available);
    }

    private async Task<PagedResult<ApplicationListItem>> PageAsync(
        string managerId, ReviewQueue queue, int? propertyId = null, string? status = null, int? pageSize = null)
    {
        var defaults = ReviewQueueService.DefaultQuery(queue);
        var query = ApplicationListQuery.Create(
            pageSize: pageSize, sortBy: defaults.SortKey, sortDirection: defaults.SortDirectionKey, status: status, propertyId: propertyId).Value!;
        var result = await _service.ListPageAsync(managerId, queue, query);
        Assert.True(result.Succeeded, result.Error);
        return result.Value!;
    }

    private static Property NewProperty(string name, UnitType type)
    {
        var property = new Property
        {
            Name = name,
            Address = new Address { Street = "1 Main St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        property.Units.Add(new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1000m, UnitType = type });
        return property;
    }
}
