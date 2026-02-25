using DataWarehouse.SDK.Storage;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

[Trait("Category", "Unit")]
public class StorageAddressTests
{
    #region FilePathAddress

    [Fact]
    public void FilePathAddress_Kind_ShouldBeFilePath()
    {
        var addr = StorageAddress.FromFilePath("/data/file.txt");
        addr.Kind.Should().Be(StorageAddressKind.FilePath);
    }

    [Fact]
    public void FilePathAddress_ToPath_ShouldReturnPath()
    {
        var addr = StorageAddress.FromFilePath("/data/file.txt");
        addr.ToPath().Should().Be("/data/file.txt");
    }

    [Fact]
    public void FilePathAddress_ToKey_ShouldReturnNormalizedPath()
    {
        var addr = StorageAddress.FromFilePath("/data/file.txt");
        addr.ToKey().Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ObjectKeyAddress

    [Fact]
    public void ObjectKeyAddress_Kind_ShouldBeObjectKey()
    {
        var addr = StorageAddress.FromObjectKey("my-bucket/key");
        addr.Kind.Should().Be(StorageAddressKind.ObjectKey);
    }

    [Fact]
    public void ObjectKeyAddress_ToKey_ShouldReturnKey()
    {
        var addr = StorageAddress.FromObjectKey("my-bucket/key");
        addr.ToKey().Should().Be("my-bucket/key");
    }

    [Fact]
    public void ObjectKeyAddress_ToPath_ShouldThrow()
    {
        var addr = StorageAddress.FromObjectKey("some-key");
        var act = () => addr.ToPath();
        act.Should().Throw<NotSupportedException>();
    }

    #endregion

    #region NvmeNamespaceAddress

    [Fact]
    public void NvmeNamespaceAddress_Kind_ShouldBeNvmeNamespace()
    {
        var addr = StorageAddress.FromNvme(1);
        addr.Kind.Should().Be(StorageAddressKind.NvmeNamespace);
    }

    [Fact]
    public void NvmeNamespaceAddress_WithControllerId_ShouldStoreIt()
    {
        var addr = (NvmeNamespaceAddress)StorageAddress.FromNvme(1, 2);
        addr.NamespaceId.Should().Be(1);
        addr.ControllerId.Should().Be(2);
    }

    [Fact]
    public void NvmeNamespaceAddress_ToKey_ShouldContainNsid()
    {
        var addr = StorageAddress.FromNvme(5);
        addr.ToKey().Should().Contain("5");
    }

    #endregion

    #region BlockDeviceAddress

    [Fact]
    public void BlockDeviceAddress_Kind_ShouldBeBlockDevice()
    {
        var addr = StorageAddress.FromBlockDevice("/dev/sda");
        addr.Kind.Should().Be(StorageAddressKind.BlockDevice);
    }

    [Fact]
    public void BlockDeviceAddress_ToKey_ShouldReturnDevicePath()
    {
        var addr = StorageAddress.FromBlockDevice("/dev/sda");
        addr.ToKey().Should().Be("/dev/sda");
    }

    [Fact]
    public void BlockDeviceAddress_ToPath_ShouldReturnDevicePath()
    {
        var addr = StorageAddress.FromBlockDevice("/dev/sda");
        addr.ToPath().Should().Be("/dev/sda");
    }

    #endregion

    #region NetworkEndpointAddress

    [Fact]
    public void NetworkEndpointAddress_Kind_ShouldBeNetworkEndpoint()
    {
        var addr = StorageAddress.FromNetworkEndpoint("localhost", 8080);
        addr.Kind.Should().Be(StorageAddressKind.NetworkEndpoint);
    }

    [Fact]
    public void NetworkEndpointAddress_ToKey_ShouldContainHostAndPort()
    {
        var addr = StorageAddress.FromNetworkEndpoint("localhost", 8080, "http");
        addr.ToKey().Should().Contain("localhost").And.Contain("8080");
    }

    [Fact]
    public void NetworkEndpointAddress_ToUri_ShouldReturnValidUri()
    {
        var addr = StorageAddress.FromNetworkEndpoint("localhost", 8080, "http");
        var uri = addr.ToUri();
        uri.Host.Should().Be("localhost");
        uri.Port.Should().Be(8080);
    }

    [Fact]
    public void NetworkEndpointAddress_InvalidPort_ShouldThrow()
    {
        var act = () => StorageAddress.FromNetworkEndpoint("localhost", 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NetworkEndpointAddress_PortTooHigh_ShouldThrow()
    {
        var act = () => StorageAddress.FromNetworkEndpoint("localhost", 70000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region GpioPinAddress

    [Fact]
    public void GpioPinAddress_Kind_ShouldBeGpioPin()
    {
        var addr = StorageAddress.FromGpioPin(17);
        addr.Kind.Should().Be(StorageAddressKind.GpioPin);
    }

    [Fact]
    public void GpioPinAddress_ToKey_ShouldContainPinNumber()
    {
        var addr = StorageAddress.FromGpioPin(17, "rpi4");
        addr.ToKey().Should().Contain("17").And.Contain("rpi4");
    }

    [Fact]
    public void GpioPinAddress_NegativePin_ShouldThrow()
    {
        var act = () => StorageAddress.FromGpioPin(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region I2cBusAddress

    [Fact]
    public void I2cBusAddress_Kind_ShouldBeI2cBus()
    {
        var addr = StorageAddress.FromI2cBus(1, 0x48);
        addr.Kind.Should().Be(StorageAddressKind.I2cBus);
    }

    [Fact]
    public void I2cBusAddress_ToKey_ShouldContainHexAddress()
    {
        var addr = StorageAddress.FromI2cBus(1, 0x48);
        addr.ToKey().Should().Contain("48");
    }

    [Fact]
    public void I2cBusAddress_DeviceAddressOutOfRange_ShouldThrow()
    {
        var act = () => StorageAddress.FromI2cBus(1, 128);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void I2cBusAddress_NegativeBus_ShouldThrow()
    {
        var act = () => StorageAddress.FromI2cBus(-1, 0x48);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region SpiBusAddress

    [Fact]
    public void SpiBusAddress_Kind_ShouldBeSpiBus()
    {
        var addr = StorageAddress.FromSpiBus(0, 0);
        addr.Kind.Should().Be(StorageAddressKind.SpiBus);
    }

    [Fact]
    public void SpiBusAddress_ToKey_ShouldContainBusAndCs()
    {
        var addr = StorageAddress.FromSpiBus(1, 2);
        addr.ToKey().Should().Contain("1").And.Contain("2");
    }

    [Fact]
    public void SpiBusAddress_NegativeChipSelect_ShouldThrow()
    {
        var act = () => StorageAddress.FromSpiBus(0, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region CustomAddress

    [Fact]
    public void CustomAddress_Kind_ShouldBeCustomAddress()
    {
        var addr = StorageAddress.FromCustom("myscheme", "myaddress");
        addr.Kind.Should().Be(StorageAddressKind.CustomAddress);
    }

    [Fact]
    public void CustomAddress_ToKey_ShouldContainSchemeAndAddress()
    {
        var addr = StorageAddress.FromCustom("myscheme", "myaddress");
        addr.ToKey().Should().Contain("myscheme").And.Contain("myaddress");
    }

    [Fact]
    public void CustomAddress_NullScheme_ShouldThrow()
    {
        var act = () => StorageAddress.FromCustom(null!, "addr");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CustomAddress_NullAddress_ShouldThrow()
    {
        var act = () => StorageAddress.FromCustom("scheme", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Implicit Conversions

    [Fact]
    public void ImplicitFromString_ObjectKey_ShouldCreateObjectKeyAddress()
    {
        StorageAddress addr = "my-object-key";
        addr.Kind.Should().Be(StorageAddressKind.ObjectKey);
        addr.ToKey().Should().Be("my-object-key");
    }

    [Fact]
    public void ImplicitFromUri_HttpScheme_ShouldCreateNetworkEndpoint()
    {
        StorageAddress addr = new Uri("http://example.com:8080");
        addr.Kind.Should().Be(StorageAddressKind.NetworkEndpoint);
    }

    [Fact]
    public void ImplicitFromUri_FileScheme_ShouldCreateFilePath()
    {
        StorageAddress addr = new Uri("file:///tmp/data.txt");
        addr.Kind.Should().Be(StorageAddressKind.FilePath);
    }

    #endregion

    #region FromUri

    [Fact]
    public void FromUri_HttpScheme_ShouldParseAsNetworkEndpoint()
    {
        var addr = StorageAddress.FromUri(new Uri("http://host:9090"));
        addr.Kind.Should().Be(StorageAddressKind.NetworkEndpoint);
        var net = (NetworkEndpointAddress)addr;
        net.Host.Should().Be("host");
        net.Port.Should().Be(9090);
        net.Scheme.Should().Be("http");
    }

    [Fact]
    public void FromUri_HttpsScheme_ShouldDefaultPort443()
    {
        var addr = StorageAddress.FromUri(new Uri("https://secure.example.com"));
        addr.Kind.Should().Be(StorageAddressKind.NetworkEndpoint);
        var net = (NetworkEndpointAddress)addr;
        net.Port.Should().Be(443);
    }

    [Fact]
    public void FromUri_UnknownScheme_ShouldCreateObjectKey()
    {
        var addr = StorageAddress.FromUri(new Uri("foobar://something"));
        addr.Kind.Should().Be(StorageAddressKind.ObjectKey);
    }

    [Fact]
    public void FromUri_NullUri_ShouldThrow()
    {
        var act = () => StorageAddress.FromUri(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Equality and ToString

    [Fact]
    public void RecordEquality_SameValues_ShouldBeEqual()
    {
        var a = StorageAddress.FromObjectKey("key1");
        var b = StorageAddress.FromObjectKey("key1");
        a.Should().Be(b);
    }

    [Fact]
    public void RecordEquality_DifferentValues_ShouldNotBeEqual()
    {
        var a = StorageAddress.FromObjectKey("key1");
        var b = StorageAddress.FromObjectKey("key2");
        a.Should().NotBe(b);
    }

    [Fact]
    public void GetHashCode_SameValues_ShouldMatch()
    {
        var a = StorageAddress.FromObjectKey("key1");
        var b = StorageAddress.FromObjectKey("key1");
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ShouldContainKindAndKey()
    {
        var addr = StorageAddress.FromObjectKey("my-key");
        var str = addr.ToString();
        str.Should().Contain("ObjectKey");
        str.Should().Contain("my-key");
    }

    #endregion

    #region DwBucket / DwNode / DwCluster

    [Fact]
    public void DwBucketAddress_Kind_ShouldBeDwBucket()
    {
        var addr = StorageAddress.FromDwBucket("mybucket", "path/to/obj");
        addr.Kind.Should().Be(StorageAddressKind.DwBucket);
    }

    [Fact]
    public void DwNodeAddress_Kind_ShouldBeDwNode()
    {
        var addr = StorageAddress.FromDwNode("node1", "path/to/obj");
        addr.Kind.Should().Be(StorageAddressKind.DwNode);
    }

    [Fact]
    public void DwClusterAddress_Kind_ShouldBeDwCluster()
    {
        var addr = StorageAddress.FromDwCluster("mycluster", "distributed-key");
        addr.Kind.Should().Be(StorageAddressKind.DwCluster);
    }

    #endregion

    #region Factory Method Null Checks

    [Fact]
    public void FromFilePath_Null_ShouldThrow()
    {
        var act = () => StorageAddress.FromFilePath(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromObjectKey_Null_ShouldThrow()
    {
        var act = () => StorageAddress.FromObjectKey(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromBlockDevice_Null_ShouldThrow()
    {
        var act = () => StorageAddress.FromBlockDevice(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromNetworkEndpoint_NullHost_ShouldThrow()
    {
        var act = () => StorageAddress.FromNetworkEndpoint(null!, 80);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
