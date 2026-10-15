// =====================================================================================================================
// Module: RentalManagement.Tests / Services / PropertyServiceTests.cs
// Purpose: Tests of PropertyService against an EF Core in-memory database and a fake clock: the inactive unit-type
//          rule, unit-number uniqueness and value checks, stale-edit rejection, removal rules, availability around
//          lease start/end dates, the available-units filters, and the portfolio numbers.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Tests.Services;

/// <summary>
/// Tests of <see cref="PropertyService"/>. Each test gets a fresh database with three unit types (one inactive) and
/// one property holding a unit on the inactive type ("101") and a unit on an active type ("102").
/// </summary>
public sealed class PropertyServiceTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplicationDbContext _db;
    private readonly PropertyService _service;

    private readonly UnitType _studio = new() { Name = "Studio", IsActive = true, SortOrder = 1 };
    private readonly UnitType _apartment = new() { Name = "Apartment", IsActive = true, SortOrder = 2 };
    private readonly UnitType _legacy = new() { Name = "Garden Suite", IsActive = false, SortOrder = 3 };
    private readonly Property _property;
    private readonly Unit _legacyUnit;
    private readonly Unit _plainUnit;

    /// <summary>Creates the per-test database and seeds the unit types, property, units and an applicant user.</summary>
    public PropertyServiceTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        _db = NewContext();
        _service = new PropertyService(_db, _clock);

        _property = new Property
        {
            Name = "Maple Court",
            Address = new Address { Street = "1 Maple St", City = "Troy", State = "NY", PostalCode = "12180" },
        };
        _legacyUnit = new Unit { UnitNumber = "101", Bedrooms = 1, MonthlyRent = 1200m, UnitType = _legacy };
        _plainUnit = new Unit { UnitNumber = "102", Bedrooms = 2, MonthlyRent = 1500m, UnitType = _apartment };
        _property.Units.Add(_legacyUnit);
        _property.Units.Add(_plainUnit);

        _db.UnitTypes.AddRange(_studio, _apartment, _legacy);
        _db.Properties.Add(_property);
        _db.Users.Add(new ApplicationUser { Id = "applicant", UserName = "a@test", Email = "a@test" });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    /// <summary>Disposes the test's DbContext.</summary>
    public void Dispose() => _db.Dispose();

    // ---- Unit type rule ---------------------------------------------------------------------------------------

    /// <summary>A new unit can't take an inactive type; the error lands on the UnitTypeId field.</summary>
    [Fact]
    public async Task New_unit_cannot_use_an_inactive_type()
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput("103", 1, 1000m, _legacy.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(UnitInput.UnitTypeId), result.Field);
        Assert.Contains("inactive", result.Error);
    }

    /// <summary>An existing unit can't switch to an inactive type, and its stored type is unchanged.</summary>
    [Fact]
    public async Task Existing_unit_cannot_change_to_an_inactive_type()
    {
        var result = await _service.UpdateUnitAsync(
            _plainUnit.Id, new UnitInput("102", 2, 1500m, _legacy.Id), _plainUnit.ConcurrencyStamp);

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(UnitInput.UnitTypeId), result.Field);
        Assert.Equal(_apartment.Id, (await _db.Units.AsNoTracking().SingleAsync(u => u.Id == _plainUnit.Id)).UnitTypeId);
    }

    /// <summary>A unit already on an inactive type can be saved with that type kept (other fields change).</summary>
    [Fact]
    public async Task Unit_that_already_has_an_inactive_type_can_keep_it()
    {
        var result = await _service.UpdateUnitAsync(
            _legacyUnit.Id, new UnitInput("101", 1, 1250m, _legacy.Id), _legacyUnit.ConcurrencyStamp);

        Assert.True(result.Succeeded, result.Error);
        var saved = await _db.Units.AsNoTracking().SingleAsync(u => u.Id == _legacyUnit.Id);
        Assert.Equal(1250m, saved.MonthlyRent);
        Assert.Equal(_legacy.Id, saved.UnitTypeId);
    }

    /// <summary>Once a unit leaves its inactive type, it can't switch back to it.</summary>
    [Fact]
    public async Task Unit_can_move_off_an_inactive_type_but_not_back()
    {
        var moved = await _service.UpdateUnitAsync(
            _legacyUnit.Id, new UnitInput("101", 0, 1100m, _studio.Id), _legacyUnit.ConcurrencyStamp);
        Assert.True(moved.Succeeded, moved.Error);

        var stamp = (await _service.GetUnitAsync(_legacyUnit.Id))!.ConcurrencyStamp;
        var back = await _service.UpdateUnitAsync(_legacyUnit.Id, new UnitInput("101", 0, 1100m, _legacy.Id), stamp);

        Assert.False(back.Succeeded);
    }

    /// <summary>The dropdown offers active types, plus the unit's own type only when that type is inactive.</summary>
    [Fact]
    public async Task Selectable_types_are_active_types_plus_the_units_own_type()
    {
        var forNewUnit = await _service.GetSelectableUnitTypesAsync(unitId: null);
        var forLegacyUnit = await _service.GetSelectableUnitTypesAsync(_legacyUnit.Id);
        var forPlainUnit = await _service.GetSelectableUnitTypesAsync(_plainUnit.Id);

        Assert.Equal(["Studio", "Apartment"], forNewUnit.Select(t => t.Name));
        Assert.Equal(["Studio", "Apartment", "Garden Suite"], forLegacyUnit.Select(t => t.Name));
        Assert.Equal(["Studio", "Apartment"], forPlainUnit.Select(t => t.Name));
    }

    /// <summary>
    /// The inactive-type exception belongs to the unit, not the property: a new unit in the same property as the
    /// unit that keeps the inactive type still can't choose it.
    /// </summary>
    [Fact]
    public async Task Inactive_type_exception_does_not_extend_to_the_units_property()
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput("104", 1, 1100m, _legacy.Id));
        var options = await _service.GetSelectableUnitTypesAsync(unitId: null);

        Assert.Equal(nameof(UnitInput.UnitTypeId), result.Field);
        Assert.DoesNotContain(options, t => t.Id == _legacy.Id);
    }

    /// <summary>Re-activating a type makes it selectable again everywhere (the rule follows the lookup's flag).</summary>
    [Fact]
    public async Task Reactivated_type_is_selectable_again()
    {
        var legacy = await _db.UnitTypes.SingleAsync(t => t.Id == _legacy.Id);
        legacy.IsActive = true;
        await _db.SaveChangesAsync();

        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput("104", 1, 1100m, _legacy.Id));

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>A type id that doesn't exist (e.g. a forged post) is rejected on the UnitTypeId field.</summary>
    [Fact]
    public async Task Unknown_unit_type_is_rejected()
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput("103", 1, 1000m, UnitTypeId: 999));

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(UnitInput.UnitTypeId), result.Field);
    }

    // ---- Unit maintenance -------------------------------------------------------------------------------------

    /// <summary>A duplicate unit number in the same property is rejected, even with surrounding spaces.</summary>
    [Fact]
    public async Task Unit_number_must_be_unique_within_a_property()
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput(" 101 ", 1, 1000m, _studio.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(UnitInput.UnitNumber), result.Field);
    }

    /// <summary>Unit numbers only need to be unique within a property, not across properties.</summary>
    [Fact]
    public async Task Same_unit_number_is_fine_in_another_property()
    {
        var other = await _service.CreatePropertyAsync(new PropertyInput("Oak Row", "2 Oak St", "Troy", "NY", "12180"));

        var result = await _service.CreateUnitAsync(other.Value, new UnitInput("101", 1, 1000m, _studio.Id));

        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>A valid unit is saved with its number trimmed and every field stored as entered.</summary>
    [Fact]
    public async Task Created_unit_is_saved_with_trimmed_number()
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput("  201 ", 0, 950m, _studio.Id));

        Assert.True(result.Succeeded, result.Error);
        var unit = await _db.Units.AsNoTracking().SingleAsync(u => u.Id == result.Value);
        Assert.Equal(("201", 0, 950m, _studio.Id), (unit.UnitNumber, unit.Bedrooms, unit.MonthlyRent, unit.UnitTypeId));
    }

    /// <summary>The service re-checks number, bedrooms and rent itself and names the offending field.</summary>
    [Theory]
    [InlineData("", 1, 1000, nameof(UnitInput.UnitNumber))]
    [InlineData("103", -1, 1000, nameof(UnitInput.Bedrooms))]
    [InlineData("103", 11, 1000, nameof(UnitInput.Bedrooms))]
    [InlineData("103", 1, 0, nameof(UnitInput.MonthlyRent))]
    public async Task Invalid_unit_values_are_rejected_on_the_server(string number, int bedrooms, decimal rent, string field)
    {
        var result = await _service.CreateUnitAsync(_property.Id, new UnitInput(number, bedrooms, rent, _studio.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(field, result.Field);
    }

    /// <summary>A second save with the old concurrency stamp is refused and doesn't overwrite the first.</summary>
    [Fact]
    public async Task Stale_unit_edit_is_rejected()
    {
        var first = await _service.UpdateUnitAsync(
            _plainUnit.Id, new UnitInput("102", 2, 1600m, _apartment.Id), _plainUnit.ConcurrencyStamp);
        var second = await _service.UpdateUnitAsync(
            _plainUnit.Id, new UnitInput("102", 2, 1700m, _apartment.Id), _plainUnit.ConcurrencyStamp);

        Assert.True(first.Succeeded, first.Error);
        Assert.False(second.Succeeded);
        Assert.Contains("Someone else changed", second.Error);
        Assert.Equal(1600m, (await _db.Units.AsNoTracking().SingleAsync(u => u.Id == _plainUnit.Id)).MonthlyRent);
    }

    /// <summary>A unit referenced by an application can't be removed, so the application's history survives.</summary>
    [Fact]
    public async Task Unit_with_an_application_cannot_be_removed()
    {
        await AddApplicationAsync(_plainUnit.Id);

        var result = await _service.DeleteUnitAsync(_plainUnit.Id);

        Assert.False(result.Succeeded);
        Assert.True(await _db.Units.AnyAsync(u => u.Id == _plainUnit.Id));
    }

    /// <summary>A unit with no applications or leases is removed.</summary>
    [Fact]
    public async Task Unit_without_history_can_be_removed()
    {
        var result = await _service.DeleteUnitAsync(_plainUnit.Id);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(await _db.Units.AnyAsync(u => u.Id == _plainUnit.Id));
    }

    // ---- Property maintenance ---------------------------------------------------------------------------------

    /// <summary>A property can be added, renamed with its stamp, and appears in the list with its formatted address.</summary>
    [Fact]
    public async Task Property_is_created_updated_and_listed()
    {
        var created = await _service.CreatePropertyAsync(new PropertyInput(" Birch Hall ", "3 Birch Rd", "Albany", "NY", "12203"));
        var details = await _service.GetPropertyAsync(created.Value);
        var updated = await _service.UpdatePropertyAsync(
            created.Value, details!.Input with { Name = "Birch Hall East" }, details.ConcurrencyStamp);

        Assert.True(updated.Succeeded, updated.Error);
        var list = await _service.GetPropertiesAsync();
        Assert.Contains(list, p => p.Name == "Birch Hall East" && p.Address == "3 Birch Rd, Albany, NY 12203" && p.UnitCount == 0);
    }

    /// <summary>A blank address field (City here) is rejected on that field.</summary>
    [Fact]
    public async Task Property_requires_every_address_field()
    {
        var result = await _service.CreatePropertyAsync(new PropertyInput("Elm", "4 Elm St", " ", "NY", "12180"));

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(PropertyInput.City), result.Field);
    }

    /// <summary>A property edit carrying an outdated concurrency stamp is refused.</summary>
    [Fact]
    public async Task Stale_property_edit_is_rejected()
    {
        var input = new PropertyInput("Maple Court II", "1 Maple St", "Troy", "NY", "12180");

        var result = await _service.UpdatePropertyAsync(_property.Id, input, Guid.NewGuid());

        Assert.False(result.Succeeded);
    }

    /// <summary>A property whose unit has an application can't be removed, and the list shows it as not removable.</summary>
    [Fact]
    public async Task Property_whose_units_have_applications_cannot_be_removed()
    {
        await AddApplicationAsync(_plainUnit.Id);

        var result = await _service.DeletePropertyAsync(_property.Id);

        Assert.False(result.Succeeded);
        Assert.False((await _service.GetPropertiesAsync()).Single().CanDelete);
    }

    /// <summary>Removing a property with no history also removes all of its units.</summary>
    [Fact]
    public async Task Property_without_history_is_removed_with_its_units()
    {
        var result = await _service.DeletePropertyAsync(_property.Id);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(await _db.Properties.AnyAsync());
        Assert.False(await _db.Units.AnyAsync());
    }

    // ---- Availability -----------------------------------------------------------------------------------------

    /// <summary>Available units exclude a unit leased today and include the others.</summary>
    [Fact]
    public async Task Unit_with_a_lease_covering_today_is_not_available()
    {
        await AddLeaseAsync(_plainUnit.Id, start: Today.AddMonths(-2));

        var available = await _service.GetAvailableUnitsAsync(new AvailableUnitFilter());

        Assert.DoesNotContain(available, u => u.Id == _plainUnit.Id);
        Assert.Contains(available, u => u.Id == _legacyUnit.Id);
    }

    /// <summary>Expired and future leases leave a unit available; a lease running today (from its first day) makes it unavailable.</summary>
    [Theory]
    [InlineData(-400, true)] // lease ended about a month ago
    [InlineData(-364, false)] // lease started 364 days ago: still running
    [InlineData(0, false)]   // lease starts today
    [InlineData(30, true)]   // lease starts next month: available until then
    public async Task Availability_depends_on_whether_the_lease_covers_today(int startDaysFromToday, bool available)
    {
        await AddLeaseAsync(_plainUnit.Id, Today.AddDays(startDaysFromToday));

        var units = await _service.GetAvailableUnitsAsync(new AvailableUnitFilter());

        Assert.Equal(available, units.Any(u => u.Id == _plainUnit.Id));
    }

    /// <summary>A lease's last day is inclusive: the unit shows as leased until today.</summary>
    [Fact]
    public async Task Lease_ending_today_still_covers_today()
    {
        await AddLeaseAsync(_plainUnit.Id, start: Today.AddMonths(-12).AddDays(1)); // ends today

        var rows = await _service.GetUnitsAsync(_property.Id);

        Assert.Equal(Today, rows.Single(r => r.Id == _plainUnit.Id).LeasedUntil);
    }

    /// <summary>Each filter (property, minimum bedrooms, maximum rent, unit type) narrows the available units.</summary>
    [Fact]
    public async Task Available_units_are_filtered_by_every_criterion()
    {
        var other = await _service.CreatePropertyAsync(new PropertyInput("Oak Row", "2 Oak St", "Troy", "NY", "12180"));
        await _service.CreateUnitAsync(other.Value, new UnitInput("A", 3, 2400m, _apartment.Id));

        Assert.Single(await _service.GetAvailableUnitsAsync(new AvailableUnitFilter(PropertyId: other.Value)));
        Assert.Equal(2, (await _service.GetAvailableUnitsAsync(new AvailableUnitFilter(MinBedrooms: 2))).Count);
        Assert.Equal(["101", "102"], (await _service.GetAvailableUnitsAsync(new AvailableUnitFilter(MaxRent: 1500m))).Select(u => u.UnitNumber));
        Assert.Equal(["101"], (await _service.GetAvailableUnitsAsync(new AvailableUnitFilter(UnitTypeId: _legacy.Id))).Select(u => u.UnitNumber));
    }

    /// <summary>The portfolio numbers count properties, units, leased and available units, and applications awaiting review.</summary>
    [Fact]
    public async Task Portfolio_stats_count_leased_and_available_units()
    {
        await AddLeaseAsync(_plainUnit.Id, start: Today);

        var stats = await _service.GetPortfolioStatsAsync();

        Assert.Equal(new PortfolioStats(Properties: 1, Units: 2, LeasedUnits: 1, AvailableUnits: 1, AwaitingReview: 0), stats);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    /// <summary>A context on this test's in-memory database.</summary>
    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>Gives a unit history by adding a draft application for it.</summary>
    /// <param name="unitId">The unit applied for.</param>
    /// <returns>The saved application.</returns>
    private async Task<RentalApplication> AddApplicationAsync(int unitId)
    {
        var application = RentalApplication.StartDraft(unitId, "applicant", _clock.GetUtcNow());
        _db.RentalApplications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    /// <summary>Puts a twelve-month lease on a unit through an approved application.</summary>
    /// <param name="unitId">The unit to lease.</param>
    /// <param name="start">The lease's first day.</param>
    private async Task AddLeaseAsync(int unitId, DateOnly start)
    {
        var application = RentalApplication.StartDraft(unitId, "applicant", _clock.GetUtcNow());
        application.TransitionTo(ApplicationStatus.Submitted, "applicant", _clock.GetUtcNow());
        application.Claim("applicant", _clock.GetUtcNow());
        application.TransitionTo(ApplicationStatus.Approved, "applicant", _clock.GetUtcNow(), ReviewOutcome.Approve);
        application.Lease = Lease.Issue(application, 1500m, start, _clock.GetUtcNow());
        _db.RentalApplications.Add(application);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
