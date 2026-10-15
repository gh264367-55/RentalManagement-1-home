// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Address.cs
// Purpose: Postal address value object shared by properties, residences and an applicant's current address. It has
//          no identity of its own; EF Core maps it as an owned type (see Data/Configurations/AddressMapping.cs).
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>A postal address. Mapped as an owned type, so it is stored as columns on its owner's table.</summary>
public class Address
{
    public string Street { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    /// <summary>Formats the address on one line, e.g. "1 Main St, Troy, NY 12180".</summary>
    /// <returns>The single-line address text.</returns>
    public override string ToString() => $"{Street}, {City}, {State} {PostalCode}";
}
