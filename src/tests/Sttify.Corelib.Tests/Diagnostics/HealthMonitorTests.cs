using Sttify.Corelib.Diagnostics;
using Xunit;

namespace Sttify.Corelib.Tests.Diagnostics;

public class HealthMonitorTests
{
    [Fact]
    public void Constructor_ShouldInitialize()
    {
        // Arrange & Act
        var healthMonitor = new HealthMonitor();

        // Assert
        Assert.NotNull(healthMonitor);
    }

    [Fact]
    public void RegisterHealthCheck_WithValidCheck_ShouldSucceed()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        var checkName = "TestCheck";
        Func<Task<HealthCheckResult>> healthCheck = () => Task.FromResult(HealthCheckResult.Healthy("Test passed"));

        // Act & Assert
        healthMonitor.RegisterHealthCheck(checkName, healthCheck); // Should not throw
    }

    [Fact]
    public async Task GetHealthStatusAsync_WithHealthyChecks_ShouldReturnHealthy()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        healthMonitor.RegisterHealthCheck("Check1", () => Task.FromResult(HealthCheckResult.Healthy("OK")));
        healthMonitor.RegisterHealthCheck("Check2", () => Task.FromResult(HealthCheckResult.Healthy("OK")));

        // Act
        var results = await healthMonitor.GetHealthStatusAsync();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(HealthStatus.Healthy, r.Value.Status));
    }

    [Fact]
    public async Task GetHealthStatusAsync_WithUnhealthyCheck_ShouldReturnUnhealthy()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        healthMonitor.RegisterHealthCheck("HealthyCheck", () => Task.FromResult(HealthCheckResult.Healthy("OK")));
        healthMonitor.RegisterHealthCheck("UnhealthyCheck", () => Task.FromResult(HealthCheckResult.Unhealthy("Error")));

        // Act
        var results = await healthMonitor.GetHealthStatusAsync();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal(HealthStatus.Healthy, results["HealthyCheck"].Status);
        Assert.Equal(HealthStatus.Unhealthy, results["UnhealthyCheck"].Status);
    }

    [Fact]
    public async Task GetHealthStatusAsync_WithExceptionInCheck_ShouldReturnUnhealthy()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        healthMonitor.RegisterHealthCheck("FailingCheck", () => throw new Exception("Test exception"));

        // Act
        var results = await healthMonitor.GetHealthStatusAsync();

        // Assert
        Assert.Single(results);
        Assert.Equal(HealthStatus.Unhealthy, results["FailingCheck"].Status);
        Assert.Contains("Test exception", results["FailingCheck"].Description);
    }

    [Fact]
    public async Task GetHealthStatusAsync_WithDegradedCheck_ShouldReturnDegraded()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        healthMonitor.RegisterHealthCheck("DegradedCheck", () => Task.FromResult(HealthCheckResult.Degraded("Warning")));

        // Act
        var results = await healthMonitor.GetHealthStatusAsync();

        // Assert
        Assert.Single(results);
        Assert.Equal(HealthStatus.Degraded, results["DegradedCheck"].Status);
        Assert.Equal("Warning", results["DegradedCheck"].Description);
    }

    [Fact]
    public void UnregisterHealthCheck_WithExistingCheck_ShouldRemove()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();
        var checkName = "TestCheck";
        healthMonitor.RegisterHealthCheck(checkName, () => Task.FromResult(HealthCheckResult.Healthy("OK")));

        // Act & Assert
        healthMonitor.UnregisterHealthCheck(checkName); // Should not throw
    }

    [Fact]
    public void UnregisterHealthCheck_WithNonExistentCheck_ShouldNotThrow()
    {
        // Arrange
        var healthMonitor = new HealthMonitor();

        // Act & Assert
        healthMonitor.UnregisterHealthCheck("NonExistentCheck"); // Should not throw
    }
}

public class HealthCheckResultTests
{
    [Fact]
    public void Healthy_ShouldCreateHealthyResult()
    {
        // Arrange
        var description = "All systems operational";

        // Act
        var result = HealthCheckResult.Healthy(description);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(description, result.Description);
    }

    [Fact]
    public void Degraded_ShouldCreateDegradedResult()
    {
        // Arrange
        var description = "Performance issues detected";

        // Act
        var result = HealthCheckResult.Degraded(description);

        // Assert
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(description, result.Description);
    }

    [Fact]
    public void Unhealthy_ShouldCreateUnhealthyResult()
    {
        // Arrange
        var description = "Critical system failure";

        // Act
        var result = HealthCheckResult.Unhealthy(description);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(description, result.Description);
    }
}