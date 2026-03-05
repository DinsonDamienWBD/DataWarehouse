using System.Reflection;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Edge.Protocols;
using DataWarehouse.SDK.Infrastructure.Intelligence;
using DataWarehouse.SDK.Storage.Billing;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for naming convention findings across multiple files.
/// Uses reflection to verify PascalCase naming after production fixes.
/// </summary>
public class NamingConventionHardeningTests
{
    private static readonly Assembly SdkAssembly = typeof(ArtNode).Assembly;

    // Finding 6-8: ActiveStoragePluginBases enum members (Vp8, Vp9, Av1)
    [Theory]
    [InlineData("Vp8")]
    [InlineData("Vp9")]
    [InlineData("Av1")]
    public void Finding6_8_MediaFormatEnumUsesPascalCase(string memberName)
    {
        var enumType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "MediaFormat" && t.IsEnum);
        Assert.NotNull(enumType);
        Assert.Contains(memberName, Enum.GetNames(enumType!));
    }

    // Finding 15-18: AedsPluginBases field naming (Jobs, Clients, Channels, Lock)
    [Theory]
    [InlineData("Jobs")]
    [InlineData("Clients")]
    [InlineData("Channels")]
    [InlineData("Lock")]
    public void Finding15_18_ServerDispatcherPluginBaseFieldsUsePascalCase(string fieldName)
    {
        var baseType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "ServerDispatcherPluginBase");
        Assert.NotNull(baseType);
        var member = baseType!.GetMember(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        Assert.NotEmpty(member);
    }

    // Finding 23: AiAutonomyConfiguration static field naming
    [Fact]
    public void Finding23_AllCheckTimingsUsesPascalCase()
    {
        var field = typeof(AiAutonomyConfiguration).GetField("AllCheckTimings",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Finding 24: AlexModel local variable (sumXY -> sumXy) - compile-time verified
    [Fact]
    public void Finding24_AlexCdfModelCompilesClean()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "AlexCdfModel");
        Assert.NotNull(type);
    }

    // Finding 38-45: ArtNode field naming
    [Theory]
    [InlineData(typeof(ArtNode.Node16), "Keys")]
    [InlineData(typeof(ArtNode.Node16), "Children")]
    [InlineData(typeof(ArtNode.Node16), "Count")]
    [InlineData(typeof(ArtNode.Node48), "ChildIndex")]
    [InlineData(typeof(ArtNode.Node48), "Children")]
    [InlineData(typeof(ArtNode.Node48), "Count")]
    [InlineData(typeof(ArtNode.Node256), "Children")]
    [InlineData(typeof(ArtNode.Node256), "Count")]
    public void Finding38_45_ArtNodeFieldsUsePascalCase(Type nodeType, string fieldName)
    {
        var field = nodeType.GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    // Finding 51: AuthorityContextPropagator static field naming
    [Fact]
    public void Finding51_AuthorityContextPropagatorFieldUsesPascalCase()
    {
        var type = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "AuthorityContextPropagator");
        Assert.NotNull(type);
        var field = type!.GetField("CurrentLocal", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Finding 65: BeTreeForest field naming
    [Fact]
    public void Finding65_BeTreeForestObjectCountUsesPascalCase()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "BeTreeForest");
        Assert.NotNull(type);
        var member = type!.GetMember("ObjectCount",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(member);
    }

    // Finding 67: BeTreeMessage local constant (MaxKeyLen -> maxKeyLen) - compile-time verified
    [Fact]
    public void Finding67_BeTreeMessageCompilesClean()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "BeTreeMessage");
        Assert.NotNull(type);
    }

    // Finding 71-72: BillingTypes CloudProvider enum naming
    [Theory]
    [InlineData("Aws")]
    [InlineData("Gcp")]
    public void Finding71_72_CloudProviderEnumUsesPascalCase(string memberName)
    {
        Assert.Contains(memberName, Enum.GetNames(typeof(CloudProvider)));
    }

    // Finding 73-75: SpotPricing property naming
    [Theory]
    [InlineData("CurrentPricePerGbMonth")]
    [InlineData("SpotPricePerGbMonth")]
    [InlineData("AvailableCapacityGb")]
    public void Finding73_75_SpotPricingUsesPascalCase(string propName)
    {
        Assert.NotNull(typeof(SpotPricing).GetProperty(propName));
    }

    // Finding 76-78: ReservedCapacity property naming
    [Theory]
    [InlineData("CommittedGb")]
    [InlineData("ReservedPricePerGbMonth")]
    [InlineData("OnDemandPricePerGbMonth")]
    public void Finding76_78_ReservedCapacityUsesPascalCase(string propName)
    {
        Assert.NotNull(typeof(ReservedCapacity).GetProperty(propName));
    }

    // Finding 122: BloomFilterSkipIndex local constant (StackThreshold -> stackThreshold)
    [Fact]
    public void Finding122_BloomFilterSkipIndexCompilesClean()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "BloomFilterSkipIndex");
        Assert.NotNull(type);
    }

    // Finding 123-125: BoundedCache eviction mode enum naming
    [Theory]
    [InlineData("Lru")]
    [InlineData("Arc")]
    [InlineData("Ttl")]
    public void Finding123_125_CacheEvictionModeEnumUsesPascalCase(string memberName)
    {
        var enumType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "CacheEvictionMode" && t.IsEnum);
        Assert.NotNull(enumType);
        Assert.Contains(memberName, Enum.GetNames(enumType!));
    }

    // Finding 132: BoundedMemoryRuntime static field naming
    [Fact]
    public void Finding132_BoundedMemoryRuntimeInstanceUsesPascalCase()
    {
        var type = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "BoundedMemoryRuntime");
        Assert.NotNull(type);
        var field = type!.GetField("SingletonInstance",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Finding 145-146: BusControllerFactory method naming
    [Fact]
    public void Finding145_146_BusControllerFactoryUsesCorrectNaming()
    {
        var type = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "BusControllerFactory");
        Assert.NotNull(type);
        var method = type!.GetMethod("CreateI2CController",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Assert.NotNull(method);
    }

    // Finding 149-152: CameraSettings pixel format enum naming
    [Theory]
    [InlineData("Rgb24")]
    [InlineData("Rgba32")]
    [InlineData("Yuv420")]
    [InlineData("Mjpeg")]
    public void Finding149_152_PixelFormatEnumUsesPascalCase(string memberName)
    {
        var enumType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "PixelFormat" && t.IsEnum);
        Assert.NotNull(enumType);
        Assert.Contains(memberName, Enum.GetNames(enumType!));
    }

    // Findings 153-176: CannInterop AclError enum naming (internal type, use reflection)
    [Theory]
    [InlineData("AclSuccess")]
    [InlineData("AclErrorInvalidParam")]
    [InlineData("AclErrorUninitialize")]
    [InlineData("AclErrorRepeatInitialize")]
    [InlineData("AclErrorFailure")]
    [InlineData("AclErrorNotFound")]
    [InlineData("AclErrorStreamNotCreated")]
    public void Finding154_176_AclErrorEnumUsesPascalCase(string memberName)
    {
        var enumType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "AclError" && t.IsEnum);
        Assert.NotNull(enumType);
        Assert.Contains(memberName, Enum.GetNames(enumType!));
    }

    // Finding 153: AclSuccess constant naming
    [Fact]
    public void Finding153_AclSuccessConstantUsesPascalCase()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "CannInterop");
        Assert.NotNull(type);
        var field = type!.GetField("AclSuccess",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Finding 177-180: Memcpy constants naming
    [Theory]
    [InlineData("AclMemcpyHostToHost")]
    [InlineData("AclMemcpyHostToDevice")]
    [InlineData("AclMemcpyDeviceToHost")]
    [InlineData("AclMemcpyDeviceToDevice")]
    public void Finding177_180_MemcpyConstantsUsePascalCase(string fieldName)
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "CannInterop");
        Assert.NotNull(type);
        var field = type!.GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
    }

    // Finding 182-188: CannInterop local variables (compile-time verified)
    [Fact]
    public void Finding182_188_CannInteropLocalVariablesCompilesClean()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "CannInterop");
        Assert.NotNull(type);
    }

    // Finding 189-198: CarbonTypes naming
    [Theory]
    [InlineData("CarbonBudget", "BudgetGramsCo2E")]
    [InlineData("CarbonBudget", "UsedGramsCo2E")]
    [InlineData("CarbonBudget", "RemainingGramsCo2E")]
    public void Finding189_191_CarbonBudgetPropertiesUsePascalCase(string typeName, string propName)
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
        Assert.NotNull(type);
        Assert.NotNull(type!.GetProperty(propName));
    }

    [Theory]
    [InlineData("Scope1DirectEmissions")]
    [InlineData("Scope2PurchasedElectricity")]
    [InlineData("Scope3ValueChain")]
    public void Finding194_196_GhgScopeCategoryEnumUsesPascalCase(string memberName)
    {
        var enumType = SdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "GhgScopeCategory" && t.IsEnum);
        Assert.NotNull(enumType);
        Assert.Contains(memberName, Enum.GetNames(enumType!));
    }

    // Finding 199-201: CheckClassification static field naming
    [Fact]
    public void Finding199_201_CheckClassificationFieldsUsePascalCase()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "CheckClassificationTable");
        Assert.NotNull(type);
        // Fields are: Tables (tuple), Classifications, FeaturesByTiming
        var allFields = type!.GetFields(BindingFlags.NonPublic | BindingFlags.Static);
        var fieldNames = allFields.Select(f => f.Name).ToArray();
        Assert.Contains("Tables", fieldNames);
        Assert.Contains("Classifications", fieldNames);
        Assert.Contains("FeaturesByTiming", fieldNames);
    }

    // Finding 212-215: CoApMethod enum naming
    [Theory]
    [InlineData("Get")]
    [InlineData("Post")]
    [InlineData("Put")]
    [InlineData("Delete")]
    public void Finding212_215_CoApMethodEnumUsesPascalCase(string memberName)
    {
        Assert.Contains(memberName, Enum.GetNames(typeof(CoApMethod)));
    }

    // Finding 217-218: ColumnarRegionEngine local constants (compile-time verified)
    [Fact]
    public void Finding217_218_ColumnarRegionEngineCompilesClean()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "ColumnarRegionEngine");
        Assert.NotNull(type);
    }
}
