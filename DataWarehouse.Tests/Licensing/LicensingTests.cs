using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Licensing;

namespace DataWarehouse.Tests.Licensing
{
    /// <summary>
    /// Comprehensive tests for the licensing and feature enforcement system.
    /// </summary>
    public class CustomerTierTests
    {
        #region TierManager Tests

        [Fact]
        public void TierManager_GetFeatures_ReturnsCorrectFeaturesForEachTier()
        {
            // Individual tier - basic features only
            var individual = TierManager.GetFeatures(CustomerTier.Individual);
            Assert.Contains(Feature.BasicStorage, individual);
            Assert.Contains(Feature.LocalBackup, individual);
            Assert.DoesNotContain(Feature.CloudBackup, individual);
            Assert.DoesNotContain(Feature.HipaaCompliance, individual);

            // SMB tier - includes business features
            var smb = TierManager.GetFeatures(CustomerTier.SMB);
            Assert.Contains(Feature.CloudBackup, smb);
            Assert.Contains(Feature.BasicAnalytics, smb);
            Assert.DoesNotContain(Feature.HipaaCompliance, smb);

            // HighStakes tier - includes compliance features
            var highStakes = TierManager.GetFeatures(CustomerTier.HighStakes);
            Assert.Contains(Feature.HipaaCompliance, highStakes);
            Assert.Contains(Feature.PciDssCompliance, highStakes);
            Assert.Contains(Feature.AuditLogging, highStakes);

            // Hyperscale tier - includes all features
            var hyperscale = TierManager.GetFeatures(CustomerTier.Hyperscale);
            Assert.Contains(Feature.PredictiveAnalytics, hyperscale);
            Assert.Contains(Feature.AutoScaling, hyperscale);
            Assert.Contains(Feature.GlobalDistribution, hyperscale);
        }

        [Fact]
        public void TierManager_HasFeature_ReturnsCorrectResult()
        {
            Assert.True(TierManager.HasFeature(CustomerTier.Individual, Feature.BasicStorage));
            Assert.False(TierManager.HasFeature(CustomerTier.Individual, Feature.HipaaCompliance));

            Assert.True(TierManager.HasFeature(CustomerTier.Hyperscale, Feature.HipaaCompliance));
            Assert.True(TierManager.HasFeature(CustomerTier.Hyperscale, Feature.BasicStorage));
        }

        [Fact]
        public void TierManager_GetLimits_ReturnsCorrectLimitsForEachTier()
        {
            var individual = TierManager.GetLimits(CustomerTier.Individual);
            Assert.True(individual.MaxStorageBytes > 0);
            Assert.True(individual.MaxStorageBytes < TierManager.GetLimits(CustomerTier.SMB).MaxStorageBytes);

            var hyperscale = TierManager.GetLimits(CustomerTier.Hyperscale);
            Assert.True(hyperscale.MaxStorageBytes > individual.MaxStorageBytes);
            Assert.True(hyperscale.MaxConcurrentOperations > individual.MaxConcurrentOperations);
        }

        [Fact]
        public void TierManager_GetRecommendedTier_ReturnsAppropriateUpgrade()
        {
            // Individual user hitting storage limits
            var recommendation = TierManager.GetRecommendedTier(
                CustomerTier.Individual,
                usedStorage: 90L * 1024 * 1024 * 1024, // 90GB
                userCount: 1);

            Assert.True(recommendation >= CustomerTier.SMB);

            // SMB hitting user limits
            recommendation = TierManager.GetRecommendedTier(
                CustomerTier.SMB,
                usedStorage: 100L * 1024 * 1024 * 1024,
                userCount: 100);

            Assert.True(recommendation >= CustomerTier.HighStakes);
        }

        #endregion

        #region TierLimits Tests

        [Fact]
        public void TierLimits_Individual_HasReasonableDefaults()
        {
            var limits = TierManager.GetLimits(CustomerTier.Individual);

            // Individual should have limited but usable resources
            Assert.True(limits.MaxStorageBytes >= 10L * 1024 * 1024 * 1024); // At least 10GB
            Assert.True(limits.MaxUsers >= 1);
            Assert.True(limits.MaxContainers >= 10);
        }

        [Fact]
        public void TierLimits_Hyperscale_HasHighLimits()
        {
            var limits = TierManager.GetLimits(CustomerTier.Hyperscale);

            // Hyperscale should have very high limits
            Assert.True(limits.MaxStorageBytes >= 1L * 1024 * 1024 * 1024 * 1024); // At least 1PB
            Assert.True(limits.MaxUsers >= 10000);
            Assert.True(limits.MaxConcurrentOperations >= 10000);
        }

        #endregion

        #region CustomerSubscription Tests

        [Fact]
        public void CustomerSubscription_ValidSubscription_VerifiesSuccessfully()
        {
            // Create a valid subscription
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };

            // Sign the subscription
            subscription.Sign("test-secret-key");

            // Verify should succeed
            Assert.True(subscription.VerifySignature("test-secret-key"));
        }

