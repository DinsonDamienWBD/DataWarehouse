using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Licensing
{
    /// <summary>
    /// Customer tiers representing different subscription levels.
    /// Each tier unlocks progressively more features and higher limits.
    /// </summary>
    public enum CustomerTier
    {
        /// <summary>
        /// Individual users: Personal projects, small datasets, basic features.
        /// Ideal for developers, researchers, or small personal projects.
        /// </summary>
        Individual = 0,

        /// <summary>
        /// Small/Medium Business: Team collaboration, moderate workloads.
        /// Includes shared workspaces, basic compliance, and standard support.
        /// </summary>
        SMB = 1,

        /// <summary>
        /// High-stakes environments: Banks, hospitals, government, legal.
        /// Full compliance features, audit trails, dedicated support, SLA.
        /// </summary>
        HighStakes = 2,

        /// <summary>
        /// Hyperscale deployments: Cloud providers, massive data platforms.
        /// Unlimited scale, custom integrations, 24/7 premium support.
        /// </summary>
        Hyperscale = 3
    }

    /// <summary>
    /// Defines the 25 enterprise features available in DataWarehouse.
    /// Features are progressively unlocked based on customer tier.
    /// </summary>
    [Flags]
    public enum Feature : long
    {
        None = 0,

        // === Core Features (1-5) ===
        /// <summary>Basic storage operations (read/write).</summary>
        BasicStorage = 1L << 0,

        /// <summary>Advanced compression algorithms (Brotli, LZ4, Zstd).</summary>
        AdvancedCompression = 1L << 1,

        /// <summary>AES-256-GCM encryption at rest.</summary>
        EncryptionAtRest = 1L << 2,

        /// <summary>TLS encryption in transit.</summary>
        EncryptionInTransit = 1L << 3,

        /// <summary>Basic search and query capabilities.</summary>
        BasicSearch = 1L << 4,

        // === Collaboration Features (6-10) ===
        /// <summary>Version control for data objects.</summary>
        VersionControl = 1L << 5,

        /// <summary>Real-time synchronization across nodes.</summary>
        RealTimeSync = 1L << 6,

        /// <summary>Multi-user workspace collaboration.</summary>
        TeamCollaboration = 1L << 7,

        /// <summary>Role-based access control (RBAC).</summary>
        RoleBasedAccess = 1L << 8,

        /// <summary>Shared containers and cross-team access.</summary>
        SharedContainers = 1L << 9,

        // === AI Features (11-15) ===
        /// <summary>Basic AI chat completions.</summary>
        AIBasicChat = 1L << 10,

        /// <summary>AI streaming responses.</summary>
        AIStreaming = 1L << 11,

        /// <summary>AI function/tool calling.</summary>
        AIFunctionCalling = 1L << 12,

        /// <summary>AI vision/image analysis.</summary>
        AIVision = 1L << 13,

        /// <summary>AI embeddings and vector search.</summary>
        AIEmbeddings = 1L << 14,

        // === Compliance Features (16-20) ===
        /// <summary>Basic audit logging.</summary>
        BasicAuditLog = 1L << 15,

        /// <summary>Full audit trail with tamper-proof logging.</summary>
        FullAuditTrail = 1L << 16,

        /// <summary>HIPAA compliance features.</summary>
        HIPAACompliance = 1L << 17,

        /// <summary>Financial/SOX/PCI-DSS compliance.</summary>
        FinancialCompliance = 1L << 18,

        /// <summary>GDPR compliance and data residency controls.</summary>
        GDPRCompliance = 1L << 19,

        // === Enterprise Features (21-25) ===
        /// <summary>SSO/SAML/OAuth integration.</summary>
        SSOIntegration = 1L << 20,

        /// <summary>Custom retention policies.</summary>
        CustomRetention = 1L << 21,

        /// <summary>API access and webhooks.</summary>
        APIWebhooks = 1L << 22,

        /// <summary>Multi-region replication.</summary>
        MultiRegionReplication = 1L << 23,

        /// <summary>Unlimited resources and custom integrations.</summary>
        UnlimitedResources = 1L << 24,

        // === Aggregate Feature Sets ===
        /// <summary>All core features.</summary>
        CoreFeatures = BasicStorage | AdvancedCompression | EncryptionAtRest | EncryptionInTransit | BasicSearch,

        /// <summary>All collaboration features.</summary>
        CollaborationFeatures = VersionControl | RealTimeSync | TeamCollaboration | RoleBasedAccess | SharedContainers,

        /// <summary>All AI features.</summary>
        AIFeatures = AIBasicChat | AIStreaming | AIFunctionCalling | AIVision | AIEmbeddings,

        /// <summary>All compliance features.</summary>
        ComplianceFeatures = BasicAuditLog | FullAuditTrail | HIPAACompliance | FinancialCompliance | GDPRCompliance,

        /// <summary>All enterprise features.</summary>
        EnterpriseFeatures = SSOIntegration | CustomRetention | APIWebhooks | MultiRegionReplication | UnlimitedResources,

        /// <summary>All 25 features.</summary>
        All = CoreFeatures | CollaborationFeatures | AIFeatures | ComplianceFeatures | EnterpriseFeatures
    }

    /// <summary>
    /// Defines resource limits for each customer tier.
    /// </summary>
    public sealed class TierLimits
    {
        /// <summary>Maximum storage in bytes.</summary>
        public long MaxStorageBytes { get; init; }

        /// <summary>Maximum number of containers.</summary>
        public int MaxContainers { get; init; }

        /// <summary>Maximum users/seats.</summary>
        public int MaxUsers { get; init; }

        /// <summary>Maximum API requests per day.</summary>
        public int MaxDailyApiRequests { get; init; }

        /// <summary>Maximum AI tokens per day.</summary>
        public long MaxDailyAITokens { get; init; }

        /// <summary>Maximum concurrent operations.</summary>
        public int MaxConcurrentOperations { get; init; }

        /// <summary>Data retention in days.</summary>
        public int RetentionDays { get; init; }

        /// <summary>Version history depth.</summary>
        public int MaxVersionHistory { get; init; }

        /// <summary>Audit log retention in days.</summary>
        public int AuditRetentionDays { get; init; }

        /// <summary>Whether dedicated resources are allocated.</summary>
        public bool DedicatedResources { get; init; }

        /// <summary>SLA uptime percentage (e.g., 99.9).</summary>
        public double? SLAUptimePercent { get; init; }

        /// <summary>Support response time in hours.</summary>
        public int? SupportResponseHours { get; init; }

        /// <summary>Monthly price in USD (null = custom pricing).</summary>
        public decimal? MonthlyPriceUsd { get; init; }

        // Constants for readability
        public const long GB = 1024L * 1024L * 1024L;
        public const long TB = 1024L * GB;
        public const long Unlimited = long.MaxValue;
        public const int UnlimitedInt = int.MaxValue;
    }

    /// <summary>
    /// Manages customer tier definitions, feature access, and limits.
    /// </summary>
    public static class TierManager
    {
        private static readonly Dictionary<CustomerTier, Feature> _tierFeatures = new()
        {
            // Individual: Core + Basic AI + Basic Audit
            [CustomerTier.Individual] = Feature.BasicStorage |
                                        Feature.AdvancedCompression |
                                        Feature.EncryptionAtRest |
                                        Feature.EncryptionInTransit |
                                        Feature.BasicSearch |
                                        Feature.VersionControl |
                                        Feature.AIBasicChat |
                                        Feature.BasicAuditLog,

            // SMB: Individual + Collaboration + AI Streaming + RBAC
            [CustomerTier.SMB] = Feature.CoreFeatures |
                                 Feature.VersionControl |
                                 Feature.RealTimeSync |
                                 Feature.TeamCollaboration |
                                 Feature.RoleBasedAccess |
                                 Feature.SharedContainers |
                                 Feature.AIBasicChat |
                                 Feature.AIStreaming |
                                 Feature.AIEmbeddings |
                                 Feature.BasicAuditLog |
                                 Feature.APIWebhooks,

            // HighStakes: SMB + Full AI + Full Compliance + SSO
            [CustomerTier.HighStakes] = Feature.CoreFeatures |
                                        Feature.CollaborationFeatures |
                                        Feature.AIFeatures |
                                        Feature.ComplianceFeatures |
                                        Feature.SSOIntegration |
                                        Feature.CustomRetention |
                                        Feature.APIWebhooks,

            // Hyperscale: All 25 features
            [CustomerTier.Hyperscale] = Feature.All
        };

        private static readonly Dictionary<CustomerTier, TierLimits> _tierLimits = new()
        {
            [CustomerTier.Individual] = new TierLimits
            {
                MaxStorageBytes = 10 * TierLimits.GB,
                MaxContainers = 5,
                MaxUsers = 1,
                MaxDailyApiRequests = 1_000,
                MaxDailyAITokens = 100_000,
                MaxConcurrentOperations = 5,
                RetentionDays = 30,
                MaxVersionHistory = 10,
                AuditRetentionDays = 7,
                DedicatedResources = false,
                SLAUptimePercent = null,
                SupportResponseHours = 72,
                MonthlyPriceUsd = 0m // Free tier
            },

            [CustomerTier.SMB] = new TierLimits
            {
                MaxStorageBytes = 500 * TierLimits.GB,
                MaxContainers = 50,
                MaxUsers = 25,
                MaxDailyApiRequests = 50_000,
                MaxDailyAITokens = 1_000_000,
                MaxConcurrentOperations = 25,
                RetentionDays = 90,
                MaxVersionHistory = 100,
                AuditRetentionDays = 90,
                DedicatedResources = false,
                SLAUptimePercent = 99.5,
                SupportResponseHours = 24,
                MonthlyPriceUsd = 99m
            },

            [CustomerTier.HighStakes] = new TierLimits
            {
                MaxStorageBytes = 10 * TierLimits.TB,
                MaxContainers = 500,
                MaxUsers = 500,
                MaxDailyApiRequests = 500_000,
                MaxDailyAITokens = 10_000_000,
                MaxConcurrentOperations = 100,
                RetentionDays = 2555, // 7 years
                MaxVersionHistory = 1000,
                AuditRetentionDays = 2555, // 7 years
                DedicatedResources = true,
                SLAUptimePercent = 99.99,
                SupportResponseHours = 4,
                MonthlyPriceUsd = 999m
            },

            [CustomerTier.Hyperscale] = new TierLimits
            {
                MaxStorageBytes = TierLimits.Unlimited,
                MaxContainers = TierLimits.UnlimitedInt,
                MaxUsers = TierLimits.UnlimitedInt,
                MaxDailyApiRequests = TierLimits.UnlimitedInt,
                MaxDailyAITokens = TierLimits.Unlimited,
                MaxConcurrentOperations = TierLimits.UnlimitedInt,
                RetentionDays = TierLimits.UnlimitedInt,
                MaxVersionHistory = TierLimits.UnlimitedInt,
                AuditRetentionDays = TierLimits.UnlimitedInt,
                DedicatedResources = true,
                SLAUptimePercent = 99.999,
                SupportResponseHours = 1,
                MonthlyPriceUsd = null // Custom pricing
            }
        };

        /// <summary>
        /// Gets the features available for a customer tier.
        /// </summary>
        public static Feature GetFeatures(CustomerTier tier)
        {
            return _tierFeatures.TryGetValue(tier, out var features) ? features : Feature.None;
        }

        /// <summary>
        /// Gets the resource limits for a customer tier.
        /// </summary>
        public static TierLimits GetLimits(CustomerTier tier)
        {
            return _tierLimits.TryGetValue(tier, out var limits) ? limits : new TierLimits();
        }

        /// <summary>
        /// Checks if a specific feature is available for a tier.
        /// </summary>
        public static bool HasFeature(CustomerTier tier, Feature feature)
        {
            var tierFeatures = GetFeatures(tier);
            return (tierFeatures & feature) == feature;
        }

        /// <summary>
        /// Gets a list of all features available for a tier.
        /// </summary>
        public static IEnumerable<Feature> GetFeatureList(CustomerTier tier)
        {
            var tierFeatures = GetFeatures(tier);
            return Enum.GetValues<Feature>()
                .Where(f => f != Feature.None && !IsAggregateFeature(f))
                .Where(f => (tierFeatures & f) == f);
        }

        /// <summary>
        /// Gets the minimum tier required for a specific feature.
        /// </summary>
        public static CustomerTier GetMinimumTier(Feature feature)
        {
            foreach (var tier in new[] { CustomerTier.Individual, CustomerTier.SMB, CustomerTier.HighStakes, CustomerTier.Hyperscale })
            {
                if (HasFeature(tier, feature))
                    return tier;
            }
            return CustomerTier.Hyperscale;
        }

        /// <summary>
        /// Gets a human-readable name for a feature.
        /// </summary>
        public static string GetFeatureName(Feature feature)
        {
            return feature switch
            {
                Feature.BasicStorage => "Basic Storage",
                Feature.AdvancedCompression => "Advanced Compression",
                Feature.EncryptionAtRest => "Encryption at Rest",
                Feature.EncryptionInTransit => "Encryption in Transit",
                Feature.BasicSearch => "Basic Search",
                Feature.VersionControl => "Version Control",
                Feature.RealTimeSync => "Real-time Sync",
                Feature.TeamCollaboration => "Team Collaboration",
                Feature.RoleBasedAccess => "Role-Based Access Control",
                Feature.SharedContainers => "Shared Containers",
                Feature.AIBasicChat => "AI Chat",
                Feature.AIStreaming => "AI Streaming",
                Feature.AIFunctionCalling => "AI Function Calling",
                Feature.AIVision => "AI Vision",
                Feature.AIEmbeddings => "AI Embeddings",
                Feature.BasicAuditLog => "Basic Audit Log",
                Feature.FullAuditTrail => "Full Audit Trail",
                Feature.HIPAACompliance => "HIPAA Compliance",
                Feature.FinancialCompliance => "Financial Compliance (SOX/PCI-DSS)",
                Feature.GDPRCompliance => "GDPR Compliance",
                Feature.SSOIntegration => "SSO Integration",
                Feature.CustomRetention => "Custom Retention Policies",
                Feature.APIWebhooks => "API & Webhooks",
                Feature.MultiRegionReplication => "Multi-Region Replication",
                Feature.UnlimitedResources => "Unlimited Resources",
                _ => feature.ToString()
            };
        }

        /// <summary>
        /// Gets a description for a feature.
        /// </summary>
        public static string GetFeatureDescription(Feature feature)
        {
            return feature switch
            {
                Feature.BasicStorage => "Store and retrieve data objects with standard I/O operations",
                Feature.AdvancedCompression => "Brotli, LZ4, Zstd compression algorithms for optimal storage efficiency",
                Feature.EncryptionAtRest => "AES-256-GCM encryption for data stored on disk",
                Feature.EncryptionInTransit => "TLS 1.3 encryption for all network communications",
                Feature.BasicSearch => "Query and filter data objects by metadata and content",
                Feature.VersionControl => "Track changes and maintain version history for all objects",
                Feature.RealTimeSync => "Automatic synchronization across multiple nodes in real-time",
                Feature.TeamCollaboration => "Multi-user workspaces with concurrent editing support",
                Feature.RoleBasedAccess => "Fine-grained permissions with role-based access control",
                Feature.SharedContainers => "Share containers across teams and organizations",
                Feature.AIBasicChat => "Chat completions with AI models (Claude, GPT, etc.)",
                Feature.AIStreaming => "Stream AI responses in real-time for better UX",
                Feature.AIFunctionCalling => "AI-powered function/tool calling for automation",
                Feature.AIVision => "Analyze images and documents with AI vision models",
                Feature.AIEmbeddings => "Generate embeddings for semantic search and RAG",
                Feature.BasicAuditLog => "Log important operations for basic auditing",
                Feature.FullAuditTrail => "Tamper-proof audit trails with full event history",
                Feature.HIPAACompliance => "Healthcare compliance: PHI protection, BAA support",
                Feature.FinancialCompliance => "SOX, PCI-DSS compliance for financial data",
                Feature.GDPRCompliance => "EU data protection: consent, right to erasure, data residency",
                Feature.SSOIntegration => "Single Sign-On with SAML, OAuth2, OIDC support",
                Feature.CustomRetention => "Configure custom data retention and lifecycle policies",
                Feature.APIWebhooks => "RESTful API access and webhook notifications",
                Feature.MultiRegionReplication => "Replicate data across geographic regions for disaster recovery",
                Feature.UnlimitedResources => "No limits on storage, users, or operations",
                _ => "Custom feature"
            };
        }

        /// <summary>
        /// Gets the tier name for display.
        /// </summary>
        public static string GetTierName(CustomerTier tier)
        {
            return tier switch
            {
                CustomerTier.Individual => "Individual",
                CustomerTier.SMB => "Small & Medium Business",
                CustomerTier.HighStakes => "High Stakes Enterprise",
                CustomerTier.Hyperscale => "Hyperscale",
                _ => tier.ToString()
            };
        }

        /// <summary>
        /// Gets a description for the tier.
        /// </summary>
        public static string GetTierDescription(CustomerTier tier)
        {
            return tier switch
            {
                CustomerTier.Individual => "Perfect for individual developers, researchers, and personal projects. Get started with essential features at no cost.",
                CustomerTier.SMB => "Built for growing teams. Includes collaboration tools, enhanced AI capabilities, and standard support.",
                CustomerTier.HighStakes => "Enterprise-grade security and compliance for banks, hospitals, government, and regulated industries. Dedicated support with SLA guarantees.",
                CustomerTier.Hyperscale => "Unlimited scale for the largest deployments. Custom integrations, 24/7 premium support, and the highest SLA.",
                _ => string.Empty
            };
        }

        private static bool IsAggregateFeature(Feature feature)
        {
            return feature is Feature.CoreFeatures or Feature.CollaborationFeatures or Feature.AIFeatures or Feature.ComplianceFeatures or Feature.EnterpriseFeatures or Feature.All;
        }

        /// <summary>
        /// Generates a comparison matrix of all tiers and features.
        /// </summary>
        public static Dictionary<Feature, Dictionary<CustomerTier, bool>> GetFeatureMatrix()
        {
            var matrix = new Dictionary<Feature, Dictionary<CustomerTier, bool>>();
            var tiers = Enum.GetValues<CustomerTier>();
            var features = Enum.GetValues<Feature>()
                .Where(f => f != Feature.None && !IsAggregateFeature(f));

            foreach (var feature in features)
            {
                matrix[feature] = new Dictionary<CustomerTier, bool>();
                foreach (var tier in tiers)
                {
                    matrix[feature][tier] = HasFeature(tier, feature);
                }
            }

            return matrix;
        }
    }

    /// <summary>
    /// Represents a customer's current subscription state.
    /// </summary>
    public sealed class CustomerSubscription
    {
        /// <summary>Unique customer identifier.</summary>
        public required string CustomerId { get; init; }

        /// <summary>Customer's organization name.</summary>
        public string? OrganizationName { get; init; }

        /// <summary>Current subscription tier.</summary>
        public CustomerTier Tier { get; set; }

        /// <summary>When the subscription started.</summary>
        public DateTimeOffset SubscriptionStart { get; init; }

        /// <summary>When the current billing period ends.</summary>
        public DateTimeOffset? BillingPeriodEnd { get; set; }

        /// <summary>Whether the subscription is currently active.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Custom feature overrides (for enterprise negotiations).</summary>
        public Feature? FeatureOverrides { get; set; }

        /// <summary>Custom limit overrides (for enterprise negotiations).</summary>
        public TierLimits? LimitOverrides { get; set; }

        /// <summary>
        /// Gets the effective features for this subscription.
        /// </summary>
        public Feature GetEffectiveFeatures()
        {
            var baseFeatures = TierManager.GetFeatures(Tier);
            return FeatureOverrides.HasValue
                ? baseFeatures | FeatureOverrides.Value
                : baseFeatures;
        }

        /// <summary>
        /// Gets the effective limits for this subscription.
        /// </summary>
        public TierLimits GetEffectiveLimits()
        {
            return LimitOverrides ?? TierManager.GetLimits(Tier);
        }

        /// <summary>
        /// Checks if a feature is available for this subscription.
        /// </summary>
        public bool HasFeature(Feature feature)
        {
            if (!IsActive) return false;
            var effectiveFeatures = GetEffectiveFeatures();
            return (effectiveFeatures & feature) == feature;
        }
    }

    /// <summary>
    /// Exception thrown when a feature is not available for the current tier.
    /// </summary>
    public class FeatureNotAvailableException : Exception
    {
        public Feature RequestedFeature { get; }
        public CustomerTier CurrentTier { get; }
        public CustomerTier RequiredTier { get; }

        public FeatureNotAvailableException(Feature feature, CustomerTier currentTier)
            : base($"Feature '{TierManager.GetFeatureName(feature)}' requires {TierManager.GetTierName(TierManager.GetMinimumTier(feature))} tier or higher. Current tier: {TierManager.GetTierName(currentTier)}")
        {
            RequestedFeature = feature;
            CurrentTier = currentTier;
            RequiredTier = TierManager.GetMinimumTier(feature);
        }
    }

    /// <summary>
    /// Exception thrown when a resource limit is exceeded.
    /// </summary>
    public class LimitExceededException : Exception
    {
        public string LimitName { get; }
        public long CurrentValue { get; }
        public long MaxValue { get; }
        public CustomerTier CurrentTier { get; }

        public LimitExceededException(string limitName, long currentValue, long maxValue, CustomerTier tier)
            : base($"Limit exceeded: {limitName}. Current: {currentValue}, Max: {maxValue} ({TierManager.GetTierName(tier)} tier)")
        {
            LimitName = limitName;
            CurrentValue = currentValue;
            MaxValue = maxValue;
            CurrentTier = tier;
        }
    }
}
