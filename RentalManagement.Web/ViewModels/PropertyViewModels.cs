using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Web.ViewModels;

/// <summary>The add/edit property modal. Property names match <see cref="PropertyInput"/> so service errors land on the right field.</summary>
public sealed class PropertyFormViewModel
{
    /// <summary>Null when adding.</summary>
    public int? Id { get; set; }

    [Required, StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Street { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string City { get; set; } = string.Empty;

    [Required, StringLength(50)]
    public string State { get; set; } = string.Empty;

    [Required, StringLength(20), Display(Name = "Postal code")]
    public string PostalCode { get; set; } = string.Empty;

    public Guid ConcurrencyStamp { get; set; }

    public bool IsNew => Id is null;

    /// <summary>The form as the service's input record.</summary>
    /// <returns>The property input.</returns>
    public PropertyInput ToInput() => new(Name, Street, City, State, PostalCode);

    /// <summary>A pre-filled edit form for an existing property.</summary>
    /// <param name="details">The property loaded from the database.</param>
    /// <returns>The form model.</returns>
    public static PropertyFormViewModel From(PropertyDetails details) => new()
    {
        Id = details.Id,
        Name = details.Input.Name,
        Street = details.Input.Street,
        City = details.Input.City,
        State = details.Input.State,
        PostalCode = details.Input.PostalCode,
        ConcurrencyStamp = details.ConcurrencyStamp,
    };
}

/// <summary>The add/edit unit modal. Property names match <see cref="UnitInput"/> so service errors land on the right field.</summary>
public sealed class UnitFormViewModel
{
    /// <summary>Null when adding.</summary>
    public int? Id { get; set; }

    public int PropertyId { get; set; }

    [Required, StringLength(20), Display(Name = "Unit number")]
    public string UnitNumber { get; set; } = string.Empty;

    [Required, Range(0, 10)]
    public int? Bedrooms { get; set; }

    [Required, Range(1, 100_000), Display(Name = "Monthly rent")]
    public decimal? MonthlyRent { get; set; }

    [Required(ErrorMessage = "Choose a unit type."), Display(Name = "Unit type")]
    public int? UnitTypeId { get; set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>Shown in the dialog title; reloaded on the server, never bound.</summary>
    [BindNever, ValidateNever]
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Active types plus this unit's persisted type. Built on the server, never bound.</summary>
    [BindNever, ValidateNever]
    public IReadOnlyList<SelectListItem> UnitTypeOptions { get; set; } = [];

    public bool IsNew => Id is null;

    /// <summary>The form as the service's input record (missing numbers become 0 and fail the service rules).</summary>
    /// <returns>The unit input.</returns>
    public UnitInput ToInput() => new(UnitNumber, Bedrooms ?? 0, MonthlyRent ?? 0, UnitTypeId ?? 0);

    /// <summary>A pre-filled edit form for an existing unit (the type options are loaded separately).</summary>
    /// <param name="details">The unit loaded from the database.</param>
    /// <returns>The form model.</returns>
    public static UnitFormViewModel From(UnitDetails details) => new()
    {
        Id = details.Id,
        PropertyId = details.PropertyId,
        PropertyName = details.PropertyName,
        UnitNumber = details.Input.UnitNumber,
        Bedrooms = details.Input.Bedrooms,
        MonthlyRent = details.Input.MonthlyRent,
        UnitTypeId = details.Input.UnitTypeId,
        ConcurrencyStamp = details.ConcurrencyStamp,
    };
}

/// <summary>A confirmation modal (<c>_Confirm</c>): remove a property or unit, withdraw an application.</summary>
public sealed class ConfirmViewModel
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>Where the confirm button posts.</summary>
    public required string ActionUrl { get; init; }

    /// <summary>
    /// Space-separated selectors of the regions to reload afterwards, or <c>page</c> to reload the whole page
    /// (when the action changes what the page may show, e.g. a withdrawn application becomes read-only).
    /// </summary>
    public required string Refresh { get; init; }

    public string ConfirmText { get; init; } = "Remove";

    public string ConfirmCss { get; init; } = "btn-danger";
}

/// <summary>The applicant's "Available units" page: GET filter fields plus the matching units.</summary>
public sealed class AvailableUnitsViewModel
{
    [Display(Name = "Property")]
    public int? PropertyId { get; set; }

    [Display(Name = "Bedrooms (at least)"), Range(0, 10)]
    public int? MinBedrooms { get; set; }

    [Display(Name = "Max rent"), Range(1, 100_000)]
    public decimal? MaxRent { get; set; }

    [Display(Name = "Unit type")]
    public int? UnitTypeId { get; set; }

    [BindNever, ValidateNever]
    public IReadOnlyList<AvailableUnit> Units { get; set; } = [];

    [BindNever, ValidateNever]
    public IReadOnlyList<SelectListItem> PropertyOptions { get; set; } = [];

    [BindNever, ValidateNever]
    public IReadOnlyList<SelectListItem> UnitTypeOptions { get; set; } = [];

    public bool HasFilter => PropertyId is not null || MinBedrooms is not null || MaxRent is not null || UnitTypeId is not null;

    /// <summary>The criteria as the service's filter record.</summary>
    /// <returns>The filter.</returns>
    public AvailableUnitFilter ToFilter() => new(PropertyId, MinBedrooms, MaxRent, UnitTypeId);
}