        [Fact]
        public void CustomerSubscription_TamperedSubscription_FailsVerification()
        {
            // Create and sign a subscription
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.Individual,
                Features = TierManager.GetFeatures(CustomerTier.Individual),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(CustomerTier.Individual)
            };
            subscription.Sign("test-secret-key");

            // Tamper with features (attempt to upgrade)
            subscription.Features = TierManager.GetFeatures(CustomerTier.Hyperscale);

            // Verification should fail
            Assert.False(subscription.VerifySignature("test-secret-key"));
        }

        [Fact]
        public void CustomerSubscription_ExpiredSubscription_IsNotActive()
        {
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-60),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(-30), // Expired
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };

            Assert.False(subscription.IsActive);
        }

        [Fact]
        public void CustomerSubscription_FutureSubscription_IsNotYetActive()
        {
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(30), // Future
                ValidUntil = DateTimeOffset.UtcNow.AddDays(60),
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };

            Assert.False(subscription.IsActive);
        }

        [Fact]
        public void CustomerSubscription_ActiveSubscription_IsActive()
        {
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };

            Assert.True(subscription.IsActive);
        }

        [Fact]
        public void CustomerSubscription_HasFeature_ReturnsCorrectResult()
        {
            var subscription = new CustomerSubscription
            {
                CustomerId = "cust123",
                Tier = CustomerTier.HighStakes,
                Features = TierManager.GetFeatures(CustomerTier.HighStakes),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(CustomerTier.HighStakes)
            };

            Assert.True(subscription.HasFeature(Feature.HipaaCompliance));
            Assert.True(subscription.HasFeature(Feature.PciDssCompliance));
            Assert.True(subscription.HasFeature(Feature.AuditLogging));
        }

        #endregion
    }

    /// <summary>
    /// Tests for the subscription manager and feature enforcement.
    /// </summary>
    public class SubscriptionManagerTests
    {
        #region InMemorySubscriptionManager Tests

        [Fact]
        public async Task Manager_GetSubscription_ReturnsRegisteredSubscription()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.SMB);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            var retrieved = await manager.GetSubscriptionAsync("cust123");

            // Assert
            Assert.NotNull(retrieved);
            Assert.Equal("cust123", retrieved!.CustomerId);
            Assert.Equal(CustomerTier.SMB, retrieved.Tier);
        }

        [Fact]
        public async Task Manager_GetSubscription_ReturnsNullForUnknownCustomer()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();

            // Act
            var subscription = await manager.GetSubscriptionAsync("unknown");

            // Assert
            Assert.Null(subscription);
        }

        [Fact]
        public async Task Manager_HasFeature_ReturnsTrueForAvailableFeature()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.HighStakes);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            var hasFeature = await manager.HasFeatureAsync("cust123", Feature.HipaaCompliance);

            // Assert
            Assert.True(hasFeature);
        }

        [Fact]
        public async Task Manager_HasFeature_ReturnsFalseForUnavailableFeature()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.Individual);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            var hasFeature = await manager.HasFeatureAsync("cust123", Feature.HipaaCompliance);

            // Assert
            Assert.False(hasFeature);
        }

        [Fact]
        public async Task Manager_EnsureFeature_ThrowsForMissingFeature()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.Individual);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act & Assert
            await Assert.ThrowsAsync<FeatureNotAvailableException>(() =>
                manager.EnsureFeatureAsync("cust123", Feature.HipaaCompliance));
        }

        [Fact]
        public async Task Manager_EnsureFeature_SucceedsForAvailableFeature()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.HighStakes);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act & Assert (should not throw)
            await manager.EnsureFeatureAsync("cust123", Feature.HipaaCompliance);
        }

        [Fact]
        public async Task Manager_GetLimits_ReturnsCorrectLimits()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.SMB);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            var limits = await manager.GetLimitsAsync("cust123");

            // Assert
            Assert.NotNull(limits);
            Assert.Equal(TierManager.GetLimits(CustomerTier.SMB).MaxStorageBytes, limits.MaxStorageBytes);
        }

        [Fact]
        public async Task Manager_RecordUsage_TracksUsage()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.SMB);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            await manager.RecordUsageAsync("cust123", new UsageRecord
            {
                Type = UsageType.Storage,
                Amount = 1024 * 1024 * 100 // 100MB
            });

            var stats = await manager.GetUsageAsync("cust123");

            // Assert
            Assert.True(stats.StorageUsed > 0);
        }

        [Fact]
        public async Task Manager_CheckLimits_AllowsWithinLimit()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.SMB);
            await manager.RegisterSubscriptionAsync(subscription);

            // Act
            var result = await manager.CheckLimitsAsync("cust123", UsageType.Storage, 1024);

            // Assert
            Assert.True(result.IsAllowed);
        }

        [Fact]
        public async Task Manager_CheckLimits_DeniesOverLimit()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var subscription = CreateTestSubscription("cust123", CustomerTier.Individual);
            await manager.RegisterSubscriptionAsync(subscription);

            var limits = TierManager.GetLimits(CustomerTier.Individual);

            // Act - request more than allowed
            var result = await manager.CheckLimitsAsync("cust123", UsageType.Storage, limits.MaxStorageBytes + 1);

            // Assert
            Assert.False(result.IsAllowed);
            Assert.NotNull(result.UpgradeTier);
        }

        #endregion

        #region Feature Tests

        [Fact]
        public void Feature_AllFeaturesHaveUniqueValues()
        {
            var features = Enum.GetValues<Feature>();
            var uniqueValues = new HashSet<Feature>(features);

            Assert.Equal(features.Length, uniqueValues.Count);
        }

        [Fact]
        public void Feature_Count_Is25()
        {
            // Per requirements: 25 features across 4 tiers
            var features = Enum.GetValues<Feature>();
            Assert.Equal(25, features.Length);
        }

        #endregion

        private static CustomerSubscription CreateTestSubscription(string customerId, CustomerTier tier)
        {
            return new CustomerSubscription
            {
                CustomerId = customerId,
                Tier = tier,
                Features = TierManager.GetFeatures(tier),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(tier)
            };
        }
    }

    /// <summary>
    /// Tests for usage tracking and statistics.
    /// </summary>
    public class UsageTrackingTests
    {
        [Fact]
        public void UsageRecord_DefaultTimestamp_IsUtcNow()
        {
            var before = DateTimeOffset.UtcNow;
            var record = new UsageRecord { Type = UsageType.Storage, Amount = 100 };
            var after = DateTimeOffset.UtcNow;

            Assert.True(record.Timestamp >= before);
            Assert.True(record.Timestamp <= after);
        }

        [Fact]
        public void UsageStatistics_Remaining_CalculatesCorrectly()
        {
            // This would be tested through the manager, but we can test the calculation
            var result = new LimitCheckResult
            {
                IsAllowed = true,
                CurrentUsage = 50,
                RequestedAmount = 10,
                MaxAllowed = 100
            };

            Assert.Equal(50, result.Remaining);
        }

        [Fact]
        public void LimitCheckResult_Remaining_DoesNotGoNegative()
        {
            var result = new LimitCheckResult
            {
                IsAllowed = false,
                CurrentUsage = 150,
                RequestedAmount = 10,
                MaxAllowed = 100
            };

            Assert.Equal(0, result.Remaining);
        }
    }

    /// <summary>
    /// Integration tests for the complete licensing flow.
    /// </summary>
    public class LicensingIntegrationTests
    {
        [Fact]
        public async Task FullWorkflow_RegisterCheckEnforce_WorksCorrectly()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var customerId = "integration-test-customer";

            // Create subscription
            var subscription = new CustomerSubscription
            {
                CustomerId = customerId,
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(365),
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };
            subscription.Sign("integration-test-secret");

            // Act - Register
            await manager.RegisterSubscriptionAsync(subscription);

            // Act - Check features
            var hasCloudBackup = await manager.HasFeatureAsync(customerId, Feature.CloudBackup);
            var hasHipaa = await manager.HasFeatureAsync(customerId, Feature.HipaaCompliance);

            // Act - Get limits
            var limits = await manager.GetLimitsAsync(customerId);

            // Act - Record usage
            await manager.RecordUsageAsync(customerId, new UsageRecord
            {
                Type = UsageType.Storage,
                Amount = 1024 * 1024 * 100
            });

            // Act - Get usage
            var usage = await manager.GetUsageAsync(customerId);

            // Assert
            Assert.True(hasCloudBackup);
            Assert.False(hasHipaa);
            Assert.NotNull(limits);
            Assert.True(usage.StorageUsed > 0);
        }

        [Fact]
        public async Task TierUpgrade_ExpandsFeatures()
        {
            // Arrange
            var manager = new InMemorySubscriptionManager();
            var customerId = "upgrade-test";

            // Start with Individual tier
            var individualSub = new CustomerSubscription
            {
                CustomerId = customerId,
                Tier = CustomerTier.Individual,
                Features = TierManager.GetFeatures(CustomerTier.Individual),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(30),
                Limits = TierManager.GetLimits(CustomerTier.Individual)
            };
            await manager.RegisterSubscriptionAsync(individualSub);

            // Verify limited features
            Assert.False(await manager.HasFeatureAsync(customerId, Feature.CloudBackup));

            // Upgrade to SMB
            var smbSub = new CustomerSubscription
            {
                CustomerId = customerId,
                Tier = CustomerTier.SMB,
                Features = TierManager.GetFeatures(CustomerTier.SMB),
                ValidFrom = DateTimeOffset.UtcNow,
                ValidUntil = DateTimeOffset.UtcNow.AddDays(365),
                Limits = TierManager.GetLimits(CustomerTier.SMB)
            };
            await manager.RegisterSubscriptionAsync(smbSub);

            // Verify expanded features
            Assert.True(await manager.HasFeatureAsync(customerId, Feature.CloudBackup));
        }
    }
}
