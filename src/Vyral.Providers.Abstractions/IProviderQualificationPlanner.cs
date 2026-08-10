namespace Vyral.Providers.Abstractions;

public interface IProviderQualificationPlanner
{
    IReadOnlyList<ProviderRunRequest> CreateQualificationRequests(ProviderQualificationRequest request);
}
