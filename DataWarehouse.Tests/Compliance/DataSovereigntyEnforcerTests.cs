using Xunit;
using FluentAssertions;
using DataWarehouse.Plugins.UltimateCompliance.Features;
using System.Collections.Generic;

namespace DataWarehouse.Tests.Compliance
{
    /// <summary>
    /// Tests for data sovereignty geofencing enforcing GDPR, CCPA, PIPL, and other
    /// regulatory framework compliance for cross-border data transfers.
    /// </summary>
    public class DataSovereigntyEnforcerTests
    {
        #region Regional Policy Tests

        [Fact]
        public void AddRegionPolicy_EuPolicyWithGdpr_PolicyStored()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var euPolicy = new RegionPolicy
            {
                Region = "EU",
                RegulatoryFramework = "GDPR",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "StandardContractualClauses", "AdequacyDecision" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };

            // Act
            enforcer.AddRegionPolicy("EU", euPolicy);
            var retrievedPolicy = enforcer.GetRegionPolicy("EU");

            // Assert
            retrievedPolicy.Should().NotBeNull();
            retrievedPolicy!.Region.Should().Be("EU");
            retrievedPolicy.RegulatoryFramework.Should().Be("GDPR");
            retrievedPolicy.ProhibitedDestinations.Should().Contain("CN");
            retrievedPolicy.ProhibitedDestinations.Should().Contain("RU");
            retrievedPolicy.RequiredMechanisms.Should().Contain("StandardContractualClauses");
            retrievedPolicy.RequiredMechanisms.Should().Contain("AdequacyDecision");
            retrievedPolicy.AllowedClassifications.Should().Contain("Public");
            retrievedPolicy.AllowedClassifications.Should().Contain("Internal");
            retrievedPolicy.AllowedClassifications.Should().Contain("Confidential");
        }

        [Fact]
        public void AddRegionPolicy_UsPolicyWithCcpa_PolicyStored()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "CCPA",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential", "Secret" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };

            // Act
            enforcer.AddRegionPolicy("US", usPolicy);
            var retrievedPolicy = enforcer.GetRegionPolicy("US");

