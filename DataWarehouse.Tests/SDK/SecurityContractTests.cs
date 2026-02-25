using DataWarehouse.SDK.Security.Siem;
using DataWarehouse.SDK.Security.SupplyChain;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Tests for security contracts: SiemEvent, SiemSeverity, SbomDocument, SbomComponent, SbomOptions.
/// </summary>
[Trait("Category", "Unit")]
public class SecurityContractTests
{
    #region SiemEvent Construction

    [Fact]
    public void SiemEvent_DefaultValues_ShouldBeSet()
    {
        var evt = new SiemEvent
        {
            Source = "test-source",
            EventType = "TestEvent",
            Description = "A test event"
        };

        evt.EventId.Should().NotBeNullOrEmpty();
        evt.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        evt.Severity.Should().Be(SiemSeverity.Info);
        evt.Metadata.Should().NotBeNull();
    }

    [Fact]
    public void SiemEvent_CustomSeverity_ShouldBeStored()
    {
        var evt = new SiemEvent
        {
            Source = "auth",
            EventType = "AuthFailure",
            Description = "Failed login",
            Severity = SiemSeverity.High
        };

        evt.Severity.Should().Be(SiemSeverity.High);
    }

    [Fact]
    public void SiemEvent_WithMetadata_ShouldStoreAll()
    {
        var evt = new SiemEvent
        {
            Source = "firewall",
            EventType = "ConnectionBlocked",
            Description = "Blocked IP",
            Metadata = new Dictionary<string, string>
            {
                ["sourceIp"] = "10.0.0.1",
                ["destPort"] = "443"
            }
        };

        evt.Metadata.Should().HaveCount(2);
        evt.Metadata["sourceIp"].Should().Be("10.0.0.1");
    }

    #endregion

    #region SiemSeverity Mapping

    [Theory]
    [InlineData(SiemSeverity.Info, 6)]
    [InlineData(SiemSeverity.Low, 5)]
    [InlineData(SiemSeverity.Medium, 4)]
    [InlineData(SiemSeverity.High, 3)]
    [InlineData(SiemSeverity.Critical, 2)]
    public void SiemSeverity_ShouldMapToSyslogValues(SiemSeverity severity, int expectedValue)
    {
        ((int)severity).Should().Be(expectedValue);
    }

    #endregion

    #region SiemEvent CEF Format

