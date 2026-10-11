using PerformanceScheduler.Core.Exceptions;

namespace PerformanceScheduler.Tests;

public sealed class ConfigurationStoreRecoveryExceptionTests
{
    [Fact]
    public void Exception_ExposesStoreAndRecoveryFailureCount()
    {
        var operationFailure = new IOException("write failed");
        var recoveryFailure = new UnauthorizedAccessException("restore denied");

        var exception = new ConfigurationStoreRecoveryException(
            "profiles",
            operationFailure,
            [recoveryFailure]);

        Assert.Equal("profiles", exception.StoreName);
        Assert.Same(operationFailure, exception.OperationFailure);
        Assert.Equal(1, exception.RecoveryFailureCount);
        Assert.Same(recoveryFailure, exception.RecoveryFailures.Single());
        Assert.Same(operationFailure, exception.InnerException is AggregateException aggregate
            ? aggregate.InnerExceptions[0]
            : null);
    }
}
