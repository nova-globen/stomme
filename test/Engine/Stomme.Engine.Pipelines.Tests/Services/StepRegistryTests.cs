using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Tests.Services;

/// <summary>
/// Unit tests for StepRegistry, specifically testing multi-tenant descriptor resolution logic and refresh behavior.
/// </summary>
public class StepRegistryTests
{
    private const string TestStepType = "TestStep";
    private const string CurrentTenant = "tenant1";

    private readonly ILogger<StepRegistry> _logger;
    private readonly StepRegistry _registry;

    public StepRegistryTests()
    {
        var tenantAccessor = Substitute.For<ITenantAccessor>();
        var stepDescriber = Substitute.For<IStepDescriber>();
        _logger = Substitute.For<ILogger<StepRegistry>>();
        _registry = new(stepDescriber, [], tenantAccessor, _logger);
        
        // Set default tenant for all tests
        tenantAccessor.TenantId.Returns(CurrentTenant);
    }

    [Fact]
    public void Find_TenantSpecificPreferredOverTenantAgnostic_WhenBothExist()
    {
        // Arrange
        var tenantSpecific = CreateDescriptor(TestStepType, 1, CurrentTenant);
        var tenantAgnostic = CreateDescriptor(TestStepType, 2, Tenant.AgnosticTenantId); // Higher version
        RegisterDescriptors(tenantSpecific, tenantAgnostic);

        // Act
        var result = _registry.Find(TestStepType);

        // Assert - tenant-specific should be preferred even though it has a lower version
        AssertDescriptor(result, CurrentTenant, 1);
    }

    [Fact]
    public void Find_ReturnsTenantAgnostic_WhenNoTenantSpecificExists()
    {
        // Arrange
        var tenantAgnostic = CreateDescriptor(TestStepType, 1, Tenant.AgnosticTenantId);
        RegisterDescriptors(tenantAgnostic);

        // Act
        var result = _registry.Find(TestStepType);

        // Assert
        AssertDescriptor(result, Tenant.AgnosticTenantId, 1);
    }

    [Theory]
    [InlineData(1, 2, 3, 3)] // Multiple versions, expect highest
    [InlineData(3, 1, 2, 3)] // Out of order registration
    [InlineData(1, 1, 1, 1)] // Same version multiple times
    public void Find_ReturnsHighestVersionTenantSpecific_WhenMultipleTenantSpecificExist(int v1, int v2, int v3, int expectedVersion)
    {
        // Arrange
        var descriptors = new[]
        {
            CreateDescriptor(TestStepType, v1, CurrentTenant),
            CreateDescriptor(TestStepType, v2, CurrentTenant),
            CreateDescriptor(TestStepType, v3, CurrentTenant)
        };
        RegisterDescriptors(descriptors);

        // Act
        var result = _registry.Find(TestStepType);

        // Assert
        AssertDescriptor(result, CurrentTenant, expectedVersion);
    }

