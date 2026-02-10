using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Security;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK security strategy infrastructure types:
    /// SecurityDomain, SecurityContext, SecurityDecision, ZeroTrust types,
    /// threat detection types, integrity verification types, and ISecurityStrategy.
    /// </summary>
    public class SdkSecurityStrategyTests
    {
        #region SecurityDomain Enum Tests

        [Fact]
        public void SecurityDomain_HasExpected11Values()
        {
            var values = Enum.GetValues<SecurityDomain>();
            Assert.Equal(11, values.Length);
        }

        [Theory]
        [InlineData(SecurityDomain.AccessControl, 0)]
        [InlineData(SecurityDomain.Identity, 1)]
        [InlineData(SecurityDomain.ThreatDetection, 2)]
        [InlineData(SecurityDomain.Integrity, 3)]
        [InlineData(SecurityDomain.Audit, 4)]
        [InlineData(SecurityDomain.Privacy, 5)]
        [InlineData(SecurityDomain.DataProtection, 6)]
        [InlineData(SecurityDomain.Network, 7)]
        [InlineData(SecurityDomain.Compliance, 8)]
        [InlineData(SecurityDomain.IntegrityVerification, 9)]
        [InlineData(SecurityDomain.ZeroTrust, 10)]
        public void SecurityDomain_HasCorrectValues(SecurityDomain domain, int expectedValue)
        {
            Assert.Equal(expectedValue, (int)domain);
        }

        #endregion

        #region SecurityContext Tests

        [Fact]
        public void SecurityContext_ConstructionWithRequiredFields_Succeeds()
        {
            var context = new SecurityContext
            {
                UserId = "user-123",
                Resource = "container/blob",
                Operation = "read"
            };

            Assert.Equal("user-123", context.UserId);
            Assert.Equal("container/blob", context.Resource);
            Assert.Equal("read", context.Operation);
        }

        [Fact]
        public void SecurityContext_OptionalFieldsDefaultToNull()
        {
            var context = new SecurityContext
            {
                UserId = "user-1",
                Resource = "res-1",
                Operation = "write"
            };

            Assert.Null(context.TenantId);
            Assert.Null(context.Roles);
            Assert.Null(context.UserAttributes);
            Assert.Null(context.ResourceAttributes);
            Assert.Null(context.Environment);
            Assert.Null(context.AuthenticationStrength);
            Assert.Null(context.SessionId);
            Assert.Null(context.Metadata);
        }

        [Fact]
        public void SecurityContext_CreateSimple_SetsRequiredFields()
        {
            var context = SecurityContext.CreateSimple("user-1", "resource-1", "delete");

            Assert.Equal("user-1", context.UserId);
            Assert.Equal("resource-1", context.Resource);
            Assert.Equal("delete", context.Operation);
        }

        [Fact]
        public void SecurityContext_WithAllOptionalFields_SetsCorrectly()
        {
            var roles = new List<string> { "admin", "reader" };
            var userAttrs = new Dictionary<string, object> { ["department"] = "engineering" };
            var context = new SecurityContext
            {
                UserId = "user-1",
                Resource = "res-1",
                Operation = "admin",
                TenantId = "tenant-1",
                Roles = roles,
                UserAttributes = userAttrs,
                AuthenticationStrength = "mfa",
                SessionId = "session-abc"
            };

            Assert.Equal("tenant-1", context.TenantId);
            Assert.Equal(2, context.Roles!.Count);
            Assert.Equal("mfa", context.AuthenticationStrength);
            Assert.Equal("session-abc", context.SessionId);
        }

        #endregion

        #region SecurityDecision Tests

        [Fact]
        public void SecurityDecision_AllowFactory_CreatesAllowedDecision()
        {
            var decision = SecurityDecision.Allow(
                SecurityDomain.AccessControl,
                "Access granted by RBAC policy",
                "policy-123");

            Assert.True(decision.Allowed);
            Assert.Equal(SecurityDomain.AccessControl, decision.Domain);
            Assert.Equal("Access granted by RBAC policy", decision.Reason);
            Assert.Equal("policy-123", decision.PolicyId);
        }

        [Fact]
        public void SecurityDecision_DenyFactory_CreatesDeniedDecision()
        {
            var actions = new List<string> { "Enable MFA" };
            var decision = SecurityDecision.Deny(
                SecurityDomain.Identity,
                "MFA required",
                "policy-mfa",
                actions);

            Assert.False(decision.Allowed);
            Assert.Equal(SecurityDomain.Identity, decision.Domain);
            Assert.Equal("MFA required", decision.Reason);
            Assert.Equal("policy-mfa", decision.PolicyId);
            Assert.Single(decision.RequiredActions!);
            Assert.Equal("Enable MFA", decision.RequiredActions![0]);
        }

        [Fact]
        public void SecurityDecision_DefaultConfidence_IsOne()
        {
            var decision = SecurityDecision.Allow(SecurityDomain.AccessControl, "test");
            Assert.Equal(1.0, decision.Confidence);
        }

        [Fact]
        public void SecurityDecision_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = SecurityDecision.Allow(
                SecurityDomain.ZeroTrust,
                "Zero trust check passed",
                "zt-policy-1");

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<SecurityDecision>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.Allowed, deserialized!.Allowed);
            Assert.Equal(original.Domain, deserialized.Domain);
            Assert.Equal(original.Reason, deserialized.Reason);
            Assert.Equal(original.PolicyId, deserialized.PolicyId);
        }

        #endregion

        #region ZeroTrust Types Tests

        [Fact]
        public void ZeroTrustPrinciple_Has7Values()
        {
            var values = Enum.GetValues<ZeroTrustPrinciple>();
            Assert.Equal(7, values.Length);
        }

        [Theory]
        [InlineData(ZeroTrustPrinciple.NeverTrustAlwaysVerify, 0)]
        [InlineData(ZeroTrustPrinciple.LeastPrivilege, 1)]
        [InlineData(ZeroTrustPrinciple.AssumeBreachLimitBlastRadius, 2)]
        [InlineData(ZeroTrustPrinciple.MicroSegmentation, 3)]
        [InlineData(ZeroTrustPrinciple.ContinuousValidation, 4)]
        [InlineData(ZeroTrustPrinciple.ContextAwareAccess, 5)]
        [InlineData(ZeroTrustPrinciple.EncryptEverything, 6)]
        public void ZeroTrustPrinciple_HasCorrectValues(ZeroTrustPrinciple principle, int expectedValue)
        {
            Assert.Equal(expectedValue, (int)principle);
        }

        [Fact]
        public void ZeroTrustPolicy_Construction_SetsRequiredProperties()
        {
            var rules = new List<ZeroTrustRule>
            {
                new ZeroTrustRule
                {
                    RuleId = "rule-1",
                    Name = "Always verify",
                    Principle = ZeroTrustPrinciple.NeverTrustAlwaysVerify
                }
            };

            var policy = new ZeroTrustPolicy
            {
                PolicyId = "zt-policy-1",
                Name = "Default Zero Trust",
                Rules = rules
            };

            Assert.Equal("zt-policy-1", policy.PolicyId);
            Assert.Equal("Default Zero Trust", policy.Name);
            Assert.Single(policy.Rules);
            Assert.Equal(1, policy.Version);
            Assert.False(policy.DefaultAllow);
        }

        [Fact]
        public void ZeroTrustEvaluation_Construction_SetsRequiredProperties()
        {
            var evaluation = new ZeroTrustEvaluation
            {
                Passed = true,
                PolicyId = "zt-1",
                TrustScore = 0.95
            };

            Assert.True(evaluation.Passed);
            Assert.Equal("zt-1", evaluation.PolicyId);
            Assert.Equal(0.95, evaluation.TrustScore);
        }

        [Fact]
        public void PrincipleEvaluation_Construction_SetsRequiredProperties()
        {
            var eval = new PrincipleEvaluation
            {
                Principle = ZeroTrustPrinciple.LeastPrivilege,
                Satisfied = true,
                Score = 0.8,
                Reason = "Minimum permissions verified"
            };

            Assert.Equal(ZeroTrustPrinciple.LeastPrivilege, eval.Principle);
            Assert.True(eval.Satisfied);
            Assert.Equal(0.8, eval.Score);
            Assert.Equal("Minimum permissions verified", eval.Reason);
        }

        [Fact]
        public void ZeroTrustRule_DefaultEnabled_IsTrue()
        {
            var rule = new ZeroTrustRule
            {
                RuleId = "r1",
                Name = "Test rule",
                Principle = ZeroTrustPrinciple.EncryptEverything
            };

            Assert.True(rule.Enabled);
            Assert.Equal(0, rule.Priority);
        }

        #endregion

        #region Threat Detection Types Tests

        [Fact]
        public void SecurityThreatType_Has16Values()
        {
            var values = Enum.GetValues<SecurityThreatType>();
            Assert.Equal(16, values.Length);
        }

        [Theory]
        [InlineData(SecurityThreatType.Unknown, 0)]
        [InlineData(SecurityThreatType.UnauthorizedAccess, 1)]
        [InlineData(SecurityThreatType.BruteForce, 2)]
        [InlineData(SecurityThreatType.DataExfiltration, 5)]
        [InlineData(SecurityThreatType.InjectionAttack, 7)]
        [InlineData(SecurityThreatType.CryptographicAttack, 15)]
        public void SecurityThreatType_HasExpectedValues(SecurityThreatType type, int expectedValue)
        {
            Assert.Equal(expectedValue, (int)type);
        }

        [Fact]
        public void SecurityThreatSeverity_Has5Values()
        {
            var values = Enum.GetValues<SecurityThreatSeverity>();
            Assert.Equal(5, values.Length);
        }

        [Theory]
        [InlineData(SecurityThreatSeverity.Info, 0)]
        [InlineData(SecurityThreatSeverity.Low, 1)]
        [InlineData(SecurityThreatSeverity.Medium, 2)]
        [InlineData(SecurityThreatSeverity.High, 3)]
        [InlineData(SecurityThreatSeverity.Critical, 4)]
        public void SecurityThreatSeverity_HasCVSSAlignedValues(SecurityThreatSeverity severity, int expectedValue)
        {
            Assert.Equal(expectedValue, (int)severity);
        }

        [Fact]
        public void ThreatIndicator_Construction_SetsRequiredFields()
        {
            var indicator = new ThreatIndicator
            {
                Type = SecurityThreatType.BruteForce,
                Severity = SecurityThreatSeverity.High,
                Confidence = 0.92,
                Description = "Multiple failed login attempts detected"
            };

            Assert.Equal(SecurityThreatType.BruteForce, indicator.Type);
            Assert.Equal(SecurityThreatSeverity.High, indicator.Severity);
            Assert.Equal(0.92, indicator.Confidence);
            Assert.Equal("Multiple failed login attempts detected", indicator.Description);
        }

        [Fact]
        public void ThreatDetectionResult_CleanFactory_ReturnsNoThreats()
        {
            var result = ThreatDetectionResult.Clean;

            Assert.False(result.ThreatsDetected);
            Assert.Equal(SecurityThreatSeverity.Info, result.HighestSeverity);
            Assert.Equal(0.0, result.RiskScore);
        }

        [Fact]
        public void ThreatIndicator_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = new ThreatIndicator
            {
                Type = SecurityThreatType.DataExfiltration,
                Severity = SecurityThreatSeverity.Critical,
                Confidence = 0.85,
                Description = "Unusual download pattern",
                Source = "behavioral-analysis",
                AffectedResource = "database-prod"
            };

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<ThreatIndicator>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.Type, deserialized!.Type);
            Assert.Equal(original.Severity, deserialized.Severity);
            Assert.Equal(original.Confidence, deserialized.Confidence);
            Assert.Equal(original.Source, deserialized.Source);
        }

        #endregion

        #region Integrity Verification Types Tests

        [Fact]
        public void IntegrityViolationType_HasExpectedValues()
        {
            var values = Enum.GetValues<IntegrityViolationType>();
            Assert.Equal(10, values.Length);
            Assert.Contains(IntegrityViolationType.HashMismatch, values);
            Assert.Contains(IntegrityViolationType.TamperDetected, values);
            Assert.Contains(IntegrityViolationType.ChainOfCustodyBroken, values);
            Assert.Contains(IntegrityViolationType.StorageIntegrityViolation, values);
        }

        [Fact]
        public void IntegrityViolation_Construction_SetsRequiredFields()
        {
            var violation = new IntegrityViolation
            {
                Type = IntegrityViolationType.HashMismatch,
                Description = "SHA-256 hash does not match",
                AffectedResource = "blob/data-001",
                ExpectedValue = "abc123",
                ActualValue = "def456"
            };

            Assert.Equal(IntegrityViolationType.HashMismatch, violation.Type);
            Assert.Equal("blob/data-001", violation.AffectedResource);
            Assert.Equal("abc123", violation.ExpectedValue);
            Assert.Equal("def456", violation.ActualValue);
            Assert.Equal(SecurityThreatSeverity.High, violation.Severity);
        }

        [Fact]
        public void IntegrityVerificationResult_ValidFactory_CreatesValidResult()
        {
            var result = IntegrityVerificationResult.Valid("abc123hash", "SHA-256");

            Assert.True(result.IsValid);
            Assert.Equal("abc123hash", result.ComputedHash);
            Assert.Equal("SHA-256", result.HashAlgorithm);
            Assert.Empty(result.Violations);
        }

        [Fact]
        public void IntegrityVerificationResult_InvalidFactory_CreatesInvalidResult()
        {
            var violations = new List<IntegrityViolation>
            {
                new IntegrityViolation
                {
                    Type = IntegrityViolationType.TamperDetected,
                    Description = "Data modified after sealing",
                    AffectedResource = "blob/sealed-001"
                }
            };

            var result = IntegrityVerificationResult.Invalid(violations);

            Assert.False(result.IsValid);
            Assert.Single(result.Violations);
        }

        [Fact]
        public void CustodyRecord_Construction_SetsRequiredFields()
        {
            var record = new CustodyRecord
            {
                Principal = "admin@company.com",
                Action = "created",
                Timestamp = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                DataHash = "sha256:abc",
                Location = "us-east-1"
            };

            Assert.Equal("admin@company.com", record.Principal);
            Assert.Equal("created", record.Action);
            Assert.Equal("sha256:abc", record.DataHash);
            Assert.Equal("us-east-1", record.Location);
        }

        #endregion

        #region ISecurityStrategy Interface Tests

        [Fact]
        public void ISecurityStrategy_DefinesEvaluateAsyncMethod()
        {
            var method = typeof(ISecurityStrategy).GetMethod("EvaluateAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<SecurityDecision>), method!.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(SecurityContext), parameters[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        }

        [Fact]
        public void ISecurityStrategy_DefinesEvaluateDomainAsyncMethod()
        {
            var method = typeof(ISecurityStrategy).GetMethod("EvaluateDomainAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<SecurityDecision>), method!.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(SecurityDomain), parameters[1].ParameterType);
        }

        [Fact]
        public void ISecurityStrategy_DefinesValidateContextMethod()
        {
            var method = typeof(ISecurityStrategy).GetMethod("ValidateContext");
            Assert.NotNull(method);
            Assert.Equal(typeof(bool), method!.ReturnType);
        }

        [Fact]
        public void ISecurityStrategy_DefinesGetStatisticsMethod()
        {
            var method = typeof(ISecurityStrategy).GetMethod("GetStatistics");
            Assert.NotNull(method);
            Assert.Equal(typeof(SecurityStatistics), method!.ReturnType);
        }

        [Fact]
        public void ISecurityStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(ISecurityStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void ISecurityStrategy_DefinesSupportedDomainsProperty()
        {
            var prop = typeof(ISecurityStrategy).GetProperty("SupportedDomains");
            Assert.NotNull(prop);
            Assert.True(prop!.PropertyType.IsGenericType);
        }

        #endregion

        #region SecurityStatistics Tests

        [Fact]
        public void SecurityStatistics_Empty_HasZeroCounts()
        {
            var stats = SecurityStatistics.Empty;

            Assert.Equal(0, stats.TotalEvaluations);
            Assert.Equal(0, stats.AllowedCount);
            Assert.Equal(0, stats.DeniedCount);
            Assert.Equal(0, stats.ErrorCount);
            Assert.Equal(0, stats.DenialRate);
            Assert.Equal(0, stats.ErrorRate);
        }

        [Fact]
        public void SecurityStatistics_DenialRate_CalculatesCorrectly()
        {
            var stats = new SecurityStatistics
            {
                TotalEvaluations = 100,
                AllowedCount = 80,
                DeniedCount = 20,
                StartTime = DateTime.UtcNow,
                LastUpdateTime = DateTime.UtcNow
            };

            Assert.Equal(0.2, stats.DenialRate);
        }

        #endregion

        #region IThreatDetector Interface Tests

        [Fact]
        public void IThreatDetector_DefinesDetectAsyncMethod()
        {
            var method = typeof(IThreatDetector).GetMethod("DetectAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<ThreatDetectionResult>), method!.ReturnType);
        }

        [Fact]
        public void IThreatDetector_DefinesReportConfirmedThreatAsyncMethod()
        {
            var method = typeof(IThreatDetector).GetMethod("ReportConfirmedThreatAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        #endregion

        #region IIntegrityVerifier Interface Tests

        [Fact]
        public void IIntegrityVerifier_DefinesVerifyAsyncMethod()
        {
            var method = typeof(IIntegrityVerifier).GetMethod("VerifyAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<IntegrityVerificationResult>), method!.ReturnType);
        }

        [Fact]
        public void IIntegrityVerifier_DefinesSupportedAlgorithmsProperty()
        {
            var prop = typeof(IIntegrityVerifier).GetProperty("SupportedAlgorithms");
            Assert.NotNull(prop);
        }

        #endregion
    }
}
