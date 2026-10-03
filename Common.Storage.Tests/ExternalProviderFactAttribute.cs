using Xunit;

namespace Common.Storage.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExternalProviderFactAttribute : FactAttribute
{
    public ExternalProviderFactAttribute(string requiredEnvironmentVariables, string? guidance = null)
    {
        string[] required = requiredEnvironmentVariables
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string[] missing = required
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = $"External provider proof skipped. Missing: {string.Join(", ", missing)}." +
                (string.IsNullOrWhiteSpace(guidance) ? string.Empty : $" {guidance}");
        }
    }
}
