// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Unit.cs
// Purpose: A rentable unit of a property (unit number, bedrooms, monthly rent, unit type). Leases and applications
//          point at it; a unit whose lease covers today is not available for new applications.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>A rentable unit in a <see cref="Property"/>. Unavailable while a lease covers today.</summary>
public class Unit
{
    public int Id { get; set; }

    public int PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    /// <summary>Unique within the property, e.g. "101".</summary>
    public string UnitNumber { get; set; } = string.Empty;

    /// <summary>0 for a studio.</summary>
    public int Bedrooms { get; set; }

    public decimal MonthlyRent { get; set; }

    public int UnitTypeId { get; set; }

    public UnitType UnitType { get; set; } = null!;

    /// <summary>
    /// Rotated on every change, and also when a lease is issued, so two managers approving applications for the
    /// same unit at the same moment cannot both commit.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public ICollection<Lease> Leases { get; set; } = [];

    public ICollection<RentalApplication> Applications { get; set; } = [];
}
