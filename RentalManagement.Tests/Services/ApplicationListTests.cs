// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationListTests.cs
// Purpose: Tests of the application list filters (ApplicationService.ListPageAsync / QueryPage): applicants only ever
//          see their own applications whatever the filter, managers see all, status and property filters narrow
//          correctly, the property dropdown is scoped per role, and the filters are translated into the SQL WHERE clause
//          (checked on the SQL Server provider's generated SQL) rather than applied in memory.
// =====================================================================================================================

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>
/// Tests of the application list. Data: two properties (Maple, Oak). Alice has a Draft at Maple and a Submitted
/// application at Oak; Bob has a Submitted application at Maple and an Approved one at Oak.
/// </summary>
public sealed class ApplicationListTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Manager = "manager";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly ApplicationService _service;
    private readonly int _mapleId;
    private readonly int _oakId;

    /// <summary>Creates the in-memory database with the two properties and four applications.</summary>
    public ApplicationListTests()
    {
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new ApplicationService(_db, _clock);

        _db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@test", FirstName = "Alice", LastName = "Archer" },
            new ApplicationUser { Id = Bob, UserName = "bob@test", FirstName = "Bob", LastName = "Baker" },
            new ApplicationUser { Id = Manager, UserName = "pm@test", FirstName = "Pat", LastName = "Manager" });
        var type = new UnitType { Name = "Apartment", IsActive = true };
        var maple = NewProperty("Maple Court", type);
        var oak = NewProperty("Oak Row", type);
        _db.Properties.AddRange(maple, oak);
        _db.SaveChanges();
        (_mapleId, _oakId) = (maple.Id, oak.Id);

        var mapleUnit = maple.Units.Single().Id;
        var oakUnit = oak.Units.Single().Id;
        _db.RentalApplications.AddRange(
            InStatus(mapleUnit, Alice, ApplicationStatus.Draft),
            InStatus(oakUnit, Alice, ApplicationStatus.Submitted),
            InStatus(mapleUnit, Bob, ApplicationStatus.Submitted),
            InStatus(oakUnit, Bob, ApplicationStatus.Approved));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>Without filters an applicant sees exactly their own applications and a manager sees all four.</summary>
    [Fact]
    public async Task Applicants_see_their_own_applications_and_managers_see_all()
    {
        var alices = await ListAsync(Alice, isManager: false);
        var all = await ListAsync(Manager, isManager: true);

        Assert.Equal(2, alices.Count);
        Assert.All(alices, a => Assert.Equal("Alice Archer", a.ApplicantName));
        Assert.Equal(4, all.Count);
    }

    /// <summary>The status filter narrows to that status, for managers and applicants alike.</summary>
    [Fact]
    public async Task Status_filter_narrows_the_list()
    {
        var submitted = await ListAsync(Manager, isManager: true, new ApplicationListFilter(Status: ApplicationStatus.Submitted));
        var alicesSubmitted = await ListAsync(Alice, isManager: false, new ApplicationListFilter(Status: ApplicationStatus.Submitted));

        Assert.Equal(["Alice Archer", "Bob Baker"], submitted.Select(a => a.ApplicantName).Order());
        Assert.Equal("Oak Row", Assert.Single(alicesSubmitted).PropertyName);
    }

    /// <summary>The property filter narrows to that property, and combines with the status filter.</summary>
    [Fact]
    public async Task Property_filter_narrows_and_combines_with_status()
    {
        var maple = await ListAsync(Manager, isManager: true, new ApplicationListFilter(PropertyId: _mapleId));
        var oakApproved = await ListAsync(Manager, isManager: true, new ApplicationListFilter(ApplicationStatus.Approved, _oakId));

        Assert.Equal(2, maple.Count);
        Assert.All(maple, a => Assert.Equal("Maple Court", a.PropertyName));
        Assert.Equal("Bob Baker", Assert.Single(oakApproved).ApplicantName);
    }

    /// <summary>
    /// A filter can never widen an applicant's view: filtering on a status or property where only someone else has
    /// applications returns nothing, not the other applicant's rows.
    /// </summary>
    [Fact]
    public async Task Filters_never_reveal_another_applicants_applications()
    {
        var approved = await ListAsync(Alice, isManager: false, new ApplicationListFilter(Status: ApplicationStatus.Approved));
        var oakSubmittedForBob = await ListAsync(Bob, isManager: false, new ApplicationListFilter(ApplicationStatus.Submitted, _oakId));

        Assert.Empty(approved);
        Assert.Empty(oakSubmittedForBob);
    }

    /// <summary>The property dropdown lists every property for managers and only the applicant's own for applicants.</summary>
    [Fact]
    public async Task Property_options_are_scoped_per_role()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ApplicationService(db, _clock);
        var type = new UnitType { Name = "Apartment", IsActive = true };
        var applied = NewProperty("Applied Place", type);
        var other = NewProperty("Other Place", type);
        db.Users.Add(new ApplicationUser { Id = Alice, UserName = "alice@test" });
        db.Properties.AddRange(applied, other);
        await db.SaveChangesAsync();
        db.RentalApplications.Add(InStatus(applied.Units.Single().Id, Alice, ApplicationStatus.Draft));
        await db.SaveChangesAsync();

        var forAlice = await service.GetListPropertyOptionsAsync(Alice, isManager: false);
        var forManager = await service.GetListPropertyOptionsAsync(Manager, isManager: true);

        Assert.Equal(["Applied Place"], forAlice.Select(p => p.Name));
        Assert.Equal(["Applied Place", "Other Place"], forManager.Select(p => p.Name));
    }

    /// <summary>
    /// The filters run in SQL: on the SQL Server provider the generated query has the applicant, status and property
    /// conditions as parameters in its WHERE clause, with ordering, paging and projection in the same statement.
    /// </summary>
    [Fact]
    public void Applicant_query_filters_in_the_SQL_WHERE_clause()
    {
        using var sql = SqlServerContext();
        var service = new ApplicationService(sql, _clock);

        var text = service.QueryPage(Alice, isManager: false, Query(new ApplicationListFilter(ApplicationStatus.Submitted, 7))).ToQueryString();

        var where = WhereClause(text);
        Assert.Matches(@"\[ApplicantId\] = @\w+", where);
        Assert.Matches(@"\[Status\] = @\w+", where);
        Assert.Matches(@"\[PropertyId\] = @\w+", where);
        Assert.Contains("ORDER BY", text);
    }

    /// <summary>A manager's query has no applicant condition, and an unfiltered one has no WHERE clause at all.</summary>
    [Fact]
    public void Manager_query_has_no_applicant_condition()
    {
        using var sql = SqlServerContext();
        var service = new ApplicationService(sql, _clock);

        var filtered = service.QueryPage(Manager, isManager: true, Query(new ApplicationListFilter(ApplicationStatus.Denied))).ToQueryString();
        var unfiltered = service.QueryPage(Manager, isManager: true, Query(new ApplicationListFilter())).ToQueryString();

        Assert.DoesNotContain("[ApplicantId] = @", WhereClause(filtered));
        Assert.Matches(@"\[Status\] = @\w+", WhereClause(filtered));
        Assert.DoesNotMatch(@"(?m)^\s*WHERE ", unfiltered);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Every matching row (one page of 100) as the given user, through ListPageAsync.</summary>
    private async Task<IReadOnlyList<ApplicationListItem>> ListAsync(string userId, bool isManager, ApplicationListFilter? filter = null) =>
        (await _service.ListPageAsync(userId, isManager, Query(filter ?? new ApplicationListFilter()))).Items;

    /// <summary>A list query for a filter, with the largest page size.</summary>
    private static ApplicationListQuery Query(ApplicationListFilter filter) =>
        ApplicationListQuery.Create(pageSize: 100, status: filter.Status?.ToString(), propertyId: filter.PropertyId).Value!;

    /// <summary>A context on the SQL Server provider that is only used to generate SQL; it never opens a connection.</summary>
    private static ApplicationDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
            .Options);

    /// <summary>
    /// The WHERE clause of a generated query (from "WHERE" up to the next "ORDER BY"). With paging, EF puts the filter,
    /// order and OFFSET/FETCH in a subquery and joins the display columns afterwards, so the clause may be indented.
    /// </summary>
    private static string WhereClause(string sql)
    {
        var match = Regex.Match(sql, @"(?ms)^\s*WHERE (.*?)^\s*ORDER BY");
        Assert.True(match.Success, $"No WHERE clause in:\n{sql}");
        return match.Groups[1].Value;
    }

    /// <summary>A property with one unit of the given type.</summary>
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

    /// <summary>An application for the unit, moved through legal transitions to <paramref name="status"/>.</summary>
    private RentalApplication InStatus(int unitId, string applicantId, ApplicationStatus status)
    {
        var now = _clock.GetUtcNow();
        var application = RentalApplication.StartDraft(unitId, applicantId, now);
        if (status is ApplicationStatus.Submitted or ApplicationStatus.Approved)
        {
            application.TransitionTo(ApplicationStatus.Submitted, applicantId, now);
        }

        if (status == ApplicationStatus.Approved)
        {
            application.Claim(Manager, now);
            application.TransitionTo(ApplicationStatus.Approved, Manager, now, ReviewOutcome.Approve);
        }

        return application;
    }
}
