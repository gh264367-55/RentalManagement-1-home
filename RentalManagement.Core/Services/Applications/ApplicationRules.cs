// =====================================================================================================================
// Module: RentalManagement.Core / Services / Applications / ApplicationRules.cs
// Purpose: The single definition of the rental application's validation rules: Applicant Information (names, phone,
//          email, current address), each prior residence (address, landlord, move-in/move-out dates) and the Residence
//          History section as a whole, plus the property manager's review decision (outcome, comment for Return/Deny,
//          lease start date for Approve). Every rule reports the input field it belongs to. Also normalizes input
//          (trimming) and converts entities back to input records for re-validation at submit.
// =====================================================================================================================

using System.ComponentModel.DataAnnotations;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Services.Applications;

/// <summary>
/// The validation rules for each application section, defined once. The service applies them when a section is
/// saved and again at submit (the data could predate a rule, or have been saved by an older version), so the
/// browser's client-side checks are a convenience, never the authority. Every error names its input field.
/// </summary>
public static class ApplicationRules
{
    public const int NameLength = 100;
    public const int PhoneLength = 30;
    public const int EmailLength = 256;
    public const int LandlordNameLength = 150;

    private static readonly EmailAddressAttribute EmailFormat = new();

    /// <summary>
    /// Section 1 rules: first and last name, a valid phone number and email address, and a complete current address.
    /// </summary>
    /// <param name="input">The (normalized) applicant information.</param>
    /// <returns>Every problem found, one per field; empty when valid.</returns>
    public static IReadOnlyList<FieldError> ValidateApplicantInfo(ApplicantInfoInput input)
    {
        var errors = new List<FieldError>();
        Required(errors, input.FirstName, nameof(input.FirstName), "First name", NameLength);
        Required(errors, input.LastName, nameof(input.LastName), "Last name", NameLength);
        Phone(errors, input.Phone, nameof(input.Phone), "Phone");
        if (Required(errors, input.Email, nameof(input.Email), "Email", EmailLength) && !EmailFormat.IsValid(input.Email!.Trim()))
        {
            errors.Add(new FieldError(nameof(input.Email), "Enter a valid email address."));
        }

        Address(errors, input.Street, input.City, input.State, input.PostalCode);
        return errors;
    }

    /// <summary>
    /// A prior residence: full address, landlord contact, and dates where move-in is not after move-out and
    /// neither is in the future (the current address is in Applicant Information).
    /// </summary>
    /// <param name="input">The (normalized) residence.</param>
    /// <param name="today">Today's date, from the injected clock.</param>
    /// <returns>Every problem found; empty when valid.</returns>
    public static IReadOnlyList<FieldError> ValidateResidence(ResidenceInput input, DateOnly today)
    {
        var errors = new List<FieldError>();
        Address(errors, input.Street, input.City, input.State, input.PostalCode);
        Required(errors, input.LandlordName, nameof(input.LandlordName), "Landlord name", LandlordNameLength);
        Phone(errors, input.LandlordPhone, nameof(input.LandlordPhone), "Landlord phone");

        if (input.MoveInDate is null)
        {
            errors.Add(new FieldError(nameof(input.MoveInDate), "Move-in date is required."));
        }
        else if (input.MoveInDate > today)
        {
            errors.Add(new FieldError(nameof(input.MoveInDate), "Move-in date can't be in the future."));
        }

        if (input.MoveOutDate is null)
        {
            errors.Add(new FieldError(nameof(input.MoveOutDate), "Move-out date is required."));
        }
        else if (input.MoveOutDate > today)
        {
            errors.Add(new FieldError(nameof(input.MoveOutDate), "Move-out date can't be in the future. Enter your current home under Applicant Information."));
        }
        else if (input.MoveInDate is { } moveIn && input.MoveOutDate < moveIn)
        {
            errors.Add(new FieldError(nameof(input.MoveOutDate), "Move-out date must be on or after the move-in date."));
        }

        return errors;
    }

    /// <summary>Residence History is complete when it has at least one residence and every residence is valid.</summary>
    /// <param name="residences">The application's saved residences.</param>
    /// <param name="today">Today's date, from the injected clock.</param>
    /// <returns>A section-level message (null field) when incomplete; empty when complete.</returns>
    public static IReadOnlyList<FieldError> ValidateResidenceHistory(IReadOnlyCollection<ResidenceInput> residences, DateOnly today)
    {
        if (residences.Count == 0)
        {
            return [new FieldError(null, "Add at least one prior residence before you continue.")];
        }

        return residences.Any(r => ValidateResidence(r, today).Count > 0)
            ? [new FieldError(null, "One or more residences have missing or invalid details. Edit them before you continue.")]
            : [];
    }

    /// <summary>The longest reviewer comment accepted (the column is nvarchar(2000)).</summary>
    public const int CommentLength = 2000;

    /// <summary>How far ahead a lease may start, counted from the approval day.</summary>
    public const int MaxLeaseStartDaysAhead = 365;

