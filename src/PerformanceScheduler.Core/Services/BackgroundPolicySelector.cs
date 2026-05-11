using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class BackgroundPolicySelector
{
    public BackgroundProcessPolicy? Select(
        ProcessClassification classification,
        IEnumerable<BackgroundProcessPolicy> policies)
    {
        var policyList = policies.ToArray();
        if (policyList.Length == 0)
        {
            return null;
        }

        var classificationCategory = NormalizeCategory(classification.ToString());
        return policyList.FirstOrDefault(policy => NormalizeCategory(policy.Category) == classificationCategory) ??
               policyList.FirstOrDefault(policy => NormalizeCategory(policy.Category) == "default");
    }

    private static string NormalizeCategory(string category) =>
        category.Trim().Replace(" ", string.Empty).ToLowerInvariant();
}
