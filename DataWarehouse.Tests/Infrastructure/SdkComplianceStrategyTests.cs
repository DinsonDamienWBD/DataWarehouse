using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Compliance;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK compliance strategy infrastructure types:
    /// IComplianceStrategy, ComplianceRequirements, ComplianceAssessmentResult,
    /// ComplianceViolation, ComplianceControl, ComplianceStatistics, and ComplianceCapabilities.
    /// </summary>
    public class SdkComplianceStrategyTests
    {
        #region IComplianceStrategy Interface Tests

        [Fact]
        public void IComplianceStrategy_DefinesAssessAsyncMethod()
        {
            var method = typeof(IComplianceStrategy).GetMethod("AssessAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<ComplianceAssessmentResult>), method!.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(ComplianceFramework), parameters[0].ParameterType);
        }

        [Fact]
        public void IComplianceStrategy_DefinesAssessControlAsyncMethod()
        {
            var method = typeof(IComplianceStrategy).GetMethod("AssessControlAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<bool>), method!.ReturnType);
        }

        [Fact]
        public void IComplianceStrategy_DefinesCollectEvidenceAsyncMethod()
        {
            var method = typeof(IComplianceStrategy).GetMethod("CollectEvidenceAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void IComplianceStrategy_DefinesGetRequirementsMethod()
        {
            var method = typeof(IComplianceStrategy).GetMethod("GetRequirements");
            Assert.NotNull(method);
            Assert.Equal(typeof(ComplianceRequirements), method!.ReturnType);
        }

        [Fact]
        public void IComplianceStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(IComplianceStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IComplianceStrategy_DefinesSupportedFrameworksProperty()
        {
            var prop = typeof(IComplianceStrategy).GetProperty("SupportedFrameworks");
            Assert.NotNull(prop);
        }

        #endregion

        #region ComplianceFramework Enum Tests

        [Fact]
        public void ComplianceFramework_Has8Frameworks()
        {
            var values = Enum.GetValues<ComplianceFramework>();
            Assert.Equal(8, values.Length);
        }

        [Theory]
        [InlineData(ComplianceFramework.GDPR, 0)]
        [InlineData(ComplianceFramework.HIPAA, 1)]
        [InlineData(ComplianceFramework.SOX, 2)]
        [InlineData(ComplianceFramework.PCIDSS, 3)]
        [InlineData(ComplianceFramework.FedRAMP, 4)]
        [InlineData(ComplianceFramework.SOC2, 5)]
        [InlineData(ComplianceFramework.ISO27001, 6)]
        [InlineData(ComplianceFramework.CCPA, 7)]
        public void ComplianceFramework_HasCorrectValues(ComplianceFramework framework, int expected)
        {
            Assert.Equal(expected, (int)framework);
        }

        #endregion

        #region ComplianceRequirements Tests

        [Fact]
        public void ComplianceRequirements_Construction_SetsRequiredProperties()
        {
            var controls = new List<ComplianceControl>
            {
                new ComplianceControl
                {
                    ControlId = "GDPR-Art.32",
                    Description = "Security of processing",
                    Category = ComplianceControlCategory.Encryption,
                    Severity = ComplianceSeverity.High,
                    IsAutomated = true,
                    Framework = ComplianceFramework.GDPR
                }
            };

            var requirements = new ComplianceRequirements
            {
                Framework = ComplianceFramework.GDPR,
                Controls = controls
            };

            Assert.Equal(ComplianceFramework.GDPR, requirements.Framework);
            Assert.Single(requirements.Controls);
            Assert.Equal("GDPR-Art.32", requirements.Controls[0].ControlId);
        }

        [Fact]
        public void ComplianceRequirements_OptionalFields_DefaultToNull()
        {
            var requirements = new ComplianceRequirements
            {
                Framework = ComplianceFramework.HIPAA,
                Controls = new List<ComplianceControl>()
            };

            Assert.Null(requirements.ResidencyRequirements);
            Assert.Null(requirements.RetentionRequirements);
            Assert.Null(requirements.EncryptionRequirements);
            Assert.Null(requirements.AuditLogRetention);
            Assert.Null(requirements.CertificationValidity);
        }

        #endregion

        #region ComplianceAssessmentResult Tests

        [Fact]
        public void ComplianceAssessmentResult_Construction_SetsRequiredProperties()
        {
            var result = new ComplianceAssessmentResult
            {
                Framework = ComplianceFramework.SOC2,
                IsCompliant = true,
                ComplianceScore = 0.95,
                Violations = new List<ComplianceViolation>()
            };

            Assert.Equal(ComplianceFramework.SOC2, result.Framework);
            Assert.True(result.IsCompliant);
            Assert.Equal(0.95, result.ComplianceScore);
            Assert.Empty(result.Violations);
        }

        [Fact]
        public void ComplianceAssessmentResult_GetViolationsBySeverity_GroupsCorrectly()
        {
            var violations = new List<ComplianceViolation>
            {
                new ComplianceViolation
                {
                    ControlId = "ctrl-1",
                    Severity = ComplianceSeverity.High,
                    Details = "Encryption not enabled",
                    RemediationSteps = new List<string> { "Enable encryption" }
                },
                new ComplianceViolation
                {
                    ControlId = "ctrl-2",
                    Severity = ComplianceSeverity.High,
                    Details = "Audit logging disabled",
                    RemediationSteps = new List<string> { "Enable audit logging" }
                },
                new ComplianceViolation
                {
                    ControlId = "ctrl-3",
                    Severity = ComplianceSeverity.Low,
                    Details = "Password policy too lenient",
                    RemediationSteps = new List<string> { "Update password policy" }
                }
            };

            var result = new ComplianceAssessmentResult
            {
                Framework = ComplianceFramework.HIPAA,
                IsCompliant = false,
                ComplianceScore = 0.6,
                Violations = violations
            };

            var bySeverity = result.GetViolationsBySeverity();
            Assert.Equal(2, bySeverity[ComplianceSeverity.High]);
            Assert.Equal(1, bySeverity[ComplianceSeverity.Low]);
        }

        #endregion

        #region ComplianceViolation Tests

        [Fact]
        public void ComplianceViolation_Construction_SetsRequiredProperties()
        {
            var violation = new ComplianceViolation
            {
                ControlId = "HIPAA-164.312(a)(1)",
                Severity = ComplianceSeverity.Critical,
                Details = "Access control violation",
                RemediationSteps = new List<string>
                {
                    "Review access control lists",
                    "Implement least privilege"
                }
            };

            Assert.Equal("HIPAA-164.312(a)(1)", violation.ControlId);
            Assert.Equal(ComplianceSeverity.Critical, violation.Severity);
            Assert.Equal("Access control violation", violation.Details);
            Assert.Equal(2, violation.RemediationSteps.Count);
        }

        [Fact]
        public void ComplianceViolation_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = new ComplianceViolation
            {
                ControlId = "PCI-DSS-3.4",
                Severity = ComplianceSeverity.High,
                Details = "PAN data not encrypted",
                Resource = "database/cards",
                RemediationSteps = new List<string> { "Encrypt PAN data" }
            };

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<ComplianceViolation>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.ControlId, deserialized!.ControlId);
            Assert.Equal(original.Severity, deserialized.Severity);
            Assert.Equal(original.Details, deserialized.Details);
            Assert.Equal(original.Resource, deserialized.Resource);
        }

        #endregion

        #region ComplianceControl Tests

        [Fact]
        public void ComplianceControl_Construction_SetsAllRequiredFields()
        {
            var control = new ComplianceControl
            {
                ControlId = "SOC2-CC6.1",
                Description = "Logical and physical access controls",
                Category = ComplianceControlCategory.AccessControl,
                Severity = ComplianceSeverity.High,
                IsAutomated = true,
                Framework = ComplianceFramework.SOC2,
                RemediationGuidance = "Implement MFA for all access"
            };

            Assert.Equal("SOC2-CC6.1", control.ControlId);
            Assert.Equal(ComplianceControlCategory.AccessControl, control.Category);
            Assert.True(control.IsAutomated);
            Assert.Equal(ComplianceFramework.SOC2, control.Framework);
            Assert.Equal("Implement MFA for all access", control.RemediationGuidance);
        }

        [Fact]
        public void ComplianceSeverity_Has5Levels()
        {
            var values = Enum.GetValues<ComplianceSeverity>();
            Assert.Equal(5, values.Length);
        }

        [Fact]
        public void ComplianceControlCategory_Has10Categories()
        {
            var values = Enum.GetValues<ComplianceControlCategory>();
            Assert.Equal(10, values.Length);
        }

        #endregion

        #region ComplianceStatistics Tests

        [Fact]
        public void ComplianceStatistics_Empty_HasZeroCounts()
        {
            var stats = ComplianceStatistics.Empty;

            Assert.Equal(0, stats.TotalAssessments);
            Assert.Equal(0, stats.CompliantCount);
            Assert.Equal(0, stats.NonCompliantCount);
            Assert.Equal(0, stats.TotalViolations);
            Assert.Equal(0, stats.ErrorCount);
            Assert.Equal(0, stats.ComplianceRate);
            Assert.Equal(0, stats.ErrorRate);
        }

        [Fact]
        public void ComplianceStatistics_ComplianceRate_CalculatesCorrectly()
        {
            var stats = new ComplianceStatistics
            {
                TotalAssessments = 50,
                CompliantCount = 40,
                NonCompliantCount = 10,
                StartTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow
            };

            Assert.Equal(0.8, stats.ComplianceRate);
        }

        [Fact]
        public void ComplianceStatistics_ErrorRate_CalculatesCorrectly()
        {
            var stats = new ComplianceStatistics
            {
                TotalAssessments = 100,
                ErrorCount = 5,
                StartTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow
            };

            Assert.Equal(0.05, stats.ErrorRate);
        }

        #endregion
    }
}
