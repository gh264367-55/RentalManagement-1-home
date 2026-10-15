// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationListQueryTests.cs
// Purpose: Tests of ApplicationListQuery.Create, the single place raw list parameters are validated: defaults, the
//          page-size whitelist, invalid page numbers, the sort-column and direction whitelists, status names (every
//          status in the enum, so a status added later such as Under Review is accepted automatically) and property ids.
// =====================================================================================================================

using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>Tests of <see cref="ApplicationListQuery"/>.</summary>
public class ApplicationListQueryTests
{
    /// <summary>No parameters: page 1 of 10, most recently submitted first, no filters.</summary>
    [Fact]
    public void Defaults_to_the_first_page_of_ten_most_recently_submitted_first()
    {
        var query = Valid(ApplicationListQuery.Create());

        Assert.Equal((1, 10, ApplicationSortColumn.SubmittedDate, SortDirection.Descending, (ApplicationStatus?)null, (int?)null),
            (query.Page, query.PageSize, query.SortBy, query.SortDirection, query.Status, query.PropertyId));
        Assert.Equal(("submittedDate", "desc", 0L), (query.SortKey, query.SortDirectionKey, query.Skip));
        Assert.Equal(query, ApplicationListQuery.Default);
    }

    /// <summary>Only 10, 25, 50 and 100 rows per page are allowed.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void Allowed_page_sizes_are_accepted(int size) =>
        Assert.Equal(size, Valid(ApplicationListQuery.Create(pageSize: size)).PageSize);

    /// <summary>Any other page size is rejected (not clamped), with the error on "pageSize".</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(101)]
    [InlineData(1000)]
    public void Other_page_sizes_are_rejected(int size) =>
        AssertInvalid(ApplicationListQuery.Create(pageSize: size), "pageSize");

    /// <summary>The page number must be at least 1.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Page_numbers_below_one_are_rejected(int page) =>
        AssertInvalid(ApplicationListQuery.Create(page: page), "page");

    /// <summary>Huge page numbers are valid requests (they return an empty page); Skip is computed without overflow.</summary>
    [Fact]
    public void Huge_page_numbers_are_accepted_without_overflow()
    {
        var query = Valid(ApplicationListQuery.Create(page: int.MaxValue, pageSize: 100));

        Assert.Equal((long)(int.MaxValue - 1) * 100, query.Skip);
    }

    /// <summary>Every whitelisted sort key maps to its column, case-insensitively.</summary>
    [Theory]
    [InlineData("id", ApplicationSortColumn.Id)]
    [InlineData("applicantName", ApplicationSortColumn.ApplicantName)]
    [InlineData("PROPERTYNAME", ApplicationSortColumn.PropertyName)]
    [InlineData("unitnumber", ApplicationSortColumn.UnitNumber)]
    [InlineData("status", ApplicationSortColumn.Status)]
    [InlineData("submittedDate", ApplicationSortColumn.SubmittedDate)]
    [InlineData(" updatedDate ", ApplicationSortColumn.UpdatedDate)]
    public void Whitelisted_sort_keys_are_accepted(string key, ApplicationSortColumn column) =>
        Assert.Equal(column, Valid(ApplicationListQuery.Create(sortBy: key)).SortBy);

    /// <summary>Anything else, including SQL fragments and entity property names that aren't keys, is rejected.</summary>
    [Theory]
    [InlineData("ApplicantId")]
    [InlineData("Name; DROP TABLE RentalApplications")]
    [InlineData("Unit.Property.Name")]
    [InlineData("1")]
    public void Unknown_sort_keys_are_rejected(string key) =>
        AssertInvalid(ApplicationListQuery.Create(sortBy: key), "sortBy");

    /// <summary>"asc"/"desc" (and the long forms) are accepted; anything else is rejected.</summary>
    [Theory]
    [InlineData("asc", SortDirection.Ascending)]
    [InlineData("DESC", SortDirection.Descending)]
    [InlineData("ascending", SortDirection.Ascending)]
    [InlineData("descending", SortDirection.Descending)]
    public void Sort_directions_are_parsed(string value, SortDirection expected) =>
        Assert.Equal(expected, Valid(ApplicationListQuery.Create(sortBy: "id", sortDirection: value)).SortDirection);

