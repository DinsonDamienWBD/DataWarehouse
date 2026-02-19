using System.Collections.ObjectModel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.CrossMoonshot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Moonshots;

/// <summary>
/// Tests for cross-moonshot wiring: event propagation between moonshot pairs
/// and the registrar's conditional activation based on feature flags.
/// </summary>
public sealed class CrossMoonshotWiringTests
{
    private readonly MoonshotConfiguration _config = MoonshotConfigurationDefaults.CreateProductionDefaults();

    /// <summary>
    /// Creates a mock message bus that captures Subscribe and PublishAsync calls.
    /// Allows test code to invoke registered handlers to simulate incoming events.
    /// </summary>
    private static (Mock<IMessageBus> BusMock, Dictionary<string, Func<PluginMessage, Task>> Handlers, List<(string Topic, PluginMessage Message)> Published)
        CreateCapturingMessageBus()
    {
        var busMock = new Mock<IMessageBus>();
        var handlers = new Dictionary<string, Func<PluginMessage, Task>>();
        var published = new List<(string Topic, PluginMessage Message)>();

        busMock.Setup(b => b.Subscribe(It.IsAny<string>(), It.IsAny<Func<PluginMessage, Task>>()))
            .Returns<string, Func<PluginMessage, Task>>((topic, handler) =>
            {
                handlers[topic] = handler;
                return Mock.Of<IDisposable>();
            });

        busMock.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), It.IsAny<CancellationToken>()))
            .Returns<string, PluginMessage, CancellationToken>((topic, msg, _) =>
            {
                published.Add((topic, msg));
                return Task.CompletedTask;
            });

        // Overload without CancellationToken
        busMock.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), default))
            .Returns<string, PluginMessage, CancellationToken>((topic, msg, _) =>
            {
                published.Add((topic, msg));
                return Task.CompletedTask;
            });

        return (busMock, handlers, published);
    }

    [Fact]
    public async Task TagConsciousnessWiring_ScoreCompleted_AttachesTags()
    {
        // Arrange
        var (busMock, handlers, published) = CreateCapturingMessageBus();
        var wiring = new TagConsciousnessWiring(
            busMock.Object, _config, NullLogger<TagConsciousnessWiring>.Instance);

        await wiring.RegisterAsync(CancellationToken.None);

        // Simulate consciousness.score.completed event
        Assert.True(handlers.ContainsKey("consciousness.score.completed"),
            "TagConsciousnessWiring should subscribe to consciousness.score.completed");

        var scoreMessage = new PluginMessage
        {
            Type = "consciousness.score.completed",
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = "obj-001",
                ["score"] = 85.0,
                ["liability"] = 15.0
            }
        };

        // Act
        await handlers["consciousness.score.completed"](scoreMessage);

        // Assert: tags.system.attach was published with consciousness tags
        Assert.Contains(published, p => p.Topic == "tags.system.attach");
        var tagMsg = published.First(p => p.Topic == "tags.system.attach");
        Assert.Equal("obj-001", tagMsg.Message.Payload["objectId"]?.ToString());
        var tags = tagMsg.Message.Payload["tags"] as Dictionary<string, string>;
        Assert.NotNull(tags);
        Assert.Equal("85.0", tags["dw:consciousness:value"]);
        Assert.Equal("Critical", tags["dw:consciousness:grade"]); // score 85 >= 80
    }

    [Fact]
    public async Task ComplianceSovereigntyWiring_ZoneCheckCompleted_AddsEvidence()
    {
        // Arrange
        var (busMock, handlers, published) = CreateCapturingMessageBus();
        var wiring = new ComplianceSovereigntyWiring(
            busMock.Object, _config, NullLogger<ComplianceSovereigntyWiring>.Instance);

        await wiring.RegisterAsync(CancellationToken.None);
        Assert.True(handlers.ContainsKey("sovereignty.zone.check.completed"));

        var zoneCheckMessage = new PluginMessage
        {
            Type = "sovereignty.zone.check.completed",
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = "obj-002",
                ["zoneId"] = "eu-west-1",
                ["decision"] = "Allowed"
            }
        };

        // Act
        await handlers["sovereignty.zone.check.completed"](zoneCheckMessage);

        // Assert: compliance.passport.add-evidence was published
        Assert.Contains(published, p => p.Topic == "compliance.passport.add-evidence");
        var evidenceMsg = published.First(p => p.Topic == "compliance.passport.add-evidence");
        Assert.Equal("obj-002", evidenceMsg.Message.Payload["objectId"]?.ToString());
        Assert.Equal("SovereigntyZoneCheck", evidenceMsg.Message.Payload["evidenceType"]?.ToString());
        Assert.Equal("eu-west-1", evidenceMsg.Message.Payload["zoneId"]?.ToString());
    }

    [Fact]
    public async Task PlacementCarbonWiring_IntensityChanged_TriggersRecalculation()
    {
        // Arrange
        var (busMock, handlers, published) = CreateCapturingMessageBus();
        var wiring = new PlacementCarbonWiring(
            busMock.Object, _config, NullLogger<PlacementCarbonWiring>.Instance);

        await wiring.RegisterAsync(CancellationToken.None);
        Assert.True(handlers.ContainsKey("carbon.intensity.updated"));

        // Simulate >20% intensity change (from 100 to 125 = 25% delta)
        var intensityMessage = new PluginMessage
        {
            Type = "carbon.intensity.updated",
            Payload = new Dictionary<string, object>
            {
                ["previousIntensity"] = 100.0,
                ["currentIntensity"] = 125.0,
                ["storageClass"] = "standard"
            }
        };

        // Act
        await handlers["carbon.intensity.updated"](intensityMessage);

        // Assert: storage.placement.recalculate-batch was published
        Assert.Contains(published, p => p.Topic == "storage.placement.recalculate-batch");
        var recalcMsg = published.First(p => p.Topic == "storage.placement.recalculate-batch");
        Assert.Equal("CarbonIntensityChange", recalcMsg.Message.Payload["reason"]?.ToString());
    }

    [Fact]
    public async Task TimeLockComplianceWiring_PassportIssued_AppliesLock()
    {
        // Arrange
        var (busMock, handlers, published) = CreateCapturingMessageBus();
        var wiring = new TimeLockComplianceWiring(
            busMock.Object, _config, NullLogger<TimeLockComplianceWiring>.Instance);

        await wiring.RegisterAsync(CancellationToken.None);
        Assert.True(handlers.ContainsKey("compliance.passport.issued"));

        // SOX regulation requires 7-year retention
        var passportMessage = new PluginMessage
        {
            Type = "compliance.passport.issued",
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = "obj-003",
                ["regulation"] = "SOX"
            }
        };

        // Act
        await handlers["compliance.passport.issued"](passportMessage);

        // Assert: tamperproof.timelock.apply was published with 7-year duration
        Assert.Contains(published, p => p.Topic == "tamperproof.timelock.apply");
        var lockMsg = published.First(p => p.Topic == "tamperproof.timelock.apply");
        Assert.Equal("obj-003", lockMsg.Message.Payload["objectId"]?.ToString());

        // SOX = 7 years in seconds
        var expectedSeconds = TimeSpan.FromDays(7 * 365).TotalSeconds;
        Assert.Equal(expectedSeconds, (double)lockMsg.Message.Payload["duration"]);
        Assert.Equal("SOX", lockMsg.Message.Payload["regulation"]?.ToString());
        Assert.True((bool)lockMsg.Message.Payload["requiresImmutability"]);
    }

    [Fact]
    public async Task FabricPlacementWiring_PlacementCompleted_RegistersAddress()
    {
        // Arrange
        var (busMock, handlers, published) = CreateCapturingMessageBus();
        var wiring = new FabricPlacementWiring(
            busMock.Object, _config, NullLogger<FabricPlacementWiring>.Instance);

        await wiring.RegisterAsync(CancellationToken.None);
        Assert.True(handlers.ContainsKey("storage.placement.completed"));

        var placementMessage = new PluginMessage
        {
            Type = "storage.placement.completed",
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = "obj-1",
                ["nodeId"] = "node-1"
            }
        };

        // Act
        await handlers["storage.placement.completed"](placementMessage);

        // Assert: fabric.namespace.register was published with dw:// address
        Assert.Contains(published, p => p.Topic == "fabric.namespace.register");
        var fabricMsg = published.First(p => p.Topic == "fabric.namespace.register");
        Assert.Equal("obj-1", fabricMsg.Message.Payload["objectId"]?.ToString());
        Assert.Equal("dw://node@node-1/obj-1", fabricMsg.Message.Payload["dwAddress"]?.ToString());
    }

    [Fact]
    public async Task CrossMoonshotRegistrar_DisabledMoonshot_SkipsWiring()
    {
        // Arrange: config with CarbonAwareLifecycle disabled
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        foreach (var kvp in _config.Moonshots)
        {
            if (kvp.Key == MoonshotId.CarbonAwareLifecycle)
                moonshots[kvp.Key] = kvp.Value with { Enabled = false };
            else
                moonshots[kvp.Key] = kvp.Value;
        }

        var disabledConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance
        };

        var busMock = new Mock<IMessageBus>();
        busMock.Setup(b => b.Subscribe(It.IsAny<string>(), It.IsAny<Func<PluginMessage, Task>>()))
            .Returns(Mock.Of<IDisposable>());

        var registrar = new CrossMoonshotWiringRegistrar(
            busMock.Object, disabledConfig, NullLoggerFactory.Instance);

        // Act
        await registrar.RegisterAllAsync(CancellationToken.None);

        // Assert: PlacementCarbonWiring should NOT be registered (CarbonAwareLifecycle is disabled)
        var active = registrar.GetActiveWirings();
        Assert.DoesNotContain("PlacementCarbonWiring", active);
    }

    [Fact]
    public async Task CrossMoonshotRegistrar_AllEnabled_RegistersAllWirings()
    {
        // Arrange: production defaults with all moonshots enabled
        var busMock = new Mock<IMessageBus>();
        busMock.Setup(b => b.Subscribe(It.IsAny<string>(), It.IsAny<Func<PluginMessage, Task>>()))
            .Returns(Mock.Of<IDisposable>());

        var registrar = new CrossMoonshotWiringRegistrar(
            busMock.Object, _config, NullLoggerFactory.Instance);

        // Act
        await registrar.RegisterAllAsync(CancellationToken.None);

        // Assert: all 7 wirings should be registered
        var active = registrar.GetActiveWirings();
        Assert.Equal(7, active.Count);
        Assert.Contains("TagConsciousnessWiring", active);
        Assert.Contains("ComplianceSovereigntyWiring", active);
        Assert.Contains("PlacementCarbonWiring", active);
        Assert.Contains("SyncConsciousnessWiring", active);
        Assert.Contains("TimeLockComplianceWiring", active);
        Assert.Contains("ChaosImmunityWiring", active);
        Assert.Contains("FabricPlacementWiring", active);
    }
}
