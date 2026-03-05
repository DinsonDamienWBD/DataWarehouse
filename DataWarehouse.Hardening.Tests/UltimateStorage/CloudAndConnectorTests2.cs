// Hardening tests for UltimateStorage Cloud, Connector, Enterprise, S3Compatible findings 280-284, 360-377, 441-445, 496-500
// GcsArchive, GcsStrategy, GraphQlConnector, GrpcConnector, GrpcStorage,
// HpeStoreOnce, IbmCos, LinodeObjectStorage, Manila, Memcached, MinioStrategy

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class CloudAndConnectorTests2
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string CloudDir = Path.Combine(PluginRoot, "Strategies", "Cloud");
    private static readonly string ConnDir = Path.Combine(PluginRoot, "Strategies", "Connectors");
    private static readonly string SpecializedDir = Path.Combine(PluginRoot, "Strategies", "Specialized");
    private static readonly string S3Dir = Path.Combine(PluginRoot, "Strategies", "S3Compatible");
    private static readonly string OpenStackDir = Path.Combine(PluginRoot, "Strategies", "OpenStack");

    // === GcsArchiveStrategy (finding 280) ===

    /// <summary>
    /// Finding 280 (LOW): _timeoutSeconds exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding280_GcsArchive_TimeoutExposed()
    {
        var code = File.ReadAllText(Path.Combine(PluginRoot, "Strategies", "Archive", "GcsArchiveStrategy.cs"));
        Assert.Contains("internal int TimeoutSeconds", code);
    }

    // === GcsStrategy (findings 282-283) ===

    /// <summary>
    /// Finding 282 (LOW): _enableRequesterPays exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding282_Gcs_RequesterPaysExposed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "GcsStrategy.cs"));
        Assert.Contains("internal bool EnableRequesterPays", code);
    }

    // === GraphQlConnectorStrategy (findings 360-361) ===

    /// <summary>
    /// Finding 360 (MEDIUM): MethodHasAsyncOverload - use DisposeAsync.
    /// </summary>
    [Fact]
    public void Finding360_GraphQl_DisposeAsync()
    {
        var code = File.ReadAllText(Path.Combine(ConnDir, "GraphQlConnectorStrategy.cs"));
        Assert.Contains("await stream.DisposeAsync()", code);
    }

    /// <summary>
    /// Finding 361 (LOW): ParseGraphQLKey renamed to ParseGraphQlKey.
    /// </summary>
    [Fact]
    public void Finding361_GraphQl_MethodRenamed()
    {
        var code = File.ReadAllText(Path.Combine(ConnDir, "GraphQlConnectorStrategy.cs"));
        Assert.Contains("ParseGraphQlKey", code);
        Assert.DoesNotContain("ParseGraphQLKey", code);
    }

    // === GrpcConnectorStrategy (findings 362-363) ===

    /// <summary>
    /// Finding 362 (LOW): _authToken exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding362_GrpcConnector_AuthTokenExposed()
    {
        var code = File.ReadAllText(Path.Combine(ConnDir, "GrpcConnectorStrategy.cs"));
        Assert.Contains("internal string? AuthToken", code);
    }

    // === GrpcStorageStrategy (findings 364-366) ===

    /// <summary>
    /// Finding 364 (LOW): _loadBalancingPolicy exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding364_GrpcStorage_LoadBalancingPolicyExposed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "GrpcStorageStrategy.cs"));
        Assert.Contains("internal string LoadBalancingPolicy", code);
    }

    // === IbmCosStrategy (findings 372-375) ===

    /// <summary>
    /// Finding 372 (LOW): _enableAsperaSupport exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding372_IbmCos_AsperaSupportExposed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "IbmCosStrategy.cs"));
        Assert.Contains("internal bool EnableAsperaSupport", code);
    }

    /// <summary>
    /// Finding 374 (LOW): uploadedSize initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding374_IbmCos_UploadedSize_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "IbmCosStrategy.cs"));
        Assert.Contains("long uploadedSize;", code);
        Assert.DoesNotContain("long uploadedSize = 0;", code);
    }

    /// <summary>
    /// Finding 375 (LOW): key_meta renamed to keyMeta.
    /// </summary>
    [Fact]
    public void Finding375_IbmCos_KeyMeta_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "IbmCosStrategy.cs"));
        Assert.Contains("keyMeta", code);
        Assert.DoesNotContain("key_meta", code);
    }

    // === LinodeObjectStorageStrategy (findings 441-445) ===

    /// <summary>
    /// Finding 441 (LOW): _enableCors exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding441_Linode_CorsExposed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "LinodeObjectStorageStrategy.cs"));
        Assert.Contains("internal bool EnableCors", code);
    }

    /// <summary>
    /// Finding 443 (LOW): s3ex renamed to s3Ex.
    /// </summary>
    [Fact]
    public void Finding443_Linode_S3ExRenamed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "LinodeObjectStorageStrategy.cs"));
        Assert.Contains("s3Ex", code);
    }

    /// <summary>
    /// Findings 444-445 (MEDIUM): PossibleMultipleEnumeration fixed by materializing.
    /// </summary>
    [Fact]
    public void Findings444to445_Linode_MultipleEnumeration_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "LinodeObjectStorageStrategy.cs"));
        Assert.Contains("rulesList", code);
        Assert.DoesNotContain("rules.Any()", code);
    }

    // === MemcachedStrategy (findings 497-499) ===

    /// <summary>
    /// Finding 497 (LOW): _indexKeyPrefix exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding497_Memcached_IndexKeyPrefixExposed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "MemcachedStrategy.cs"));
        Assert.Contains("internal string IndexKeyPrefix", code);
    }

    /// <summary>
    /// Finding 498 (LOW): Meta assignment not used - fixed.
    /// </summary>
    [Fact]
    public void Finding498_Memcached_MetaAssignment_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "MemcachedStrategy.cs"));
        Assert.Contains("StorageObjectMetadata meta;", code);
        Assert.DoesNotContain("StorageObjectMetadata? meta = null;", code);
    }

    // === MinioStrategy (finding 500) ===

    /// <summary>
    /// Finding 500 (LOW): _useSSL renamed to _useSsl.
    /// </summary>
    [Fact]
    public void Finding500_Minio_UseSsl_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "MinioStrategy.cs"));
        Assert.Contains("_useSsl", code);
        Assert.DoesNotContain("_useSSL", code);
    }
}
