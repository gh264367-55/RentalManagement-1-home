// =====================================================================================================================
// Module: RentalManagement.Tests / DependencyInjectionTests.cs
// Purpose: Verifies the Core layer's service registration (AddRentalManagementCore).
// =====================================================================================================================

using Microsoft.Extensions.DependencyInjection;
using RentalManagement.Core;

namespace RentalManagement.Tests;

/// <summary>Tests of <see cref="DependencyInjection.AddRentalManagementCore"/>.</summary>
public class DependencyInjectionTests
{
    /// <summary>Production code gets the real system clock unless a test registered a fake one first.</summary>
    [Fact]
    public void AddRentalManagementCore_registers_the_system_TimeProvider()
    {
        using var provider = new ServiceCollection().AddRentalManagementCore().BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }
}
