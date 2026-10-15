// =====================================================================================================================
// Module: RentalManagement.Core / Domain / Property.cs
// Purpose: A building or complex maintained by property managers. It owns its units (deleting a property deletes its
//          units) and carries a concurrency stamp so two managers editing it at once cannot overwrite each other.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>A building or complex maintained by property managers. Owns its units.</summary>
public class Property
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Address Address { get; set; } = new();

    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public ICollection<Unit> Units { get; set; } = [];
}
