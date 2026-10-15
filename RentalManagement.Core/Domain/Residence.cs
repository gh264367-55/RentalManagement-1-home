// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Residence.cs
// Purpose: One prior residence in an application's Residence History section: address, landlord contact, and the
//          move-in/move-out dates. Belongs to exactly one application and is deleted with it.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>One prior residence in an application's Residence History section.</summary>
public class Residence
{
    public int Id { get; set; }

    public int RentalApplicationId { get; set; }

    public RentalApplication RentalApplication { get; set; } = null!;

    public Address Address { get; set; } = new();

    public string LandlordName { get; set; } = string.Empty;

    public string LandlordPhone { get; set; } = string.Empty;

    public DateOnly MoveInDate { get; set; }

    public DateOnly MoveOutDate { get; set; }
}