            // Assert
            retrievedPolicy.Should().NotBeNull();
            retrievedPolicy!.RegulatoryFramework.Should().Be("CCPA");
            retrievedPolicy.AllowedClassifications.Should().Contain("Secret");
        }

        [Fact]
        public void AddRegionPolicy_CnPolicyWithPipl_PolicyStored()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new[] { "US", "EU", "UK", "JP", "AU" },
                RequiredMechanisms = new[] { "GovernmentApproval" },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };

            // Act
            enforcer.AddRegionPolicy("CN", cnPolicy);
            var retrievedPolicy = enforcer.GetRegionPolicy("CN");

            // Assert
            retrievedPolicy.Should().NotBeNull();
            retrievedPolicy!.RegulatoryFramework.Should().Be("PIPL");
            retrievedPolicy.RequiresLocalProcessing.Should().BeTrue();
            retrievedPolicy.AllowsCloudStorage.Should().BeFalse();
        }

        #endregion

        #region Transfer Validation Tests (EU -> Destinations)

        [Fact]
        public void ValidateTransfer_EuToUsWithoutSccs_TransferDenied()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();

            // Act - attempt transfer without configuring allowed transfer mechanism
            var result = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.StandardContractualClauses);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("No transfer mechanism configured");
            result.RequiredMechanisms.Should().Contain("StandardContractualClauses");
        }

        [Fact]
        public void ValidateTransfer_EuToUsWithSccs_TransferAllowed()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);

            // Act
            var result = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.StandardContractualClauses);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("Transfer allowed");
            result.Message.Should().Contain("StandardContractualClauses");
        }

        [Fact]
        public void ValidateTransfer_EuToUsWithAdequacyDecision_TransferAllowed()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            enforcer.AllowTransfer("EU", "US", TransferMechanism.AdequacyDecision);

            // Act
            var result = enforcer.ValidateTransfer("EU", "US", "Internal", TransferMechanism.AdequacyDecision);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("Transfer allowed");
            result.Message.Should().Contain("AdequacyDecision");
        }

        [Fact]
        public void ValidateTransfer_EuToCn_TransferDeniedProhibited()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new string[] { },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            // Act
            var result = enforcer.ValidateTransfer("EU", "CN", "Public", TransferMechanism.StandardContractualClauses);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("explicitly prohibited");
        }

        [Fact]
        public void ValidateTransfer_EuToUk_TransferAllowedAdequacyDecision()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            var ukPolicy = new RegionPolicy
            {
                Region = "UK",
                RegulatoryFramework = "UK-GDPR",
                ProhibitedDestinations = new string[] { },
                RequiredMechanisms = new[] { "AdequacyDecision" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("UK", ukPolicy);
            enforcer.AllowTransfer("EU", "UK", TransferMechanism.AdequacyDecision);

            // Act
            var result = enforcer.ValidateTransfer("EU", "UK", "Confidential", TransferMechanism.AdequacyDecision);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("Transfer allowed");
        }

        #endregion

        #region Transfer Validation Tests (US -> Destinations)

        [Fact]
        public void ValidateTransfer_UsToEu_RequiresMechanism()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();

            // Act - attempt without configured mechanism
            var result = enforcer.ValidateTransfer("US", "EU", "Public", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("No transfer mechanism configured");
        }

        [Fact]
        public void ValidateTransfer_UsToCn_TransferDeniedProhibited()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new string[] { },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            // Act
            var result = enforcer.ValidateTransfer("US", "CN", "Public", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("explicitly prohibited");
        }

        [Fact]
        public void ValidateTransfer_UsToUs_TransferAllowedDomestic()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();

            // Act
            var result = enforcer.ValidateTransfer("US", "US", "Confidential", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("same region");
        }

        #endregion

        #region Data Classification Tests

        [Fact]
        public void ValidateTransfer_EuAllowsPublicInternalConfidential_ClassificationsEnforced()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);

            // Act & Assert - Public allowed
            var publicResult = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.StandardContractualClauses);
            publicResult.IsAllowed.Should().BeTrue();

            // Internal allowed
            var internalResult = enforcer.ValidateTransfer("EU", "US", "Internal", TransferMechanism.StandardContractualClauses);
            internalResult.IsAllowed.Should().BeTrue();

            // Confidential allowed
            var confidentialResult = enforcer.ValidateTransfer("EU", "US", "Confidential", TransferMechanism.StandardContractualClauses);
            confidentialResult.IsAllowed.Should().BeTrue();

            // Secret blocked
            var secretResult = enforcer.ValidateTransfer("EU", "US", "Secret", TransferMechanism.StandardContractualClauses);
            secretResult.IsAllowed.Should().BeFalse();
            secretResult.Message.Should().Contain("classification");
            secretResult.Message.Should().Contain("Secret");
        }

        [Fact]
        public void ValidateTransfer_CnRequiresLocalProcessing_SecretClassificationBlocked()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new[] { "US", "EU" },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            var hkPolicy = new RegionPolicy
            {
                Region = "HK",
                RegulatoryFramework = "HKPDPO",
                ProhibitedDestinations = new string[] { },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public", "Internal" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("HK", hkPolicy);

            enforcer.AllowTransfer("CN", "HK", TransferMechanism.ConsentBased);

            // Act
            var result = enforcer.ValidateTransfer("CN", "HK", "Secret", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("classification");
        }

        #endregion

        #region Required Mechanisms Tests

        [Fact]
        public void ValidateTransfer_MissingRequiredMechanism_ReturnsValidationFailure()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);

            // Act - attempt with wrong mechanism
            var result = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("not allowed");
            result.RequiredMechanisms.Should().Contain("StandardContractualClauses");
        }

        [Fact]
        public void ValidateTransfer_MultipleMechanismsConfigured_AnyMechanismAllows()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);
            enforcer.AllowTransfer("EU", "US", TransferMechanism.AdequacyDecision);

            // Act - use first mechanism
            var result1 = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.StandardContractualClauses);

            // Act - use second mechanism
            var result2 = enforcer.ValidateTransfer("EU", "US", "Public", TransferMechanism.AdequacyDecision);

            // Assert
            result1.IsAllowed.Should().BeTrue();
            result2.IsAllowed.Should().BeTrue();
        }

        #endregion

        #region Destination Queries Tests

        [Fact]
        public void GetAllowedDestinations_EuWithUsAndUk_ReturnsAllowedList()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();
            var ukPolicy = new RegionPolicy
            {
                Region = "UK",
                RegulatoryFramework = "UK-GDPR",
                ProhibitedDestinations = new string[] { },
                RequiredMechanisms = new[] { "AdequacyDecision" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("UK", ukPolicy);
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);
            enforcer.AllowTransfer("EU", "UK", TransferMechanism.AdequacyDecision);

            // Act
            var allowedDestinations = enforcer.GetAllowedDestinations("EU");

            // Assert
            allowedDestinations.Should().Contain("US");
            allowedDestinations.Should().Contain("UK");
            allowedDestinations.Should().NotContain("CN");
        }

        [Fact]
        public void GetAllowedDestinations_NoTransfersConfigured_ReturnsEmptyList()
        {
            // Arrange
            var enforcer = SetupEuUsScenario();

            // Act
            var allowedDestinations = enforcer.GetAllowedDestinations("EU");

            // Assert
            allowedDestinations.Should().BeEmpty();
        }

        #endregion

        #region Local Processing Tests

        [Fact]
        public void RegionPolicy_RequiresLocalProcessing_EnforcesInRegionProcessing()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new[] { "US", "EU", "UK", "JP", "AU" },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            // Act
            var policy = enforcer.GetRegionPolicy("CN");

            // Assert
            policy.Should().NotBeNull();
            policy!.RequiresLocalProcessing.Should().BeTrue();
            policy.AllowsCloudStorage.Should().BeFalse();
        }

        [Fact]
        public void RegionPolicy_AllowsCloudStorageFalse_BlocksCloudProviders()
        {
            // Arrange
            var enforcer = new DataSovereigntyEnforcer();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL",
                ProhibitedDestinations = new[] { "US" },
                RequiredMechanisms = new string[] { },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            // Act
            var policy = enforcer.GetRegionPolicy("CN");

            // Assert
            policy.Should().NotBeNull();
            policy!.AllowsCloudStorage.Should().BeFalse();
        }

        #endregion

        #region Real-World Regulatory Scenarios

        [Fact]
        public void RealWorld_GdprArticle46Sccs_ValidatesStandardContractualClauses()
        {
            // Arrange - GDPR Article 46 requires SCCs for third country transfers
            var enforcer = new DataSovereigntyEnforcer();
            var euPolicy = new RegionPolicy
            {
                Region = "EU",
                RegulatoryFramework = "GDPR Article 46",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "StandardContractualClauses" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "CCPA",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential", "Secret" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("EU", euPolicy);
            enforcer.AddRegionPolicy("US", usPolicy);
            enforcer.AllowTransfer("EU", "US", TransferMechanism.StandardContractualClauses);

            // Act
            var result = enforcer.ValidateTransfer("EU", "US", "Confidential", TransferMechanism.StandardContractualClauses);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("StandardContractualClauses");
        }

        [Fact]
        public void RealWorld_CcpaCpra_ValidatesCrossBorderProvisions()
        {
            // Arrange - CCPA/CPRA cross-border transfer provisions
            var enforcer = new DataSovereigntyEnforcer();
            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "CCPA/CPRA",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            var euPolicy = new RegionPolicy
            {
                Region = "EU",
                RegulatoryFramework = "GDPR",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "StandardContractualClauses" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("US", usPolicy);
            enforcer.AddRegionPolicy("EU", euPolicy);
            enforcer.AllowTransfer("US", "EU", TransferMechanism.ConsentBased);

            // Act
            var result = enforcer.ValidateTransfer("US", "EU", "Internal", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeTrue();
        }

        [Fact]
        public void RealWorld_PiplArticle38_EnforcesDataLocalization()
        {
            // Arrange - PIPL Article 38 requires data localization
            var enforcer = new DataSovereigntyEnforcer();
            var cnPolicy = new RegionPolicy
            {
                Region = "CN",
                RegulatoryFramework = "PIPL Article 38",
                ProhibitedDestinations = new[] { "US", "EU", "UK", "JP", "AU" },
                RequiredMechanisms = new[] { "GovernmentApproval" },
                AllowedClassifications = new[] { "Public" },
                RequiresLocalProcessing = true,
                AllowsCloudStorage = false
            };
            enforcer.AddRegionPolicy("CN", cnPolicy);

            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "CCPA",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential", "Secret" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("US", usPolicy);

            // Act - attempt cross-border transfer
            var result = enforcer.ValidateTransfer("CN", "US", "Public", TransferMechanism.ConsentBased);

            // Assert
            result.IsAllowed.Should().BeFalse();
            result.Message.Should().Contain("prohibited");
        }

        [Fact]
        public void RealWorld_EuUsDataPrivacyFramework_ValidatesAdequacyDecision()
        {
            // Arrange - EU-US Data Privacy Framework (Adequacy Decision)
            var enforcer = new DataSovereigntyEnforcer();
            var euPolicy = new RegionPolicy
            {
                Region = "EU",
                RegulatoryFramework = "GDPR with EU-US DPF",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "AdequacyDecision", "StandardContractualClauses" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "EU-US DPF",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential", "Secret" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };
            enforcer.AddRegionPolicy("EU", euPolicy);
            enforcer.AddRegionPolicy("US", usPolicy);
            enforcer.AllowTransfer("EU", "US", TransferMechanism.AdequacyDecision);

            // Act
            var result = enforcer.ValidateTransfer("EU", "US", "Internal", TransferMechanism.AdequacyDecision);

            // Assert
            result.IsAllowed.Should().BeTrue();
            result.Message.Should().Contain("AdequacyDecision");
        }

        #endregion

        #region Helper Methods

        private static DataSovereigntyEnforcer SetupEuUsScenario()
        {
            var enforcer = new DataSovereigntyEnforcer();

            var euPolicy = new RegionPolicy
            {
                Region = "EU",
                RegulatoryFramework = "GDPR",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "StandardContractualClauses", "AdequacyDecision" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };

            var usPolicy = new RegionPolicy
            {
                Region = "US",
                RegulatoryFramework = "CCPA",
                ProhibitedDestinations = new[] { "CN", "RU" },
                RequiredMechanisms = new[] { "ConsentBased" },
                AllowedClassifications = new[] { "Public", "Internal", "Confidential", "Secret" },
                RequiresLocalProcessing = false,
                AllowsCloudStorage = true
            };

            enforcer.AddRegionPolicy("EU", euPolicy);
            enforcer.AddRegionPolicy("US", usPolicy);

            return enforcer;
        }

        #endregion
    }
}
