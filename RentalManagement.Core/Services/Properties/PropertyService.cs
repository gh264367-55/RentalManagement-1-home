// =====================================================================================================================
// Module: RentalManagement.Core / Services / Properties / PropertyService.cs
// Purpose: Business logic for property and unit maintenance (property managers) and for browsing available units
//          (applicants). Queries project straight to read models in SQL; commands validate input, enforce the
//          unit-number, unit-type and removal rules, and use concurrency stamps to reject stale edits.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using RentalManagement.Core.Data;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Properties;

/// <summary>
/// Properties and units: the queries behind the management screens and the applicant's unit browser, and the
/// commands that enforce the maintenance rules:
/// <list type="bullet">
///   <item>a unit number is unique within its property;</item>
///   <item>a unit type must exist, and an inactive type may stay on the unit that already has it but can't be
///   chosen for a new unit or a type change — checked against the <b>persisted</b> type, never the browser's;</item>
///   <item>a property or unit with applications or leases can't be removed (their history must survive);</item>
///   <item>edits carry the concurrency stamp they were loaded with, so a stale form can't overwrite newer data.</item>
/// </list>
/// Field names in errors match the input record's property names.
/// </summary>
public sealed class PropertyService(ApplicationDbContext db, TimeProvider clock)
{
    private const string StaleMessage =
        "Someone else changed this {0} after you opened it. Close the dialog and open it again to see the latest version.";

    // ---- Queries --------------------------------------------------------------------------------------------