    [Theory]
    [InlineData(1, 2, 3, 3)] // Multiple versions, expect highest
    [InlineData(3, 1, 2, 3)] // Out of order registration
    [InlineData(1, 1, 1, 1)] // Same version multiple times
    public void Find_ReturnsHighestVersionTenantAgnostic_WhenMultipleTenantAgnosticExist(int v1, int v2, int v3, int expectedVersion)
    {
        // Arrange
        var descriptors = new[]
        {
            CreateDescriptor(TestStepType, v1, Tenant.AgnosticTenantId),
            CreateDescriptor(TestStepType, v2, Tenant.AgnosticTenantId),
            CreateDescriptor(TestStepType, v3, Tenant.AgnosticTenantId)
        };
        RegisterDescriptors(descriptors);

        // Act
        var result = _registry.Find(TestStepType);

        // Assert
        AssertDescriptor(result, Tenant.AgnosticTenantId, expectedVersion);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNoMatchingDescriptorsExist()
    {
        // Arrange
        var otherDescriptor = CreateDescriptor("OtherStep", 1, CurrentTenant);
        RegisterDescriptors(otherDescriptor);

        // Act
        var result = _registry.Find("NonExistentStep");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Find_IgnoresOtherTenantDescriptors_OnlyReturnsCurrentTenantOrAgnostic()
    {
        // Arrange
        var descriptors = new[]
        {
            CreateDescriptor(TestStepType, 1, CurrentTenant),
            CreateDescriptor(TestStepType, 5, "tenant2"), // Much higher version but wrong tenant
            CreateDescriptor(TestStepType, 2, Tenant.AgnosticTenantId)
        };
        RegisterDescriptors(descriptors);

        // Act
        var result = _registry.Find(TestStepType);

        // Assert - should return tenant1 descriptor (not tenant2, even though it has higher version)
        AssertDescriptor(result, CurrentTenant, 1);
    }

    [Fact]
    public async Task RefreshDescriptorsAsync_CalledTwice_DoesNotLogWarnings()
    {
        // Arrange
        var mockProvider = Substitute.For<IStepProvider>();
        var descriptor1 = new StepDescriptor
        {
            TypeName = "TestStep1",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Test",
            Description = "Test Step 1",
            IsBrowsable = true
        };
        
        var descriptor2 = new StepDescriptor
        {
            TypeName = "TestStep2",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Test",
            Description = "Test Step 2",
            IsBrowsable = true
        };
        
        mockProvider.GetDescriptorsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IEnumerable<StepDescriptor>>([descriptor1, descriptor2]));

        var providers = new[] { mockProvider };
        
        // Act - First refresh
        await _registry.RefreshDescriptorsAsync(providers);
        
        // Act - Second refresh (simulates the intentional repopulation in DefaultRegistriesPopulator)
        await _registry.RefreshDescriptorsAsync(providers);
        
        // Assert - Verify no warning logs were made
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("was already registered")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
        
        // Verify descriptors are still registered
        var allDescriptors = _registry.ListAll().ToList();
        Assert.Equal(2, allDescriptors.Count);
        Assert.Contains(allDescriptors, d => d.TypeName == "TestStep1");
        Assert.Contains(allDescriptors, d => d.TypeName == "TestStep2");
    }
    
    [Fact]
    public async Task RefreshDescriptorsAsync_PreservesManualDescriptors()
    {
        // Arrange
        // Create a manual descriptor
        var manualDescriptor = new StepDescriptor
        {
            TypeName = "ManualStep",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Manual",
            Description = "Manually registered step",
            IsBrowsable = true
        };
        
        _registry.Register(manualDescriptor);
        
        // Create a provider descriptor
        var mockProvider = Substitute.For<IStepProvider>();
        var providerDescriptor = new StepDescriptor
        {
            TypeName = "ProviderStep",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Provider",
            Description = "Provider step",
            IsBrowsable = true
        };
        
        mockProvider.GetDescriptorsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IEnumerable<StepDescriptor>>([providerDescriptor]));

        var providers = new[] { mockProvider };
        
        // Act - Refresh with provider
        await _registry.RefreshDescriptorsAsync(providers);
        
        // Assert - Both manual and provider descriptors should be present
        var allDescriptors = _registry.ListAll().ToList();
        Assert.Equal(2, allDescriptors.Count);
        Assert.Contains(allDescriptors, d => d.TypeName == "ManualStep");
        Assert.Contains(allDescriptors, d => d.TypeName == "ProviderStep");
        
        // Verify no warnings about manual descriptor being replaced
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("ManualStep")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
    
    [Fact]
    public async Task RefreshDescriptorsAsync_LogsWarning_WhenDifferentProvidersRegisterSameStep()
    {
        // Arrange
        // Create two different providers with the same step
        var descriptor1 = new StepDescriptor
        {
            TypeName = "DuplicateStep",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Test",
            Description = "From Provider 1",
            IsBrowsable = true
        };

        var descriptor2 = new StepDescriptor
        {
            TypeName = "DuplicateStep",
            Version = 1,
            Kind = StepKind.Task,
            Category = "Test",
            Description = "From Provider 2",
            IsBrowsable = true
        };

        var provider1 = new Provider1([descriptor1]);
        var provider2 = new Provider2([descriptor2]);
        var providers = new IStepProvider[] { provider1, provider2 };

        // Act
        await _registry.RefreshDescriptorsAsync(providers);

        // Assert - Should log a warning for the duplicate
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("DuplicateStep") && v.ToString()!.Contains("was already registered")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    
    private StepDescriptor CreateDescriptor(string typeName, int version, string? tenantId) =>
        new()
        {
            TypeName = typeName,
            Version = version,
            TenantId = tenantId,
            Kind = StepKind.Action
        };

    private void RegisterDescriptors(params StepDescriptor[] descriptors)
    {
        foreach (var descriptor in descriptors)
            _registry.Register(descriptor);
    }

    private static void AssertDescriptor(StepDescriptor? result, string? expectedTenantId, int expectedVersion)
    {
        Assert.NotNull(result);
        Assert.Equal(expectedTenantId, result.TenantId);
        Assert.Equal(expectedVersion, result.Version);
    }
    private sealed class Provider1(IEnumerable<StepDescriptor> descriptors) : IStepProvider
    {
        public ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) => new(descriptors);
    }

    private sealed class Provider2(IEnumerable<StepDescriptor> descriptors) : IStepProvider
    {
        public ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) => new(descriptors);
    }
    
}