    [Fact]
    public void ToCef_ShouldStartWithCefHeader()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "TestType",
            Description = "Test description"
        };

        var cef = evt.ToCef();
        cef.Should().StartWith("CEF:0|DataWarehouse|SecurityMonitor|1.0|");
    }

    [Fact]
    public void ToCef_CriticalSeverity_ShouldMapTo10()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Critical",
            Description = "Critical event",
            Severity = SiemSeverity.Critical
        };

        var cef = evt.ToCef();
        // CEF severity for Critical should be 10
        cef.Should().Contain("|10|");
    }

    [Fact]
    public void ToCef_InfoSeverity_ShouldMapTo1()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Info",
            Description = "Info event",
            Severity = SiemSeverity.Info
        };

        var cef = evt.ToCef();
        cef.Should().Contain("|1|");
    }

    [Fact]
    public void ToCef_WithMetadata_ShouldIncludeExtensions()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Test",
            Description = "Test desc",
            Metadata = new Dictionary<string, string> { ["userId"] = "admin" }
        };

        var cef = evt.ToCef();
        cef.Should().Contain("userId=admin");
    }

    [Fact]
    public void ToCef_ShouldEscapePipeCharacters()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Type|With|Pipes",
            Description = "Desc"
        };

        var cef = evt.ToCef();
        cef.Should().Contain("Type\\|With\\|Pipes");
    }

    #endregion

    #region SiemEvent Syslog Format

    [Fact]
    public void ToSyslog_ShouldContainPriority()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Test",
            Description = "Test event",
            Severity = SiemSeverity.Info
        };

        var syslog = evt.ToSyslog();
        // Priority = facility(4) * 8 + severity(6) = 38
        syslog.Should().StartWith("<38>");
    }

    [Fact]
    public void ToSyslog_ShouldContainEventId()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Test",
            Description = "Test event"
        };

        var syslog = evt.ToSyslog();
        syslog.Should().Contain(evt.EventId);
    }

    [Fact]
    public void ToSyslog_ShouldContainStructuredData()
    {
        var evt = new SiemEvent
        {
            Source = "auth-service",
            EventType = "LoginFail",
            Description = "Failed login attempt"
        };

        var syslog = evt.ToSyslog();
        syslog.Should().Contain("[dw@0");
        syslog.Should().Contain("source=\"auth-service\"");
        syslog.Should().Contain("eventType=\"LoginFail\"");
    }

    [Fact]
    public void ToSyslog_CustomHostname_ShouldBeUsed()
    {
        var evt = new SiemEvent
        {
            Source = "test",
            EventType = "Test",
            Description = "Test"
        };

        var syslog = evt.ToSyslog("custom-host");
        syslog.Should().Contain("custom-host");
    }

    #endregion

    #region SbomDocument

    [Fact]
    public void SbomDocument_DefaultValues_ShouldBeSet()
    {
        var doc = new SbomDocument();
        doc.Content.Should().BeEmpty();
        doc.ComponentCount.Should().Be(0);
        doc.VulnerabilityCount.Should().Be(0);
        doc.Components.Should().BeEmpty();
    }

    [Fact]
    public void SbomDocument_WithComponents_ShouldStoreAll()
    {
        var doc = new SbomDocument
        {
            Format = SbomFormat.CycloneDX_1_5_Json,
            Content = "{\"bomFormat\":\"CycloneDX\"}",
            ComponentCount = 2,
            GeneratedAt = DateTimeOffset.UtcNow,
            Components = new[]
            {
                new SbomComponent { Name = "xunit", Version = "3.0" },
                new SbomComponent { Name = "FluentAssertions", Version = "8.0" }
            }
        };

        doc.Format.Should().Be(SbomFormat.CycloneDX_1_5_Json);
        doc.Components.Should().HaveCount(2);
        doc.Components[0].Name.Should().Be("xunit");
    }

    #endregion

    #region SbomComponent

    [Fact]
    public void SbomComponent_DefaultType_ShouldBeLibrary()
    {
        var comp = new SbomComponent();
        comp.ComponentType.Should().Be("library");
    }

    [Fact]
    public void SbomComponent_WithAllFields_ShouldStoreAll()
    {
        var comp = new SbomComponent
        {
            Name = "Newtonsoft.Json",
            Version = "13.0.1",
            ComponentType = "library",
            PackageUrl = "pkg:nuget/Newtonsoft.Json@13.0.1",
            Sha256Hash = "abc123def456"
        };

        comp.Name.Should().Be("Newtonsoft.Json");
        comp.Version.Should().Be("13.0.1");
        comp.PackageUrl.Should().Contain("nuget");
        comp.Sha256Hash.Should().Be("abc123def456");
    }

    #endregion

    #region SbomOptions

    [Fact]
    public void SbomOptions_DefaultValues()
    {
        var opts = new SbomOptions();
        opts.IncludeTransitive.Should().BeTrue();
        opts.IncludeHashes.Should().BeTrue();
        opts.IncludeLicenses.Should().BeTrue();
        opts.CreatorTool.Should().Contain("DataWarehouse");
        opts.NamespaceFilter.Should().BeNull();
    }

    [Fact]
    public void SbomOptions_CustomValues()
    {
        var opts = new SbomOptions
        {
            IncludeTransitive = false,
            IncludeHashes = false,
            NamespaceFilter = new[] { "DataWarehouse" }
        };

        opts.IncludeTransitive.Should().BeFalse();
        opts.IncludeHashes.Should().BeFalse();
        opts.NamespaceFilter.Should().HaveCount(1);
    }

    #endregion

    #region SbomFormat Enum

    [Theory]
    [InlineData(SbomFormat.CycloneDX_1_5_Json)]
    [InlineData(SbomFormat.CycloneDX_1_5_Xml)]
    [InlineData(SbomFormat.SPDX_2_3_Json)]
    [InlineData(SbomFormat.SPDX_2_3_TagValue)]
    public void SbomFormat_AllValues_ShouldBeDefined(SbomFormat format)
    {
        Enum.IsDefined(format).Should().BeTrue();
    }

    #endregion

    #region SiemTransportOptions

    [Fact]
    public void SiemTransportOptions_DefaultValues()
    {
        var opts = new SiemTransportOptions();
        opts.BatchSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SiemTransportOptions_Endpoint_ShouldBeConfigurable()
    {
        var opts = new SiemTransportOptions
        {
            Endpoint = new Uri("https://splunk:8088/services/collector"),
            AuthToken = "test-token"
        };

        opts.Endpoint.Host.Should().Be("splunk");
        opts.AuthToken.Should().Be("test-token");
    }

    #endregion
}
