// =====================================================================================================================
// Module: RentalManagement.Core / Services / Properties / PropertyModels.cs
// Purpose: The data contracts of PropertyService: input records for property and unit commands, and read models
//          (lists, edit details, dropdown options, portfolio numbers, available units) projected directly in SQL.
// =====================================================================================================================

namespace RentalManagement.Core.Services.Properties;

// Inputs and read models for PropertyService. Read models are projected in SQL, so only the columns they need are read.

/// <summary>Fields a property manager enters for a property.</summary>
public sealed record PropertyInput(string Name, string Street, string City, string State, string PostalCode);

/// <summary>Fields a property manager enters for a unit.</summary>
public sealed record UnitInput(string UnitNumber, int Bedrooms, decimal MonthlyRent, int UnitTypeId);

/// <summary>A property as shown in the management list.</summary>
public sealed record PropertySummary(int Id, string Name, string Address, int UnitCount, bool CanDelete);

/// <summary>A property loaded for editing.</summary>
public sealed record PropertyDetails(int Id, PropertyInput Input, Guid ConcurrencyStamp);

/// <summary>A unit as shown in a property's unit table.</summary>
public sealed record UnitRow(
    int Id,
    int PropertyId,
    string UnitNumber,
    int Bedrooms,
    decimal MonthlyRent,
    string UnitTypeName,
    bool UnitTypeIsActive,
    DateOnly? LeasedUntil,
    int ApplicationCount,
    bool CanDelete);

/// <summary>A unit loaded for editing.</summary>
public sealed record UnitDetails(int Id, int PropertyId, string PropertyName, UnitInput Input, Guid ConcurrencyStamp);

/// <summary>An entry of the unit-type dropdown.</summary>
public sealed record UnitTypeOption(int Id, string Name, bool IsActive);

/// <summary>An entry of a property dropdown.</summary>
public sealed record PropertyOption(int Id, string Name);

/// <summary>Headline numbers for the property manager's portfolio.</summary>
public sealed record PortfolioStats(int Properties, int Units, int LeasedUnits, int AvailableUnits, int AwaitingReview);

/// <summary>Criteria for browsing available units. Every criterion is optional.</summary>
public sealed record AvailableUnitFilter(int? PropertyId = null, int? MinBedrooms = null, decimal? MaxRent = null, int? UnitTypeId = null);

/// <summary>A unit an applicant can apply for.</summary>
public sealed record AvailableUnit(
    int Id,
    int PropertyId,
    string PropertyName,
    string Address,
    string UnitNumber,
    int Bedrooms,
    decimal MonthlyRent,
    string UnitTypeName);
