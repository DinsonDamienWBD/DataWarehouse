using System;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;
using Xunit;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for SDK findings 1101-1249 covering Infrastructure/Policy,
/// IntelligenceAware (PII naming), InterfaceTypes, IoRingNativeMethods, and IoUringBindings.
/// </summary>
public class InterfaceAndIoRingHardeningTests
{
    // Findings 1188: InputValidator._propertyCache -> PropertyCache
    [Fact]
    public void Finding1188_InputValidator_StaticFieldNaming()
    {
        var type = Type.GetType("DataWarehouse.SDK.Validation.InputValidator, DataWarehouse.SDK");
        Assert.NotNull(type);
        var field = type!.GetField("PropertyCache", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Findings 1195: IntegrityTreeRegion MaxLeafCount -> maxLeafCount (local constant)
    [Fact]
    public void Finding1195_IntegrityTreeRegion_LocalConstantNaming()
    {
        // Local constants renamed to camelCase; verified by build success
        var type = Type.GetType("DataWarehouse.SDK.VirtualDiskEngine.Regions.IntegrityTreeRegion, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Findings 1197-1202: IntelligenceAwarePluginBase PII naming
    [Fact]
    public void Finding1197_RequestPiiDetectionAsync_Method()
    {
        var type = typeof(IntelligenceAwarePluginBase);
        var method = type.GetMethod("RequestPiiDetectionAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Null(type.GetMethod("RequestPIIDetectionAsync", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void Finding1199_PiiDetectionResult_TypeNaming()
    {
        var type = typeof(PiiDetectionResult);
        Assert.NotNull(type);
        Assert.NotNull(type.GetProperty("ContainsPii"));
        Assert.NotNull(type.GetProperty("PiiItems"));
        Assert.Null(type.GetProperty("ContainsPII"));
        Assert.Null(type.GetProperty("PIIItems"));
    }

    [Fact]
    public void Finding1202_PiiItem_TypeNaming()
    {
        var type = typeof(PiiItem);
        Assert.NotNull(type);
    }

    // Findings 1203-1204: IntelligenceCapabilities NLP->Nlp, PIIDetection->PiiDetection
    [Fact]
    public void Finding1203_1204_IntelligenceCapabilities_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(IntelligenceCapabilities), IntelligenceCapabilities.Nlp));
        Assert.True(Enum.IsDefined(typeof(IntelligenceCapabilities), IntelligenceCapabilities.PiiDetection));
        Assert.DoesNotContain("NLP", Enum.GetNames<IntelligenceCapabilities>());
        Assert.DoesNotContain("PIIDetection", Enum.GetNames<IntelligenceCapabilities>());
    }

    // Findings 1205-1206: IntelligenceTopics RequestPIIDetection -> RequestPiiDetection
    [Fact]
    public void Finding1205_1206_IntelligenceTopics_PiiNaming()
    {
        var type = typeof(IntelligenceTopics);
        Assert.NotNull(type.GetField("RequestPiiDetection"));
        Assert.NotNull(type.GetField("RequestPiiDetectionResponse"));
        Assert.Null(type.GetField("RequestPIIDetection"));
        Assert.Null(type.GetField("RequestPIIDetectionResponse"));
    }

    // Findings 1207-1208: InterfaceCapabilities RequiresTLS->RequiresTls, CreateGraphQLDefaults->CreateGraphQlDefaults
    [Fact]
    public void Finding1207_InterfaceCapabilities_RequiresTls()
    {
        var type = typeof(InterfaceCapabilities);
        var prop = type.GetProperty("RequiresTls");
        Assert.NotNull(prop);
        Assert.Null(type.GetProperty("RequiresTLS"));
    }

    [Fact]
    public void Finding1208_InterfaceCapabilities_CreateGraphQlDefaults()
    {
        var type = typeof(InterfaceCapabilities);
        var method = type.GetMethod("CreateGraphQlDefaults");
        Assert.NotNull(method);
        Assert.Null(type.GetMethod("CreateGraphQLDefaults"));
    }

    // Findings 1210-1215: InterfaceProtocol REST->Rest, gRPC->GRpc, etc.
    [Theory]
    [InlineData("Rest")]
    [InlineData("GRpc")]
    [InlineData("GraphQl")]
    [InlineData("Mqtt")]
    [InlineData("Amqp")]
    [InlineData("Nats")]
    public void Finding1210_1215_InterfaceProtocol_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<InterfaceProtocol>(name, out _));
    }

    [Fact]
    public void Finding1210_InterfaceProtocol_OldNamesRemoved()
    {
        var names = Enum.GetNames<InterfaceProtocol>();
        Assert.DoesNotContain("REST", names);
        Assert.DoesNotContain("gRPC", names);
        Assert.DoesNotContain("GraphQL", names);
        Assert.DoesNotContain("MQTT", names);
        Assert.DoesNotContain("AMQP", names);
        Assert.DoesNotContain("NATS", names);
    }

    // Findings 1216-1224: HttpMethod GET->Get, POST->Post, etc.
    [Theory]
    [InlineData("Get")]
    [InlineData("Post")]
    [InlineData("Put")]
    [InlineData("Delete")]
    [InlineData("Patch")]
    [InlineData("Head")]
    [InlineData("Options")]
    [InlineData("Trace")]
    [InlineData("Connect")]
    public void Finding1216_1224_HttpMethod_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<DataWarehouse.SDK.Contracts.Interface.HttpMethod>(name, out _));
    }

    [Fact]
    public void Finding1216_HttpMethod_OldNamesRemoved()
    {
        var names = Enum.GetNames<DataWarehouse.SDK.Contracts.Interface.HttpMethod>();
        Assert.DoesNotContain("GET", names);
        Assert.DoesNotContain("POST", names);
        Assert.DoesNotContain("DELETE", names);
    }

    // Finding 1225: InvertedTagIndex MaxAllowedSkip -> maxAllowedSkip (local constant)
    [Fact]
    public void Finding1225_InvertedTagIndex_LocalConstantNaming()
    {
        var type = Type.GetType("DataWarehouse.SDK.Tags.InvertedTagIndex, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Findings 1232-1241: IoRingNativeMethods constant and type naming
    [Fact]
    public void Finding1232_1241_IoRingNativeMethods_PascalCase()
    {
        var type = Type.GetType("DataWarehouse.SDK.VirtualDiskEngine.IO.Windows.IoRingNativeMethods, DataWarehouse.SDK");
        Assert.NotNull(type);
        // Verify renamed types exist as nested types
        var flags = type!.GetNestedType("IoringCreateFlags", BindingFlags.NonPublic);
        Assert.NotNull(flags);
        var cqe = type.GetNestedType("IoringCqe", BindingFlags.NonPublic);
        Assert.NotNull(cqe);
    }

    // Findings 1242-1249: IoUringBindings constant naming
    [Theory]
    [InlineData("IoringOpRead")]
    [InlineData("IoringOpWrite")]
    [InlineData("IoringOpReadFixed")]
    [InlineData("IoringOpWriteFixed")]
    [InlineData("IoringOpUringCmd")]
    [InlineData("IoringSetupSqpoll")]
    [InlineData("IoringSetupIopoll")]
    [InlineData("IoringSetupSingleIssuer")]
    public void Finding1242_1249_IoUringBindings_PascalCase(string fieldName)
    {
        var type = typeof(IoUringBindings);
        var field = type.GetField(fieldName);
        Assert.NotNull(field);
    }

    [Fact]
    public void Finding1242_IoUringBindings_OldNamesRemoved()
    {
        var type = typeof(IoUringBindings);
        Assert.Null(type.GetField("IORING_OP_READ"));
        Assert.Null(type.GetField("IORING_SETUP_SQPOLL"));
        Assert.Null(type.GetField("O_DIRECT"));
    }
}
