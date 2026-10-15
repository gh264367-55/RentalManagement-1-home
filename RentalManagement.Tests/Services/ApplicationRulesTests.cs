// =====================================================================================================================
// Module: RentalManagement.Tests / Services / ApplicationRulesTests.cs
// Purpose: Unit tests of the application's validation rules (ApplicationRules): required fields, email and phone
//          formats, residence dates, and the "at least one residence" rule. Also provides the valid sample inputs that
//          the ApplicationService tests reuse.
// =====================================================================================================================

using RentalManagement.Core.Services.Applications;

namespace RentalManagement.Tests.Services;

/// <summary>Tests of <see cref="ApplicationRules"/>.</summary>
public class ApplicationRulesTests
{
    /// <summary>The fixed "today" used by the date rules.</summary>
    private static readonly DateOnly Today = new(2026, 10, 8);

    /// <summary>Applicant information that passes every rule.</summary>
    internal static readonly ApplicantInfoInput ValidInfo =
        new("Ada", "Lovelace", "(518) 555-0123", "ada@example.com", "1 Main St", "Troy", "NY", "12180");

    /// <summary>A prior residence that passes every rule.</summary>
    internal static readonly ResidenceInput ValidResidence =
        new("9 Elm St", "Albany", "NY", "12203", "Pat Landlord", "518-555-0199", new DateOnly(2023, 1, 1), new DateOnly(2025, 6, 30));

    /// <summary>Complete, well-formed applicant information produces no errors.</summary>
    [Fact]
    public void Valid_applicant_info_has_no_errors() =>
        Assert.Empty(ApplicationRules.ValidateApplicantInfo(ValidInfo));

    /// <summary>Every missing field is reported (not just the first), each against its own field name.</summary>
    [Fact]
    public void Every_missing_applicant_field_is_reported_against_its_field()
    {
        var errors = ApplicationRules.ValidateApplicantInfo(new ApplicantInfoInput(null, " ", null, null, null, null, null, null));

        Assert.Equal(
            ["FirstName", "LastName", "Phone", "Email", "Street", "City", "State", "PostalCode"],
            errors.Select(e => e.Field));
    }

    /// <summary>A malformed email gives exactly one error, on the Email field.</summary>
    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    public void Invalid_email_is_rejected(string email)
    {
        var errors = ApplicationRules.ValidateApplicantInfo(ValidInfo with { Email = email });

        Assert.Equal("Email", Assert.Single(errors).Field);
    }

    /// <summary>Phone numbers need 10–15 digits and may only use common phone punctuation.</summary>
    [Theory]
    [InlineData("555-0123", false)]          // too few digits
    [InlineData("call me maybe", false)]     // letters
    [InlineData("(518) 555-0123", true)]
    [InlineData("+1 518.555.0123", true)]
    public void Phone_needs_10_to_15_digits_and_phone_punctuation_only(string phone, bool valid) =>
        Assert.Equal(valid, ApplicationRules.ValidateApplicantInfo(ValidInfo with { Phone = phone }).Count == 0);

    /// <summary>A complete residence in the past produces no errors.</summary>
    [Fact]
    public void Valid_residence_has_no_errors() =>
        Assert.Empty(ApplicationRules.ValidateResidence(ValidResidence, Today));

    /// <summary>Move-out before move-in is reported on the move-out field, where the user fixes it.</summary>
    [Fact]
    public void Move_out_before_move_in_is_rejected_on_the_move_out_field()
    {
        var errors = ApplicationRules.ValidateResidence(
            ValidResidence with { MoveInDate = new DateOnly(2025, 1, 1), MoveOutDate = new DateOnly(2024, 1, 1) }, Today);

        Assert.Equal("MoveOutDate", Assert.Single(errors).Field);
    }

    /// <summary>The date comparison is inclusive: moving in and out on the same day is valid.</summary>
    [Fact]
    public void Same_day_move_in_and_out_is_allowed() =>
        Assert.Empty(ApplicationRules.ValidateResidence(
            ValidResidence with { MoveInDate = Today, MoveOutDate = Today }, Today));

    /// <summary>Prior residences can't have future dates (the current home belongs in Applicant Information).</summary>
    [Fact]
    public void Future_dates_are_rejected()
    {
        var errors = ApplicationRules.ValidateResidence(
            ValidResidence with { MoveInDate = Today.AddDays(1), MoveOutDate = Today.AddDays(30) }, Today);

        Assert.Equal(["MoveInDate", "MoveOutDate"], errors.Select(e => e.Field));
    }

    /// <summary>An empty residence reports every required field, each against its own field name.</summary>
    [Fact]
    public void Missing_residence_fields_are_each_reported()
    {
        var errors = ApplicationRules.ValidateResidence(new ResidenceInput(null, null, null, null, null, null, null, null), Today);

        Assert.Equal(
            ["Street", "City", "State", "PostalCode", "LandlordName", "LandlordPhone", "MoveInDate", "MoveOutDate"],
            errors.Select(e => e.Field));
    }

    /// <summary>Residence History is incomplete with no residences and complete with one valid residence.</summary>
    [Fact]
    public void Residence_history_needs_at_least_one_residence()
    {
        Assert.NotEmpty(ApplicationRules.ValidateResidenceHistory([], Today));
        Assert.Empty(ApplicationRules.ValidateResidenceHistory([ValidResidence], Today));
    }
}