    /// <summary>An unknown direction is an error.</summary>
    [Fact]
    public void Unknown_sort_direction_is_rejected() =>
        AssertInvalid(ApplicationListQuery.Create(sortDirection: "up"), "sortDirection");

    /// <summary>A column chosen without a direction uses its natural direction: dates newest first, text A→Z.</summary>
    [Theory]
    [InlineData("submittedDate", SortDirection.Descending)]
    [InlineData("updatedDate", SortDirection.Descending)]
    [InlineData("propertyName", SortDirection.Ascending)]
    [InlineData("id", SortDirection.Ascending)]
    public void A_column_without_a_direction_uses_its_natural_direction(string key, SortDirection expected) =>
        Assert.Equal(expected, Valid(ApplicationListQuery.Create(sortBy: key)).SortDirection);

    /// <summary>
    /// Every status in the enum can be used as a filter by its name, so a status added later (e.g. Under Review for
    /// bonus feature 2) works without changing the list code.
    /// </summary>
    public static TheoryData<ApplicationStatus> AllStatuses => [.. Enum.GetValues<ApplicationStatus>()];

    /// <summary>Each status name parses, case-insensitively.</summary>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Every_status_name_is_a_valid_filter(ApplicationStatus status)
    {
        Assert.Equal(status, Valid(ApplicationListQuery.Create(status: status.ToString())).Status);
        Assert.Equal(status, Valid(ApplicationListQuery.Create(status: status.ToString().ToUpperInvariant())).Status);
    }

    /// <summary>Multi-word status names may be written with spaces, hyphens or underscores ("Under Review").</summary>
    [Fact]
    public void Status_names_ignore_spaces_hyphens_and_underscores()
    {
        Assert.Equal(ApplicationStatus.Submitted, ApplicationListQuery.ParseStatus(" Sub mitted "));
        Assert.Equal(ApplicationStatus.Withdrawn, ApplicationListQuery.ParseStatus("with-drawn"));
        foreach (var status in Enum.GetValues<ApplicationStatus>())
        {
            var spaced = System.Text.RegularExpressions.Regex.Replace(status.ToString(), "(?<=[a-z])(?=[A-Z])", " ");
            Assert.Equal(status, ApplicationListQuery.ParseStatus(spaced));
        }
    }

    /// <summary>Unknown names and numbers are rejected, so only defined statuses reach the query.</summary>
    [Theory]
    [InlineData("Bogus")]
    [InlineData("1")]
    [InlineData("99")]
    public void Unknown_statuses_are_rejected(string status) =>
        AssertInvalid(ApplicationListQuery.Create(status: status), "status");

    /// <summary>A property id must be positive.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_property_ids_are_rejected(int propertyId) =>
        AssertInvalid(ApplicationListQuery.Create(propertyId: propertyId), "propertyId");

    /// <summary>Every invalid parameter is reported at once.</summary>
    [Fact]
    public void All_problems_are_reported_together()
    {
        var result = ApplicationListQuery.Create(page: 0, pageSize: 7, sortBy: "x", sortDirection: "y", status: "z", propertyId: 0);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.Equal(["page", "pageSize", "sortBy", "sortDirection", "status", "propertyId"], result.Errors.Select(e => e.Field));
    }

    /// <summary>Blank values mean "not given", as an empty query-string value or an "Any" option would send.</summary>
    [Fact]
    public void Blank_values_take_the_defaults() =>
        Assert.Equal(ApplicationListQuery.Default, Valid(ApplicationListQuery.Create(sortBy: " ", sortDirection: "", status: "")));

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static ApplicationListQuery Valid(OperationResult<ApplicationListQuery> result)
    {
        Assert.True(result.Succeeded, result.Error);
        return result.Value!;
    }

    private static void AssertInvalid(OperationResult<ApplicationListQuery> result, string field)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(field, Assert.Single(result.Errors).Field);
    }
}