    /// <summary>
    /// Review rules: an outcome must be chosen; Return and Deny need a comment (the applicant and the history need to
    /// know why); Approve needs a lease start date that is not in the past and at most a year ahead.
    /// </summary>
    /// <param name="input">The (normalized) review decision.</param>
    /// <param name="today">Today's date, from the injected clock.</param>
    /// <returns>Every problem found, one per field; empty when valid.</returns>
    public static IReadOnlyList<FieldError> ValidateReview(ReviewInput input, DateOnly today)
    {
        var errors = new List<FieldError>();
        if (input.Outcome is not { } outcome || !Enum.IsDefined(outcome))
        {
            errors.Add(new FieldError(nameof(input.Outcome), "Choose an outcome: Approve, Return or Deny."));
            return errors;
        }

        if (outcome.RequiresComment() && string.IsNullOrWhiteSpace(input.Comment))
        {
            errors.Add(new FieldError(nameof(input.Comment),
                $"A comment is required to {outcome.ToString().ToLowerInvariant()} an application, so the applicant knows why."));
        }
        else if (input.Comment?.Trim().Length > CommentLength)
        {
            errors.Add(new FieldError(nameof(input.Comment), $"Comment must be at most {CommentLength} characters."));
        }

        if (outcome == ReviewOutcome.Approve)
        {
            if (input.LeaseStartDate is not { } start)
            {
                errors.Add(new FieldError(nameof(input.LeaseStartDate), "Lease start date is required to approve."));
            }
            else if (start < today)
            {
                errors.Add(new FieldError(nameof(input.LeaseStartDate), "Lease start date can't be in the past."));
            }
            else if (start > today.AddDays(MaxLeaseStartDaysAhead))
            {
                errors.Add(new FieldError(nameof(input.LeaseStartDate),
                    $"Lease start date must be within {MaxLeaseStartDaysAhead} days of today."));
            }
        }

        return errors;
    }

    /// <summary>Trims every field; blank values become null so "required" checks treat them as missing.</summary>
    /// <param name="input">The raw applicant information.</param>
    /// <returns>The normalized copy.</returns>
    public static ApplicantInfoInput Normalize(ApplicantInfoInput input) => new(
        Trim(input.FirstName), Trim(input.LastName), Trim(input.Phone), Trim(input.Email),
        Trim(input.Street), Trim(input.City), Trim(input.State), Trim(input.PostalCode));

    /// <summary>Trims every text field; blank values become null. Dates are left as they are.</summary>
    /// <param name="input">The raw residence.</param>
    /// <returns>The normalized copy.</returns>
    public static ResidenceInput Normalize(ResidenceInput input) => input with
    {
        Street = Trim(input.Street),
        City = Trim(input.City),
        State = Trim(input.State),
        PostalCode = Trim(input.PostalCode),
        LandlordName = Trim(input.LandlordName),
        LandlordPhone = Trim(input.LandlordPhone),
    };

    /// <summary>Adds an error for each missing or over-long address field (Street, City, State, PostalCode).</summary>
    private static void Address(List<FieldError> errors, string? street, string? city, string? state, string? postalCode)
    {
        Required(errors, street, nameof(ApplicantInfoInput.Street), "Street", 200);
        Required(errors, city, nameof(ApplicantInfoInput.City), "City", 100);
        Required(errors, state, nameof(ApplicantInfoInput.State), "State", 50);
        Required(errors, postalCode, nameof(ApplicantInfoInput.PostalCode), "Postal code", 20);
    }

    /// <summary>A phone number: 10–15 digits, optionally formatted with spaces, dashes, dots, parentheses or a leading +.</summary>
    private static void Phone(List<FieldError> errors, string? value, string field, string label)
    {
        if (!Required(errors, value, field, label, PhoneLength) || value is null)
        {
            return;
        }

        var digits = value.Count(char.IsDigit);
        var allowed = value.All(c => char.IsDigit(c) || c is ' ' or '-' or '.' or '(' or ')' or '+');
        if (!allowed || digits is < 10 or > 15)
        {
            errors.Add(new FieldError(field, $"Enter a valid {label.ToLowerInvariant()} number, e.g. (518) 555-0123."));
        }
    }

    /// <summary>Adds an error and returns false when the value is missing or too long.</summary>
    private static bool Required(List<FieldError> errors, string? value, string field, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new FieldError(field, $"{label} is required."));
            return false;
        }

        if (value.Trim().Length > maxLength)
        {
            errors.Add(new FieldError(field, $"{label} must be at most {maxLength} characters."));
            return false;
        }

        return true;
    }

    /// <summary>Trims a value, returning null for null, empty or whitespace.</summary>
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The applicant info stored on an application, as an input record.</summary>
    internal static ApplicantInfoInput FromEntity(RentalApplication a) => new(
        a.FirstName, a.LastName, a.Phone, a.Email,
        a.CurrentAddress?.Street, a.CurrentAddress?.City, a.CurrentAddress?.State, a.CurrentAddress?.PostalCode);

    /// <summary>A saved residence, as an input record (for re-validation).</summary>
    internal static ResidenceInput FromEntity(Residence r) => new(
        r.Address.Street, r.Address.City, r.Address.State, r.Address.PostalCode,
        r.LandlordName, r.LandlordPhone, r.MoveInDate, r.MoveOutDate);
}
