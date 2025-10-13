using Microsoft.Extensions.DependencyInjection;
using Sttify.Corelib.Plugins;
using Xunit;

namespace Sttify.Corelib.Tests.Plugin;

public class PluginManagerTests
{
    private IServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Constructor_WithServiceProvider_ShouldInitialize()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();

        // Act
        var pluginManager = new PluginManager(serviceProvider);

        // Assert
        Assert.NotNull(pluginManager);
    }

    [Fact]
    public void Constructor_WithNullServiceProvider_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new PluginManager(null!));
    }

    [Fact]
    public async Task LoadAllPluginsAsync_WithNoPlugins_ShouldNotThrow()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var pluginManager = new PluginManager(serviceProvider);

        // Act & Assert
        await pluginManager.LoadAllPluginsAsync(); // Should not throw
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var pluginManager = new PluginManager(serviceProvider);

        // Act & Assert
        pluginManager.Dispose(); // Should not throw
    }
}
