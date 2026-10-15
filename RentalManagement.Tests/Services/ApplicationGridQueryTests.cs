// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationGridQueryTests.cs
// Purpose: Database-backed tests of ApplicationService.ListPageAsync, the query behind the application grid and
//          GET /api/applications/grid: default paging, page sizes, pages past the end, every sort column in both
//          directions, default-sort stability across pages, status (every enum value, including statuses added later
//          such as Under Review) and property filters, combined filter + sort, filtered totals, empty results, and the
//          applicant/manager restriction. Each test runs on real relational databases (SQLite always, SQL Server when
//          RENTALMANAGEMENT_TEST_SQLSERVER is set), so EF has to translate paging, sorting and filtering to SQL; the
//          answers are compared with LINQ-to-objects over the seeded rows. A last test checks the SQL Server text.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;
using static RentalManagement.Tests.Services.ApplicationGridDatabaseFixture;

namespace RentalManagement.Tests.Services;

/// <summary>Paging, sorting and filtering of the application list, against real databases.</summary>
public sealed class ApplicationGridQueryTests(ApplicationGridDatabaseFixture fixture) : IClassFixture<ApplicationGridDatabaseFixture>
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The providers the tests run on.</summary>
    public static TheoryData<string> ProviderData => [.. Providers];

    /// <summary>Every provider × every sort column × both directions.</summary>
    public static TheoryData<string, ApplicationSortColumn, SortDirection> SortData()
    {
        var data = new TheoryData<string, ApplicationSortColumn, SortDirection>();
        foreach (var provider in Providers)
        {
            foreach (var column in Enum.GetValues<ApplicationSortColumn>())
            {
                data.Add(provider, column, SortDirection.Ascending);
                data.Add(provider, column, SortDirection.Descending);
            }
        }

        return data;
    }

    /// <summary>Every provider × every status in the enum (so a status added later is covered automatically).</summary>
    public static TheoryData<string, ApplicationStatus> StatusData()
    {
        var data = new TheoryData<string, ApplicationStatus>();
        foreach (var provider in Providers)
        {
            foreach (var status in Enum.GetValues<ApplicationStatus>())
            {
                data.Add(provider, status);
            }
        }

        return data;
    }

    // ---- Paging -------------------------------------------------------------------------------------------------

    /// <summary>Without parameters: the first 10 rows in the default order, with the total and page count of all 30.</summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Default_query_returns_the_first_page_of_ten(string provider)
    {
        var page = await ListAsync(provider, Manager, isManager: true, ApplicationListQuery.Default);

        Assert.Equal((1, 10, 30, 3), (page.Page, page.PageSize, page.TotalCount, page.TotalPages));
        Assert.Equal(Expected(Rows(provider), ApplicationListQuery.Default).Take(10).Select(r => r.Id), page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Walking every page at each allowed size yields every row exactly once, in the overall order; the last page holds
    /// the remainder.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Pages_cover_every_row_once_in_order(string provider)
    {
        var expected = Expected(Rows(provider), ApplicationListQuery.Default).Select(r => r.Id).ToList();

        foreach (var size in ApplicationListQuery.AllowedPageSizes)
        {
            var first = await ListAsync(provider, Manager, true, Query(pageSize: size));
            var seen = new List<int>(first.Items.Select(i => i.Id));
            for (var number = 2; number <= first.TotalPages; number++)
            {
                var page = await ListAsync(provider, Manager, true, Query(page: number, pageSize: size));
                Assert.Equal(30, page.TotalCount);
                seen.AddRange(page.Items.Select(i => i.Id));
            }

            Assert.Equal(expected, seen);
            Assert.Equal((30 + size - 1) / size, first.TotalPages);
        }

        var last = await ListAsync(provider, Manager, true, Query(page: 2, pageSize: 25));
        Assert.Equal(5, last.Items.Count);
    }

    /// <summary>A page past the end is empty but still reports the filtered total; huge page numbers don't overflow.</summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Pages_beyond_the_end_are_empty_with_the_total(string provider)
    {
        var next = await ListAsync(provider, Manager, true, Query(page: 4));
        var huge = await ListAsync(provider, Manager, true, Query(page: int.MaxValue, pageSize: 100));

        Assert.Empty(next.Items);
        Assert.Equal((4, 30, 3), (next.Page, next.TotalCount, next.TotalPages));
        Assert.Empty(huge.Items);
        Assert.Equal(30, huge.TotalCount);
    }

    /// <summary>A filter that matches nothing: no rows, total 0, no pages.</summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Empty_result_has_no_rows_and_no_pages(string provider)
    {
        var page = await ListAsync(provider, Manager, true, Query(propertyId: 999_999));

        Assert.Empty(page.Items);
        Assert.Equal((0, 0), (page.TotalCount, page.TotalPages));
    }

    // ---- Sorting ------------------------------------------------------------------------------------------------

    /// <summary>Each column sorts in both directions exactly as the documented order (ties broken by id).</summary>
    [Theory]
    [MemberData(nameof(SortData))]
    public async Task Sorts_by_each_column_in_both_directions(string provider, ApplicationSortColumn column, SortDirection direction)
    {
        var query = Query(pageSize: 100, sortBy: ApplicationListQuery.SortKeyOf(column),
            sortDirection: direction == SortDirection.Ascending ? "asc" : "desc");

        var page = await ListAsync(provider, Manager, true, query);

        Assert.Equal(Expected(Rows(provider), query).Select(r => r.Id), page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The default order: submitted applications newest first, ties on the submission time broken by the last update
    /// and then the id, and never-submitted drafts last. Running it twice gives the same order.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Default_sort_is_newest_submitted_first_drafts_last_and_stable(string provider)
    {
        var first = await ListAsync(provider, Manager, true, Query(pageSize: 100));
        var again = await ListAsync(provider, Manager, true, Query(pageSize: 100));

        var submitted = first.Items.TakeWhile(i => i.SubmittedAt is not null).ToList();
        Assert.All(first.Items.Skip(submitted.Count), i => Assert.Null(i.SubmittedAt));
        Assert.Equal(submitted.Select(i => i.SubmittedAt).OrderDescending(), submitted.Select(i => i.SubmittedAt));
        Assert.Contains(submitted.GroupBy(i => i.SubmittedAt), g => g.Count() > 1);   // the data really has ties
        Assert.Equal(first.Items.Select(i => i.Id), again.Items.Select(i => i.Id));
    }

    // ---- Filtering ----------------------------------------------------------------------------------------------

    /// <summary>The status filter returns exactly the applications in that status, for every status in the enum.</summary>
    [Theory]
    [MemberData(nameof(StatusData))]
    public async Task Status_filter_returns_exactly_that_status(string provider, ApplicationStatus status)
    {
        var page = await ListAsync(provider, Manager, true, Query(pageSize: 100, status: status.ToString()));

        var expected = Rows(provider).Where(r => r.Status == status).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, page.TotalCount);
        Assert.Equal(expected.Select(r => r.Id).Order(), page.Items.Select(i => i.Id).Order());
    }

    /// <summary>The property filter returns exactly that property's applications, and the total counts only those.</summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Property_filter_returns_that_propertys_applications(string provider)
    {
        var property = Rows(provider).First(r => r.PropertyName == "Maple Court").PropertyId;

        var page = await ListAsync(provider, Manager, true, Query(pageSize: 10, propertyId: property));

        var expected = Rows(provider).Where(r => r.PropertyId == property).ToList();
        Assert.Equal(expected.Count, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal("Maple Court", i.PropertyName));
        Assert.Equal(Math.Min(10, expected.Count), page.Items.Count);
    }

    /// <summary>Status + property filters combine with a sort and a page: the right slice of the right rows.</summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Filters_combine_with_sorting_and_paging(string provider)
    {
        var rows = Rows(provider);
        var birch = rows.First(r => r.PropertyName == "Birch Hall").PropertyId;
        var status = rows.Where(r => r.PropertyId == birch).GroupBy(r => r.Status).OrderByDescending(g => g.Count()).First().Key;
        var query = Query(page: 1, pageSize: 10, sortBy: "unitNumber", sortDirection: "desc", status: status.ToString(), propertyId: birch);

        var page = await ListAsync(provider, Manager, true, query);

        var expected = Expected(rows, query).ToList();
        Assert.Equal(expected.Count, page.TotalCount);
        Assert.Equal(expected.Take(10).Select(r => r.Id), page.Items.Select(i => i.Id));
        Assert.All(page.Items, i => Assert.Equal((status, "Birch Hall"), (i.Status, i.PropertyName)));
    }

    // ---- Who sees what ------------------------------------------------------------------------------------------

    /// <summary>
    /// An applicant gets only their own applications, and their own total, whatever filter, sort or page they ask for;
    /// a manager gets everyone's.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Applicants_get_only_their_own_and_managers_get_all(string provider)
    {
        var rows = Rows(provider);
        var alices = rows.Where(r => r.ApplicantId == Alice).ToList();

        var own = await ListAsync(provider, Alice, false, Query(pageSize: 100, sortBy: "applicantName"));
        var all = await ListAsync(provider, Manager, true, Query(pageSize: 100, sortBy: "applicantName"));

        Assert.Equal(alices.Count, own.TotalCount);
        Assert.Equal(alices.Select(r => r.Id).Order(), own.Items.Select(i => i.Id).Order());
        Assert.All(own.Items, i => Assert.Equal("Alice Archer", i.ApplicantName));
        Assert.Equal(30, all.TotalCount);
        Assert.Equal(3, all.Items.Select(i => i.ApplicantName).Distinct().Count());
    }

    /// <summary>
    /// Filters never widen an applicant's view: for each property and status, Alice gets only her own matches (none
    /// where only others applied), and the totals agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderData))]
    public async Task Filters_never_reveal_other_applicants_rows(string provider)
    {
        var rows = Rows(provider);
        foreach (var propertyId in rows.Select(r => r.PropertyId).Distinct())
        {
            foreach (var status in Enum.GetValues<ApplicationStatus>())
            {
                var page = await ListAsync(provider, Alice, false, Query(pageSize: 100, status: status.ToString(), propertyId: propertyId));

                var expected = rows.Count(r => r.ApplicantId == Alice && r.PropertyId == propertyId && r.Status == status);
                Assert.Equal(expected, page.TotalCount);
                Assert.All(page.Items, i => Assert.Equal("Alice Archer", i.ApplicantName));
            }
        }
    }

    // ---- SQL ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// On SQL Server every sort column becomes an ORDER BY on its database column, paging becomes OFFSET … FETCH with
    /// parameters, and the filters are in the WHERE clause: nothing is sorted or paged in memory.
    /// </summary>
    [Fact]
    public void Paging_sorting_and_filtering_are_translated_to_SQL_Server()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True").Options);
        var service = new ApplicationService(db, _clock);
        var columnSql = new Dictionary<ApplicationSortColumn, string>
        {
            [ApplicationSortColumn.Id] = "[Id]",
            [ApplicationSortColumn.ApplicantName] = "[LastName]",
            [ApplicationSortColumn.PropertyName] = "[Name]",
            [ApplicationSortColumn.UnitNumber] = "[UnitNumber]",
            [ApplicationSortColumn.Status] = "[Status]",
            [ApplicationSortColumn.SubmittedDate] = "[SubmittedAt]",
            [ApplicationSortColumn.UpdatedDate] = "[UpdatedAt]",
            [ApplicationSortColumn.ClaimedDate] = "[ClaimedAt]",
        };

        foreach (var (column, sqlColumn) in columnSql)
        {
            var query = Query(page: 3, pageSize: 25, sortBy: ApplicationListQuery.SortKeyOf(column), sortDirection: "desc",
                status: "Submitted", propertyId: 7);

            var sql = service.QueryPage("alice", isManager: false, query).ToQueryString();

            Assert.Matches(@"OFFSET @\w+ ROWS FETCH NEXT @\w+ ROWS ONLY", sql);
            Assert.Matches($@"ORDER BY [^\n]*{System.Text.RegularExpressions.Regex.Escape(sqlColumn)} DESC", sql.Replace("\r", ""));
            Assert.Matches(@"WHERE \[\w+\]\.\[ApplicantId\] = @\w+ AND \[\w+\]\.\[Status\] = @\w+ AND \[\w+\]\.\[PropertyId\] = @\w+", sql);
            Assert.Contains("DECLARE @p int = 50;", sql);   // (3 - 1) * 25 rows skipped, as a parameter
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private IReadOnlyList<GridRow> Rows(string provider) => fixture.Rows[provider];

    private async Task<PagedResult<ApplicationListItem>> ListAsync(string provider, string userId, bool isManager, ApplicationListQuery query)
    {
        await using var db = fixture.CreateContext(provider);
        return await new ApplicationService(db, _clock).ListPageAsync(userId, isManager, query);
    }

    private static ApplicationListQuery Query(
        int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null, string? status = null, int? propertyId = null)
    {
        var result = ApplicationListQuery.Create(page, pageSize, sortBy, sortDirection, status, propertyId);
        Assert.True(result.Succeeded, result.Error);
        return result.Value!;
    }

    /// <summary>
    /// The oracle: the documented order computed in memory (ordinal text comparison; the seeded text has one case and
    /// sorts the same under SQL Server's default collation). Filters are applied as the manager sees them.
    /// </summary>
    private static IEnumerable<GridRow> Expected(IEnumerable<GridRow> rows, ApplicationListQuery query)
    {
        rows = rows.Where(r => (query.Status is null || r.Status == query.Status) && (query.PropertyId is null || r.PropertyId == query.PropertyId));
        var desc = query.SortDirection == SortDirection.Descending;
        var ordered = query.SortBy switch
        {
            ApplicationSortColumn.Id => By(rows, r => r.Id, desc),
            ApplicationSortColumn.ApplicantName => By(rows, r => r.LastName, desc).ThenBy(r => r.FirstName, desc),
            ApplicationSortColumn.PropertyName => By(rows, r => r.PropertyName, desc).ThenBy(r => r.UnitNumber, desc),
            ApplicationSortColumn.UnitNumber => By(rows, r => r.UnitNumber, desc).ThenBy(r => r.PropertyName, desc),
            ApplicationSortColumn.Status => By(rows, r => r.Status.ToString(), desc),
            ApplicationSortColumn.SubmittedDate => rows.OrderBy(r => r.SubmittedAt is null)
                .ThenBy(r => r.SubmittedAt, desc).ThenBy(r => r.UpdatedAt, desc),
            ApplicationSortColumn.UpdatedDate => By(rows, r => r.UpdatedAt, desc),
            ApplicationSortColumn.ClaimedDate => rows.OrderBy(r => r.ClaimedAt is null).ThenBy(r => r.ClaimedAt, desc),
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        return ordered.ThenBy(r => r.Id, desc);
    }

    private static IOrderedEnumerable<GridRow> By<TKey>(IEnumerable<GridRow> rows, Func<GridRow, TKey> key, bool desc) =>
        desc ? rows.OrderByDescending(key, KeyComparer<TKey>()) : rows.OrderBy(key, KeyComparer<TKey>());

    internal static IComparer<TKey> KeyComparer<TKey>() =>
        typeof(TKey) == typeof(string) ? (IComparer<TKey>)StringComparer.Ordinal : Comparer<TKey>.Default;
}

/// <summary>Direction-aware ThenBy for the in-memory oracle.</summary>
internal static class GridOrderingTestExtensions
{
    public static IOrderedEnumerable<T> ThenBy<T, TKey>(this IOrderedEnumerable<T> source, Func<T, TKey> key, bool descending)
    {
        var comparer = ApplicationGridQueryTests.KeyComparer<TKey>();
        return descending ? source.ThenByDescending(key, comparer) : source.ThenBy(key, comparer);
    }
}
