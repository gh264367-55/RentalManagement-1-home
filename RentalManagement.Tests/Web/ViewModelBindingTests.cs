// =====================================================================================================================
// Module: RentalManagement.Tests / Web / ViewModelBindingTests.cs
// Purpose: Overposting guards on the view models posted by forms: server-decided values (editability, headers,
//          dropdown options, context shown in dialogs, list rows) must be [BindNever], and no view model may expose a
//          domain entity for model binding.
// =====================================================================================================================

using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using RentalManagement.Core.Domain;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Tests.Web;

/// <summary>Reflection tests that posted view models can't be used to set server-decided values.</summary>
public class ViewModelBindingTests
{
    /// <summary>Properties the server decides; a post must never be able to set them.</summary>
    public static TheoryData<Type, string> ServerDecided => new()
    {
        { typeof(ApplicationPageViewModel), nameof(ApplicationPageViewModel.Header) },
        { typeof(ApplicationPageViewModel), nameof(ApplicationPageViewModel.SubmitBlockers) },
        { typeof(ApplicantInfoViewModel), nameof(ApplicantInfoViewModel.IsEditable) },
        { typeof(UnitFormViewModel), nameof(UnitFormViewModel.UnitTypeOptions) },
        { typeof(UnitFormViewModel), nameof(UnitFormViewModel.PropertyName) },
        { typeof(ReviewFormViewModel), nameof(ReviewFormViewModel.UnitLabel) },
        { typeof(ReviewFormViewModel), nameof(ReviewFormViewModel.ApplicantName) },
        { typeof(ReviewFormViewModel), nameof(ReviewFormViewModel.MonthlyRent) },
        { typeof(ApplicationListViewModel), nameof(ApplicationListViewModel.Grid) },
        { typeof(ApplicationListViewModel), nameof(ApplicationListViewModel.IsManager) },
        { typeof(AvailableUnitsViewModel), nameof(AvailableUnitsViewModel.Units) },
    };

    /// <summary>Each server-decided property carries [BindNever].</summary>
    [Theory]
    [MemberData(nameof(ServerDecided))]
    public void Server_decided_values_cannot_be_posted(Type viewModel, string property) =>
        Assert.NotNull(viewModel.GetProperty(property)!.GetCustomAttribute<BindNeverAttribute>());

    /// <summary>
    /// The page view model binds exactly the section on screen and the Section 1 fields; the status, owner, unit and
    /// completion flags exist only on the entity, which is never bound.
    /// </summary>
    [Fact]
    public void Application_page_binds_only_the_section_and_applicant_info()
    {
        var bindable = typeof(ApplicationPageViewModel).GetProperties()
            .Where(p => p.CanWrite && p.GetCustomAttribute<BindNeverAttribute>() is null)
            .Select(p => p.Name)
            .Order();

        Assert.Equal(["ApplicantInfo", "CurrentSection", "Id"], bindable);
    }

    /// <summary>No view model has a property typed as a domain entity, so entities are never model-bound.</summary>
    [Fact]
    public void No_view_model_exposes_a_domain_entity()
    {
        var entityTypes = typeof(RentalApplication).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(RentalApplication).Namespace && t.IsClass && !t.IsAbstract && !t.IsSealed)
            .ToHashSet();
        var offenders = typeof(ApplicationPageViewModel).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ApplicationPageViewModel).Namespace)
            .SelectMany(t => t.GetProperties().Where(p => entityTypes.Contains(p.PropertyType)).Select(p => $"{t.Name}.{p.Name}"));

        Assert.Empty(offenders);
    }
}