    /// <summary>
    /// Every property, by name, with its one-line address, unit count, and whether it may be removed (no unit has an
    /// application or lease).
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The property list.</returns>
    public async Task<IReadOnlyList<PropertySummary>> GetPropertiesAsync(CancellationToken cancellationToken = default) =>
        await db.Properties
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new PropertySummary(
                p.Id,
                p.Name,
                p.Address.Street + ", " + p.Address.City + ", " + p.Address.State + " " + p.Address.PostalCode,
                p.Units.Count,
                !p.Units.Any(u => u.Applications.Any() || u.Leases.Any())))
            .ToListAsync(cancellationToken);

    /// <summary>A property's editable fields and concurrency stamp, for the edit modal.</summary>
    /// <param name="id">The property id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The property, or null when it doesn't exist.</returns>
    public async Task<PropertyDetails?> GetPropertyAsync(int id, CancellationToken cancellationToken = default) =>
        await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PropertyDetails(
                p.Id,
                new PropertyInput(p.Name, p.Address.Street, p.Address.City, p.Address.State, p.Address.PostalCode),
                p.ConcurrencyStamp))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Id and name of every property, by name, for filter dropdowns.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The dropdown options.</returns>
    public async Task<IReadOnlyList<PropertyOption>> GetPropertyOptionsAsync(CancellationToken cancellationToken = default) =>
        await db.Properties
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new PropertyOption(p.Id, p.Name))
            .ToListAsync(cancellationToken);

    /// <summary>A property's units with today's lease status, for the management table.</summary>
    /// <param name="propertyId">The property whose units to list.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The units ordered by unit number.</returns>
    public async Task<IReadOnlyList<UnitRow>> GetUnitsAsync(int propertyId, CancellationToken cancellationToken = default)
    {
        var today = clock.GetToday();
        return await db.Units
            .AsNoTracking()
            .Where(u => u.PropertyId == propertyId)
            .OrderBy(u => u.UnitNumber)
            .Select(u => new UnitRow(
                u.Id,
                u.PropertyId,
                u.UnitNumber,
                u.Bedrooms,
                u.MonthlyRent,
                u.UnitType.Name,
                u.UnitType.IsActive,
                u.Leases.Where(l => l.StartDate <= today && l.EndDate >= today)
                    .Select(l => (DateOnly?)l.EndDate)
                    .FirstOrDefault(),
                u.Applications.Count,
                !u.Applications.Any() && !u.Leases.Any()))
            .ToListAsync(cancellationToken);
    }

    /// <summary>A unit's editable fields, its property and its concurrency stamp, for the edit and remove modals.</summary>
    /// <param name="id">The unit id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The unit, or null when it doesn't exist.</returns>
    public async Task<UnitDetails?> GetUnitAsync(int id, CancellationToken cancellationToken = default) =>
        await db.Units
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new UnitDetails(
                u.Id,
                u.PropertyId,
                u.Property.Name,
                new UnitInput(u.UnitNumber, u.Bedrooms, u.MonthlyRent, u.UnitTypeId),
                u.ConcurrencyStamp))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The unit types a manager may pick: every active type, plus the persisted type of unit <paramref name="unitId"/>
    /// when it is inactive (so it still displays and can be kept). Pass null for a new unit.
    /// </summary>
    /// <param name="unitId">The unit being edited, or null when adding a unit.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The selectable types in display order.</returns>
    public async Task<IReadOnlyList<UnitTypeOption>> GetSelectableUnitTypesAsync(
        int? unitId, CancellationToken cancellationToken = default)
    {
        var currentTypeId = unitId is null
            ? null
            : await db.Units.Where(u => u.Id == unitId).Select(u => (int?)u.UnitTypeId).SingleOrDefaultAsync(cancellationToken);

        return await db.UnitTypes
            .AsNoTracking()
            .Where(t => t.IsActive || t.Id == currentTypeId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new UnitTypeOption(t.Id, t.Name, t.IsActive))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Every unit type, active or not, for filtering lists of existing units.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All unit types in display order.</returns>
    public async Task<IReadOnlyList<UnitTypeOption>> GetAllUnitTypesAsync(CancellationToken cancellationToken = default) =>
        await db.UnitTypes
            .AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new UnitTypeOption(t.Id, t.Name, t.IsActive))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The manager's headline numbers: properties, units, units leased today, units available today, and
    /// applications awaiting review (Submitted). Each number is a COUNT query.
    /// </summary>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The portfolio numbers.</returns>
    public async Task<PortfolioStats> GetPortfolioStatsAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.GetToday();
        var properties = await db.Properties.CountAsync(cancellationToken);
        var units = await db.Units.CountAsync(cancellationToken);
        var available = await db.Units.AvailableOn(today).CountAsync(cancellationToken);
        var awaitingReview = await db.RentalApplications.CountAsync(a => a.Status == ApplicationStatus.Submitted, cancellationToken);
        return new PortfolioStats(properties, units, units - available, available, awaitingReview);
    }

    /// <summary>Units with no lease covering today, filtered in SQL.</summary>
    /// <param name="filter">Optional criteria (property, minimum bedrooms, maximum rent, unit type).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching units ordered by property name, then unit number.</returns>
    public async Task<IReadOnlyList<AvailableUnit>> GetAvailableUnitsAsync(
        AvailableUnitFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.Units.AsNoTracking().AvailableOn(clock.GetToday());

        if (filter.PropertyId is { } propertyId)
        {
            query = query.Where(u => u.PropertyId == propertyId);
        }

        if (filter.MinBedrooms is { } minBedrooms)
        {
            query = query.Where(u => u.Bedrooms >= minBedrooms);
        }

        if (filter.MaxRent is { } maxRent)
        {
            query = query.Where(u => u.MonthlyRent <= maxRent);
        }

        if (filter.UnitTypeId is { } unitTypeId)
        {
            query = query.Where(u => u.UnitTypeId == unitTypeId);
        }

        return await query
            .OrderBy(u => u.Property.Name).ThenBy(u => u.UnitNumber)
            .Select(u => new AvailableUnit(
                u.Id,
                u.PropertyId,
                u.Property.Name,
                u.Property.Address.Street + ", " + u.Property.Address.City + ", " + u.Property.Address.State + " " + u.Property.Address.PostalCode,
                u.UnitNumber,
                u.Bedrooms,
                u.MonthlyRent,
                u.UnitType.Name))
            .ToListAsync(cancellationToken);
    }

    // ---- Property commands ------------------------------------------------------------------------------------

    /// <summary>Validates and adds a property.</summary>
    /// <param name="input">The property's name and address.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The new property's id, or the validation errors.</returns>
    public async Task<OperationResult<int>> CreatePropertyAsync(PropertyInput input, CancellationToken cancellationToken = default)
    {
        input = Normalize(input);
        if (ValidateProperty(input) is { } invalid)
        {
            return OperationResult<int>.From(invalid);
        }

        var property = new Property();
        Apply(property, input);
        db.Properties.Add(property);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult<int>.Success(property.Id);
    }

    /// <summary>
    /// Validates and saves a property edit, rejecting it when the property changed since the form was opened.
    /// </summary>
    /// <param name="id">The property id.</param>
    /// <param name="input">The new name and address.</param>
    /// <param name="concurrencyStamp">The stamp the edit form was loaded with.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Success, the validation errors, or a stale-edit / not-found failure.</returns>
    public async Task<OperationResult> UpdatePropertyAsync(
        int id, PropertyInput input, Guid concurrencyStamp, CancellationToken cancellationToken = default)
    {
        input = Normalize(input);
        if (ValidateProperty(input) is { } invalid)
        {
            return invalid;
        }

        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (property is null)
        {
            return OperationResult.Failure("This property no longer exists.");
        }

        if (property.ConcurrencyStamp != concurrencyStamp)
        {
            return OperationResult.Failure(string.Format(StaleMessage, "property"));
        }

        Apply(property, input);
        property.ConcurrencyStamp = Guid.NewGuid();
        return await SaveEditAsync("property", cancellationToken);
    }

    /// <summary>
    /// Removes a property and its units, unless any of its units has an application or a lease (their history must
    /// be kept).
    /// </summary>
    /// <param name="id">The property id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Success, or why the property can't be removed.</returns>
    public async Task<OperationResult> DeletePropertyAsync(int id, CancellationToken cancellationToken = default)
    {
        // Units are loaded so EF deletes them itself; the database cascade is a second line of defence.
        var property = await db.Properties.Include(p => p.Units).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (property is null)
        {
            return OperationResult.Failure("This property no longer exists.");
        }

        if (await db.Units.AnyAsync(u => u.PropertyId == id && (u.Applications.Any() || u.Leases.Any()), cancellationToken))
        {
            return OperationResult.Failure(
                "This property cannot be removed because some of its units have applications or leases.");
        }

        db.Properties.Remove(property);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    // ---- Unit commands ----------------------------------------------------------------------------------------

    /// <summary>
    /// Validates and adds a unit to a property. The unit number must be unique in the property and the unit type must
    /// exist and be active.
    /// </summary>
    /// <param name="propertyId">The property to add the unit to.</param>
    /// <param name="input">The unit's number, bedrooms, rent and type.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The new unit's id, or the validation errors.</returns>
    public async Task<OperationResult<int>> CreateUnitAsync(
        int propertyId, UnitInput input, CancellationToken cancellationToken = default)
    {
        input = Normalize(input);
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, cancellationToken))
        {
            return OperationResult<int>.Failure("This property no longer exists.");
        }

        var invalid = await ValidateUnitAsync(propertyId, unitId: null, currentUnitTypeId: null, input, cancellationToken);
        if (invalid is not null)
        {
            return OperationResult<int>.From(invalid);
        }

        var unit = new Unit { PropertyId = propertyId };
        Apply(unit, input);
        db.Units.Add(unit);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult<int>.Success(unit.Id);
    }

    /// <summary>
    /// Validates and saves a unit edit. An inactive unit type is accepted only if it is the unit's persisted type;
    /// a stale form (different concurrency stamp) is rejected.
    /// </summary>
    /// <param name="id">The unit id.</param>
    /// <param name="input">The new unit values.</param>
    /// <param name="concurrencyStamp">The stamp the edit form was loaded with.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Success, the validation errors, or a stale-edit / not-found failure.</returns>
    public async Task<OperationResult> UpdateUnitAsync(
        int id, UnitInput input, Guid concurrencyStamp, CancellationToken cancellationToken = default)
    {
        input = Normalize(input);
        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return OperationResult.Failure("This unit no longer exists.");
        }

        if (unit.ConcurrencyStamp != concurrencyStamp)
        {
            return OperationResult.Failure(string.Format(StaleMessage, "unit"));
        }

        // The persisted type decides whether an inactive type may stay; the posted form can't influence that.
        var invalid = await ValidateUnitAsync(unit.PropertyId, unit.Id, unit.UnitTypeId, input, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        Apply(unit, input);
        unit.ConcurrencyStamp = Guid.NewGuid();
        return await SaveEditAsync("unit", cancellationToken);
    }

    /// <summary>Removes a unit unless it has an application or a lease.</summary>
    /// <param name="id">The unit id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Success, or why the unit can't be removed.</returns>
    public async Task<OperationResult> DeleteUnitAsync(int id, CancellationToken cancellationToken = default)
    {
        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return OperationResult.Failure("This unit no longer exists.");
        }

        if (await db.Units.AnyAsync(u => u.Id == id && (u.Applications.Any() || u.Leases.Any()), cancellationToken))
        {
            return OperationResult.Failure("This unit cannot be removed because it has applications or leases.");
        }

        db.Units.Remove(unit);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    // ---- Rules ------------------------------------------------------------------------------------------------

    /// <summary>Checks that every property field is present and within its length.</summary>
    /// <param name="input">The normalized property input.</param>
    /// <returns>The first problem found, or null when valid.</returns>
    private static OperationResult? ValidateProperty(PropertyInput input) =>
        Required(input.Name, nameof(PropertyInput.Name), "Name", 150)
        ?? Required(input.Street, nameof(PropertyInput.Street), "Street", 200)
        ?? Required(input.City, nameof(PropertyInput.City), "City", 100)
        ?? Required(input.State, nameof(PropertyInput.State), "State", 50)
        ?? Required(input.PostalCode, nameof(PropertyInput.PostalCode), "Postal code", 20);

    /// <summary>
    /// The unit rules: number present, 0–10 bedrooms, positive rent, number unique within the property, type exists,
    /// and an inactive type only when it is the unit's persisted type.
    /// </summary>
    /// <param name="propertyId">The unit's property.</param>
    /// <param name="unitId">The unit being edited (excluded from the duplicate check), or null for a new unit.</param>
    /// <param name="currentUnitTypeId">The unit's persisted type id, or null for a new unit.</param>
    /// <param name="input">The normalized unit input.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The first problem found, or null when valid.</returns>
    private async Task<OperationResult?> ValidateUnitAsync(
        int propertyId, int? unitId, int? currentUnitTypeId, UnitInput input, CancellationToken cancellationToken)
    {
        if (Required(input.UnitNumber, nameof(UnitInput.UnitNumber), "Unit number", 20) is { } missing)
        {
            return missing;
        }

        if (input.Bedrooms is < 0 or > 10)
        {
            return OperationResult.Failure("Bedrooms must be between 0 and 10.", nameof(UnitInput.Bedrooms));
        }

        if (input.MonthlyRent <= 0)
        {
            return OperationResult.Failure("Monthly rent must be greater than zero.", nameof(UnitInput.MonthlyRent));
        }

        var duplicate = await db.Units.AnyAsync(
            u => u.PropertyId == propertyId && u.UnitNumber == input.UnitNumber && u.Id != unitId, cancellationToken);
        if (duplicate)
        {
            return OperationResult.Failure(
                $"Unit {input.UnitNumber} already exists in this property.", nameof(UnitInput.UnitNumber));
        }

        var type = await db.UnitTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == input.UnitTypeId, cancellationToken);
        if (type is null)
        {
            return OperationResult.Failure("Choose a unit type.", nameof(UnitInput.UnitTypeId));
        }

        if (!type.CanBeAssignedTo(currentUnitTypeId))
        {
            return OperationResult.Failure(
                $"Unit type '{type.Name}' is inactive and can't be assigned to this unit.", nameof(UnitInput.UnitTypeId));
        }

        return null;
    }

    /// <summary>Checks that a text value is present and not longer than <paramref name="maxLength"/>.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="field">The input field name used in the error.</param>
    /// <param name="label">The human-readable field name used in the message.</param>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>A failure, or null when the value is acceptable.</returns>
    private static OperationResult? Required(string value, string field, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OperationResult.Failure($"{label} is required.", field);
        }

        return value.Length > maxLength
            ? OperationResult.Failure($"{label} must be at most {maxLength} characters.", field)
            : null;
    }

    /// <summary>Trims every property field and turns nulls into empty strings.</summary>
    private static PropertyInput Normalize(PropertyInput input) => new(
        (input.Name ?? string.Empty).Trim(),
        (input.Street ?? string.Empty).Trim(),
        (input.City ?? string.Empty).Trim(),
        (input.State ?? string.Empty).Trim(),
        (input.PostalCode ?? string.Empty).Trim());

    /// <summary>Trims the unit number (so " 101 " and "101" count as duplicates).</summary>
    private static UnitInput Normalize(UnitInput input) =>
        input with { UnitNumber = (input.UnitNumber ?? string.Empty).Trim() };

    /// <summary>Copies validated input onto a property entity.</summary>
    private static void Apply(Property property, PropertyInput input)
    {
        // Update the owned address in place; replacing the instance would make EF delete and re-insert it.
        property.Name = input.Name;
        property.Address.Street = input.Street;
        property.Address.City = input.City;
        property.Address.State = input.State;
        property.Address.PostalCode = input.PostalCode;
    }

    /// <summary>Copies validated input onto a unit entity.</summary>
    private static void Apply(Unit unit, UnitInput input)
    {
        unit.UnitNumber = input.UnitNumber;
        unit.Bedrooms = input.Bedrooms;
        unit.MonthlyRent = input.MonthlyRent;
        unit.UnitTypeId = input.UnitTypeId;
    }

    /// <summary>
    /// Saves an edit whose concurrency stamp the caller has just rotated. EF adds <c>WHERE ConcurrencyStamp = @loaded</c>,
    /// so a save that races another one between our stamp check and this write fails instead of overwriting it.
    /// </summary>
    private async Task<OperationResult> SaveEditAsync(string noun, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult.Failure(string.Format(StaleMessage, noun));
        }
    }
}
