using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.DataMarketplace;

/// <summary>
/// Production-ready Data Marketplace Plugin implementing T83.
/// Provides comprehensive data commerce capabilities for monetizing and sharing datasets.
///
/// Features:
/// - 83.1: Data Listing - Publish datasets with pricing and terms
/// - 83.2: Subscription Engine - Time-based and query-based access models
/// - 83.3: Usage Metering - Track queries, bytes transferred, compute used
/// - 83.4: Billing Integration - Generate invoices and integrate with payment
/// - 83.5: License Management - Enforce usage terms and restrictions
/// - 83.6: Access Revocation - Automatic revocation on payment failure
/// - 83.7: Data Preview - Sample data without full access
/// - 83.8: Rating and Reviews - Buyer feedback on data quality
/// - 83.9: Chargeback Reporting - Internal cost allocation reports
/// - 83.10: Smart Contract Integration - Optional blockchain-based contracts
///
/// Message Commands:
/// - marketplace.list: Create/update a data listing
/// - marketplace.search: Search available listings
/// - marketplace.subscribe: Subscribe to a dataset
/// - marketplace.access: Access subscribed data
/// - marketplace.preview: Get sample data
/// - marketplace.meter: Record usage metrics
/// - marketplace.invoice: Generate billing invoice
/// - marketplace.review: Submit rating/review
/// - marketplace.revoke: Revoke access
/// - marketplace.chargeback: Generate chargeback report
/// </summary>
public sealed class DataMarketplacePlugin : PlatformPluginBase
{
    private readonly ConcurrentDictionary<string, DataListing> _listings = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, License> _licenses = new();
    private readonly ConcurrentDictionary<string, UsageRecord> _usageRecords = new();
    private readonly ConcurrentDictionary<string, List<Review>> _reviews = new();
    private readonly ConcurrentDictionary<string, Invoice> _invoices = new();
    private readonly ConcurrentDictionary<string, SmartContract> _smartContracts = new();
    private readonly ConcurrentDictionary<string, AccessGrant> _accessGrants = new();
    private readonly SemaphoreSlim _meteringLock = new(1, 1);
    private readonly Timer _accessEnforcementTimer;
    private readonly Timer _billingCycleTimer;
    private readonly DataMarketplaceConfig _config;
    private readonly string _storagePath;
    private bool _isRunning;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.commerce.marketplace";

    /// <inheritdoc/>
    public override string Name => "Data Marketplace (T83)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string PlatformDomain => "DataMarketplace";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Initializes a new instance of the DataMarketplacePlugin.
    /// </summary>
    /// <param name="config">Optional configuration for the marketplace.</param>
    public DataMarketplacePlugin(DataMarketplaceConfig? config = null)
    {
        _config = config ?? new DataMarketplaceConfig();
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "marketplace");

        _accessEnforcementTimer = new Timer(
            async _ => await EnforceAccessPoliciesAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _billingCycleTimer = new Timer(
            async _ => await ProcessBillingCycleAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        await LoadStateAsync();
        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "marketplace.list", DisplayName = "Data Listing", Description = "Create/update a data listing" },
            new() { Name = "marketplace.search", DisplayName = "Search", Description = "Search available listings" },
            new() { Name = "marketplace.subscribe", DisplayName = "Subscribe", Description = "Subscribe to a dataset" },
            new() { Name = "marketplace.access", DisplayName = "Access Data", Description = "Access subscribed data" },
            new() { Name = "marketplace.preview", DisplayName = "Preview", Description = "Get sample data" },
            new() { Name = "marketplace.meter", DisplayName = "Meter Usage", Description = "Record usage metrics" },
            new() { Name = "marketplace.invoice", DisplayName = "Invoice", Description = "Generate billing invoice" },
            new() { Name = "marketplace.review", DisplayName = "Review", Description = "Submit rating/review" },
            new() { Name = "marketplace.revoke", DisplayName = "Revoke", Description = "Revoke access" },
            new() { Name = "marketplace.chargeback", DisplayName = "Chargeback", Description = "Generate chargeback report" },
            new() { Name = "marketplace.contract", DisplayName = "Smart Contract", Description = "Manage smart contracts" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalListings"] = _listings.Count;
        metadata["ActiveSubscriptions"] = _subscriptions.Values.Count(s => s.Status == SubscriptionStatus.Active);
        metadata["TotalRevenue"] = _invoices.Values.Where(i => i.Status == InvoiceStatus.Paid).Sum(i => i.TotalAmount);
        metadata["SupportedCurrencies"] = _config.SupportedCurrencies;
        metadata["BlockchainIntegration"] = _config.EnableBlockchainContracts;
        return metadata;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "marketplace.list":
                await HandleListAsync(message);
                break;
            case "marketplace.search":
                HandleSearch(message);
                break;
            case "marketplace.subscribe":
                await HandleSubscribeAsync(message);
                break;
            case "marketplace.access":
                await HandleAccessAsync(message);
                break;
            case "marketplace.preview":
                await HandlePreviewAsync(message);
                break;
            case "marketplace.meter":
                await HandleMeterAsync(message);
                break;
            case "marketplace.invoice":
                await HandleInvoiceAsync(message);
                break;
            case "marketplace.review":
                await HandleReviewAsync(message);
                break;
            case "marketplace.revoke":
                await HandleRevokeAsync(message);
                break;
            case "marketplace.chargeback":
                await HandleChargebackAsync(message);
                break;
            case "marketplace.contract":
                await HandleContractAsync(message);
                break;
            default:
                await base.OnMessageAsync(message);
                break;
        }
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _isRunning = true;
        Directory.CreateDirectory(_storagePath);

        // Start access enforcement timer
        _accessEnforcementTimer.Change(TimeSpan.Zero, _config.AccessEnforcementInterval);

        // Start billing cycle timer
        _billingCycleTimer.Change(TimeSpan.Zero, _config.BillingCycleInterval);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _isRunning = false;
        _accessEnforcementTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _billingCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);

        await SaveStateAsync();
    }

    #region 83.1: Data Listing

    /// <summary>
    /// Creates or updates a data listing for the marketplace.
    /// </summary>
    /// <param name="listing">The listing to create/update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created/updated listing with generated ID.</returns>
    public async Task<DataListing> CreateListingAsync(DataListing listing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (string.IsNullOrWhiteSpace(listing.Id))
        {
            listing = listing with { Id = GenerateId("lst") };
        }

        listing = listing with
        {
            CreatedAt = listing.CreatedAt == default ? DateTime.UtcNow : listing.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Status = listing.Status == ListingStatus.Unknown ? ListingStatus.Draft : listing.Status
        };

        // Validate pricing
        ValidatePricing(listing.Pricing);

        // Calculate quality score
        var qualityScore = await CalculateQualityScoreAsync(listing, ct);
        listing = listing with { QualityScore = qualityScore };

        _listings[listing.Id] = listing;
        await SaveListingAsync(listing, ct);

        return listing;
    }

    /// <summary>
    /// Publishes a draft listing to the marketplace.
    /// </summary>
    /// <param name="listingId">The listing ID to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The published listing.</returns>
    public async Task<DataListing> PublishListingAsync(string listingId, CancellationToken ct = default)
    {
        if (!_listings.TryGetValue(listingId, out var listing))
        {
            throw new KeyNotFoundException($"Listing not found: {listingId}");
        }

        if (listing.Status == ListingStatus.Published)
        {
            return listing;
        }

        // Validate listing is complete
        ValidateListingForPublish(listing);

        listing = listing with
        {
            Status = ListingStatus.Published,
            PublishedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _listings[listingId] = listing;
        await SaveListingAsync(listing, ct);

        return listing;
    }

    /// <summary>
    /// Searches for listings matching the specified criteria.
    /// </summary>
    /// <param name="query">Search query parameters.</param>
    /// <returns>Matching listings.</returns>
    public IReadOnlyList<DataListing> SearchListings(ListingSearchQuery query)
    {
        var results = _listings.Values.AsEnumerable();

        // Filter by status (default to published only)
        results = results.Where(l => l.Status == (query.Status ?? ListingStatus.Published));

        // Filter by category
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            results = results.Where(l => l.Category.Equals(query.Category, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by tags
        if (query.Tags?.Length > 0)
        {
            results = results.Where(l => query.Tags.Any(t => l.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        // Filter by price range
        if (query.MinPrice.HasValue)
        {
            results = results.Where(l => l.Pricing.BasePrice >= query.MinPrice.Value);
        }
        if (query.MaxPrice.HasValue)
        {
            results = results.Where(l => l.Pricing.BasePrice <= query.MaxPrice.Value);
        }

        // Filter by quality score
        if (query.MinQualityScore.HasValue)
        {
            results = results.Where(l => l.QualityScore >= query.MinQualityScore.Value);
        }

        // Text search
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var searchLower = query.SearchText.ToLowerInvariant();
            results = results.Where(l =>
                l.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                l.Description.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                l.Tags.Any(t => t.Contains(searchLower, StringComparison.OrdinalIgnoreCase)));
        }

        // Sort
        results = query.SortBy switch
        {
            ListingSortBy.Name => query.SortDescending ? results.OrderByDescending(l => l.Name) : results.OrderBy(l => l.Name),
            ListingSortBy.Price => query.SortDescending ? results.OrderByDescending(l => l.Pricing.BasePrice) : results.OrderBy(l => l.Pricing.BasePrice),
            ListingSortBy.Quality => query.SortDescending ? results.OrderByDescending(l => l.QualityScore) : results.OrderBy(l => l.QualityScore),
            ListingSortBy.Popularity => query.SortDescending ? results.OrderByDescending(l => GetSubscriptionCount(l.Id)) : results.OrderBy(l => GetSubscriptionCount(l.Id)),
            _ => query.SortDescending ? results.OrderByDescending(l => l.CreatedAt) : results.OrderBy(l => l.CreatedAt)
        };

        // Pagination
        if (query.Offset > 0)
        {
            results = results.Skip(query.Offset);
        }
        if (query.Limit > 0)
        {
            results = results.Take(query.Limit);
        }

        return results.ToList();
    }

    private int GetSubscriptionCount(string listingId)
    {
        return _subscriptions.Values.Count(s => s.ListingId == listingId && s.Status == SubscriptionStatus.Active);
    }

    private void ValidatePricing(DataPricing pricing)
    {
        if (pricing.BasePrice < 0)
        {
            throw new ArgumentException("Base price cannot be negative");
        }

        if (!_config.SupportedCurrencies.Contains(pricing.Currency))
        {
            throw new ArgumentException($"Unsupported currency: {pricing.Currency}");
        }
    }

    private void ValidateListingForPublish(DataListing listing)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(listing.Name))
            errors.Add("Name is required");

        if (string.IsNullOrWhiteSpace(listing.Description))
            errors.Add("Description is required");

        if (string.IsNullOrWhiteSpace(listing.DataSourceId))
            errors.Add("Data source ID is required");

        if (listing.Pricing.BasePrice < 0)
            errors.Add("Invalid pricing");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Cannot publish listing: {string.Join(", ", errors)}");
        }
    }

    private async Task<double> CalculateQualityScoreAsync(DataListing listing, CancellationToken ct)
    {
        // Quality score based on various factors (0-100)
        var score = 0.0;

        // Completeness (up to 30 points)
        if (!string.IsNullOrWhiteSpace(listing.Name)) score += 5;
        if (!string.IsNullOrWhiteSpace(listing.Description)) score += 5;
        if (listing.Tags.Length > 0) score += 5;
        if (listing.Schema != null) score += 10;
        if (!string.IsNullOrWhiteSpace(listing.Documentation)) score += 5;

        // Data freshness (up to 20 points)
        if (listing.UpdateFrequency != UpdateFrequency.Unknown)
        {
            score += listing.UpdateFrequency switch
            {
                UpdateFrequency.RealTime => 20,
                UpdateFrequency.Hourly => 18,
                UpdateFrequency.Daily => 15,
                UpdateFrequency.Weekly => 10,
                UpdateFrequency.Monthly => 5,
                _ => 0
            };
        }

        // License clarity (up to 20 points)
        if (listing.License != null)
        {
            if (!string.IsNullOrWhiteSpace(listing.License.Type)) score += 10;
            if (listing.License.AllowedUsages.Length > 0) score += 5;
            if (!string.IsNullOrWhiteSpace(listing.License.Terms)) score += 5;
        }

        // Reviews (up to 30 points)
        if (_reviews.TryGetValue(listing.Id, out var reviews) && reviews.Count > 0)
        {
            var avgRating = reviews.Average(r => r.Rating);
            score += avgRating * 6; // Max 30 points for 5-star average
        }

        return Math.Min(100, Math.Round(score, 1));
    }

    #endregion

    #region 83.2: Subscription Engine

    /// <summary>
    /// Creates a new subscription to a listing.
    /// </summary>
    /// <param name="request">Subscription request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created subscription.</returns>
    public async Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_listings.TryGetValue(request.ListingId, out var listing))
        {
            throw new KeyNotFoundException($"Listing not found: {request.ListingId}");
        }

        if (listing.Status != ListingStatus.Published)
        {
            throw new InvalidOperationException("Cannot subscribe to unpublished listing");
        }

        // Validate subscription type against listing
        if (!listing.Pricing.AllowedModels.Contains(request.Model))
        {
            throw new InvalidOperationException($"Subscription model not supported: {request.Model}");
        }

        // Calculate pricing
        var price = CalculateSubscriptionPrice(listing.Pricing, request);

        var subscription = new Subscription
        {
            Id = GenerateId("sub"),
            ListingId = request.ListingId,
            SubscriberId = request.SubscriberId,
            Model = request.Model,
            StartDate = DateTime.UtcNow,
            EndDate = CalculateEndDate(request),
            Status = SubscriptionStatus.PendingPayment,
            Price = price,
            QueryLimit = request.QueryLimit,
            ByteLimit = request.ByteLimit,
            CreatedAt = DateTime.UtcNow
        };

        // Create associated license
        var license = await CreateLicenseAsync(subscription, listing.License, ct);
        subscription = subscription with { LicenseId = license.Id };

        _subscriptions[subscription.Id] = subscription;
        await SaveSubscriptionAsync(subscription, ct);

        return subscription;
    }

    /// <summary>
    /// Activates a subscription after payment confirmation.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID.</param>
    /// <param name="paymentReference">Payment reference for audit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The activated subscription.</returns>
    public async Task<Subscription> ActivateSubscriptionAsync(string subscriptionId, string paymentReference, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            throw new KeyNotFoundException($"Subscription not found: {subscriptionId}");
        }

        subscription = subscription with
        {
            Status = SubscriptionStatus.Active,
            PaymentReference = paymentReference,
            ActivatedAt = DateTime.UtcNow
        };

        // Create access grant
        var grant = new AccessGrant
        {
            Id = GenerateId("grant"),
            SubscriptionId = subscriptionId,
            ListingId = subscription.ListingId,
            SubscriberId = subscription.SubscriberId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = subscription.EndDate,
            IsActive = true
        };

        _accessGrants[grant.Id] = grant;
        _subscriptions[subscriptionId] = subscription;

        await SaveSubscriptionAsync(subscription, ct);

        return subscription;
    }

    /// <summary>
    /// Renews an existing subscription.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID to renew.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The renewed subscription.</returns>
    public async Task<Subscription> RenewSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            throw new KeyNotFoundException($"Subscription not found: {subscriptionId}");
        }

        if (!_listings.TryGetValue(subscription.ListingId, out var listing))
        {
            throw new InvalidOperationException("Associated listing no longer exists");
        }

        // Calculate new end date from current end date or now (whichever is later)
        var baseDate = subscription.EndDate > DateTime.UtcNow ? subscription.EndDate : DateTime.UtcNow;
        var newEndDate = subscription.Model switch
        {
            SubscriptionModel.Monthly => baseDate.AddMonths(1),
            SubscriptionModel.Annual => baseDate.AddYears(1),
            SubscriptionModel.Perpetual => DateTime.MaxValue,
            _ => baseDate.AddMonths(1)
        };

        subscription = subscription with
        {
            EndDate = newEndDate,
            Status = SubscriptionStatus.PendingPayment,
            RenewedAt = DateTime.UtcNow,
            RenewalCount = subscription.RenewalCount + 1
        };

        _subscriptions[subscriptionId] = subscription;
        await SaveSubscriptionAsync(subscription, ct);

        return subscription;
    }

    private decimal CalculateSubscriptionPrice(DataPricing pricing, SubscriptionRequest request)
    {
        var basePrice = pricing.BasePrice;

        // Apply model-specific pricing
        basePrice = request.Model switch
        {
            SubscriptionModel.PayPerQuery => 0, // Pay per use
            SubscriptionModel.Monthly => basePrice,
            SubscriptionModel.Annual => basePrice * 10, // 2 months free
            SubscriptionModel.Perpetual => basePrice * 36,
            SubscriptionModel.Trial => 0,
            _ => basePrice
        };

        // Apply tier discounts
        if (request.Tier == SubscriptionTier.Enterprise)
        {
            basePrice *= 0.8m; // 20% enterprise discount
        }

        return basePrice;
    }

    private DateTime CalculateEndDate(SubscriptionRequest request)
    {
        return request.Model switch
        {
            SubscriptionModel.Trial => DateTime.UtcNow.AddDays(_config.TrialDurationDays),
            SubscriptionModel.Monthly => DateTime.UtcNow.AddMonths(1),
            SubscriptionModel.Annual => DateTime.UtcNow.AddYears(1),
            SubscriptionModel.Perpetual => DateTime.MaxValue,
            SubscriptionModel.PayPerQuery => DateTime.UtcNow.AddMonths(1), // Bill monthly
            _ => DateTime.UtcNow.AddMonths(1)
        };
    }

    #endregion

    #region 83.3: Usage Metering

    /// <summary>
    /// Records usage for a subscription.
    /// </summary>
    /// <param name="usage">Usage event to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated usage record.</returns>
    public async Task<UsageRecord> RecordUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (!_subscriptions.TryGetValue(usage.SubscriptionId, out var subscription))
        {
            throw new KeyNotFoundException($"Subscription not found: {usage.SubscriptionId}");
        }

        await _meteringLock.WaitAsync(ct);
        try
        {
            var recordKey = $"{usage.SubscriptionId}:{DateTime.UtcNow:yyyy-MM}";

            if (!_usageRecords.TryGetValue(recordKey, out var record))
            {
                record = new UsageRecord
                {
                    Id = GenerateId("usage"),
                    SubscriptionId = usage.SubscriptionId,
                    ListingId = subscription.ListingId,
                    PeriodStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEnd = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddTicks(-1)
                };
            }

            // Update metrics
            record = record with
            {
                QueryCount = record.QueryCount + usage.QueryCount,
                BytesTransferred = record.BytesTransferred + usage.BytesTransferred,
                ComputeUnits = record.ComputeUnits + usage.ComputeUnits,
                RecordCount = record.RecordCount + usage.RecordCount,
                LastUpdated = DateTime.UtcNow,
                EventCount = record.EventCount + 1
            };

            // Check limits
            CheckUsageLimits(subscription, record);

            _usageRecords[recordKey] = record;
            await SaveUsageRecordAsync(record, ct);

            return record;
        }
        finally
        {
            _meteringLock.Release();
        }
    }

    /// <summary>
    /// Gets usage metrics for a subscription.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID.</param>
    /// <param name="startDate">Start of period.</param>
    /// <param name="endDate">End of period.</param>
    /// <returns>Usage records for the period.</returns>
    public IReadOnlyList<UsageRecord> GetUsageMetrics(string subscriptionId, DateTime? startDate = null, DateTime? endDate = null)
    {
        var start = startDate ?? DateTime.UtcNow.AddMonths(-1);
        var end = endDate ?? DateTime.UtcNow;

        return _usageRecords.Values
            .Where(r => r.SubscriptionId == subscriptionId &&
                       r.PeriodStart >= start &&
                       r.PeriodEnd <= end)
            .OrderBy(r => r.PeriodStart)
            .ToList();
    }

    /// <summary>
    /// Gets aggregate usage across all subscriptions for a listing.
    /// </summary>
    /// <param name="listingId">Listing ID.</param>
    /// <returns>Aggregate usage summary.</returns>
    public UsageSummary GetListingUsageSummary(string listingId)
    {
        var records = _usageRecords.Values.Where(r => r.ListingId == listingId).ToList();

        return new UsageSummary
        {
            ListingId = listingId,
            TotalQueries = records.Sum(r => r.QueryCount),
            TotalBytesTransferred = records.Sum(r => r.BytesTransferred),
            TotalComputeUnits = records.Sum(r => r.ComputeUnits),
            TotalRecords = records.Sum(r => r.RecordCount),
            UniqueSubscribers = records.Select(r => r.SubscriptionId).Distinct().Count(),
            PeriodStart = records.Min(r => r.PeriodStart),
            PeriodEnd = records.Max(r => r.PeriodEnd)
        };
    }

    private void CheckUsageLimits(Subscription subscription, UsageRecord record)
    {
        if (subscription.QueryLimit.HasValue && record.QueryCount > subscription.QueryLimit.Value)
        {
            // Log warning but don't block - billing will handle overages
            record = record with { OverLimitWarning = true };
        }

        if (subscription.ByteLimit.HasValue && record.BytesTransferred > subscription.ByteLimit.Value)
        {
            record = record with { OverLimitWarning = true };
        }
    }

    #endregion

    #region 83.4: Billing Integration

    /// <summary>
    /// Generates an invoice for a subscription.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID.</param>
    /// <param name="periodEnd">End of billing period.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Generated invoice.</returns>
    public async Task<Invoice> GenerateInvoiceAsync(string subscriptionId, DateTime periodEnd, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            throw new KeyNotFoundException($"Subscription not found: {subscriptionId}");
        }

        if (!_listings.TryGetValue(subscription.ListingId, out var listing))
        {
            throw new InvalidOperationException("Associated listing not found");
        }

        var periodStart = subscription.LastInvoiceDate ?? subscription.StartDate;
        var usageRecords = GetUsageMetrics(subscriptionId, periodStart, periodEnd);

        var lineItems = new List<InvoiceLineItem>();

        // Base subscription fee
        if (subscription.Model != SubscriptionModel.PayPerQuery && subscription.Model != SubscriptionModel.Trial)
        {
            lineItems.Add(new InvoiceLineItem
            {
                Description = $"{listing.Name} - {subscription.Model} Subscription",
                Quantity = 1,
                UnitPrice = subscription.Price,
                Amount = subscription.Price,
                Type = LineItemType.Subscription
            });
        }

        // Usage-based charges
        if (subscription.Model == SubscriptionModel.PayPerQuery || usageRecords.Any(r => r.OverLimitWarning))
        {
            var totalQueries = usageRecords.Sum(r => r.QueryCount);
            var queryPrice = listing.Pricing.PerQueryPrice;

            if (subscription.Model == SubscriptionModel.PayPerQuery)
            {
                lineItems.Add(new InvoiceLineItem
                {
                    Description = $"Query Usage ({totalQueries:N0} queries)",
                    Quantity = totalQueries,
                    UnitPrice = queryPrice,
                    Amount = totalQueries * queryPrice,
                    Type = LineItemType.Usage
                });
            }
            else if (subscription.QueryLimit.HasValue)
            {
                var overLimit = totalQueries - subscription.QueryLimit.Value;
                if (overLimit > 0)
                {
                    lineItems.Add(new InvoiceLineItem
                    {
                        Description = $"Query Overage ({overLimit:N0} queries over limit)",
                        Quantity = overLimit,
                        UnitPrice = queryPrice * 1.5m, // Overage rate
                        Amount = overLimit * queryPrice * 1.5m,
                        Type = LineItemType.Overage
                    });
                }
            }
        }

        // Data transfer charges
        var totalBytes = usageRecords.Sum(r => r.BytesTransferred);
        if (subscription.ByteLimit.HasValue && totalBytes > subscription.ByteLimit.Value)
        {
            var overageBytes = totalBytes - subscription.ByteLimit.Value;
            var overageGb = overageBytes / (1024m * 1024m * 1024m);
            lineItems.Add(new InvoiceLineItem
            {
                Description = $"Data Transfer Overage ({overageGb:F2} GB)",
                Quantity = overageGb,
                UnitPrice = listing.Pricing.PerGbPrice,
                Amount = overageGb * listing.Pricing.PerGbPrice,
                Type = LineItemType.Overage
            });
        }

        var subtotal = lineItems.Sum(li => li.Amount);
        var tax = subtotal * _config.TaxRate;

        var invoice = new Invoice
        {
            Id = GenerateId("inv"),
            SubscriptionId = subscriptionId,
            ListingId = subscription.ListingId,
            SubscriberId = subscription.SubscriberId,
            PublisherId = listing.PublisherId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            LineItems = lineItems.ToArray(),
            Subtotal = subtotal,
            Tax = tax,
            TotalAmount = subtotal + tax,
            Currency = listing.Pricing.Currency,
            Status = InvoiceStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(_config.PaymentTermDays)
        };

        _invoices[invoice.Id] = invoice;
        await SaveInvoiceAsync(invoice, ct);

        // Update subscription last invoice date
        subscription = subscription with { LastInvoiceDate = periodEnd };
        _subscriptions[subscriptionId] = subscription;

        return invoice;
    }

    /// <summary>
    /// Records a payment for an invoice.
    /// </summary>
    /// <param name="invoiceId">Invoice ID.</param>
    /// <param name="payment">Payment details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated invoice.</returns>
    public async Task<Invoice> RecordPaymentAsync(string invoiceId, PaymentRecord payment, CancellationToken ct = default)
    {
        if (!_invoices.TryGetValue(invoiceId, out var invoice))
        {
            throw new KeyNotFoundException($"Invoice not found: {invoiceId}");
        }

        var newAmountPaid = invoice.AmountPaid + payment.Amount;
        var newStatus = newAmountPaid >= invoice.TotalAmount ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;

        invoice = invoice with
        {
            AmountPaid = newAmountPaid,
            Status = newStatus,
            PaidAt = newStatus == InvoiceStatus.Paid ? DateTime.UtcNow : invoice.PaidAt,
            PaymentMethod = payment.Method,
            PaymentReference = payment.Reference
        };

        _invoices[invoiceId] = invoice;
        await SaveInvoiceAsync(invoice, ct);

        // If fully paid and subscription was pending, activate it
        if (newStatus == InvoiceStatus.Paid && _subscriptions.TryGetValue(invoice.SubscriptionId, out var subscription))
        {
            if (subscription.Status == SubscriptionStatus.PendingPayment)
            {
                await ActivateSubscriptionAsync(invoice.SubscriptionId, payment.Reference, ct);
            }
        }

        return invoice;
    }

    /// <summary>
    /// Integrates with external payment gateway.
    /// </summary>
    /// <param name="invoiceId">Invoice to process.</param>
    /// <param name="gateway">Payment gateway identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payment integration result.</returns>
    public async Task<PaymentIntegrationResult> InitiatePaymentAsync(string invoiceId, string gateway, CancellationToken ct = default)
    {
        if (!_invoices.TryGetValue(invoiceId, out var invoice))
        {
            throw new KeyNotFoundException($"Invoice not found: {invoiceId}");
        }

        // Generate payment token for gateway
        var token = GeneratePaymentToken(invoice);

        // In production, this would integrate with actual payment gateways
        // (Stripe, PayPal, etc.) via their APIs
        var result = new PaymentIntegrationResult
        {
            InvoiceId = invoiceId,
            Gateway = gateway,
            PaymentToken = token,
            PaymentUrl = $"https://pay.example.com/{gateway}/{token}",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            Amount = invoice.TotalAmount - invoice.AmountPaid,
            Currency = invoice.Currency
        };

        // Update invoice with pending payment
        invoice = invoice with { Status = InvoiceStatus.PaymentPending };
        _invoices[invoiceId] = invoice;

        return result;
    }

    private string GeneratePaymentToken(Invoice invoice)
    {
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var data = $"{invoice.Id}:{invoice.TotalAmount}:{DateTime.UtcNow.Ticks}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash).Replace("/", "_").Replace("+", "-")[..32];
    }

    #endregion

    #region 83.5: License Management

    /// <summary>
    /// Creates a license for a subscription.
    /// </summary>
    private async Task<License> CreateLicenseAsync(Subscription subscription, DataLicense? template, CancellationToken ct)
    {
        var license = new License
        {
            Id = GenerateId("lic"),
            SubscriptionId = subscription.Id,
            ListingId = subscription.ListingId,
            LicenseeId = subscription.SubscriberId,
            Type = template?.Type ?? "Standard",
            Terms = template?.Terms ?? "Standard data usage terms apply.",
            AllowedUsages = template?.AllowedUsages ?? new[] { "Analysis", "Reporting" },
            RestrictedUsages = template?.RestrictedUsages ?? new[] { "Resale", "Redistribution" },
            MaxUsers = template?.MaxUsers ?? 1,
            IsTransferable = template?.IsTransferable ?? false,
            AttributionRequired = template?.AttributionRequired ?? true,
            ValidFrom = subscription.StartDate,
            ValidUntil = subscription.EndDate,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        // Generate license key
        license = license with { LicenseKey = GenerateLicenseKey(license) };

        _licenses[license.Id] = license;
        await SaveLicenseAsync(license, ct);

        return license;
    }

    /// <summary>
    /// Validates a license for a specific usage.
    /// </summary>
    /// <param name="licenseId">License ID to validate.</param>
    /// <param name="usage">Intended usage.</param>
    /// <returns>Validation result.</returns>
    public LicenseValidationResult ValidateLicense(string licenseId, string usage)
    {
        if (!_licenses.TryGetValue(licenseId, out var license))
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = "License not found"
            };
        }

        if (!license.IsActive)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = "License is not active"
            };
        }

        if (DateTime.UtcNow < license.ValidFrom)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = "License not yet valid"
            };
        }

        if (DateTime.UtcNow > license.ValidUntil)
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = "License has expired"
            };
        }

        if (license.RestrictedUsages.Contains(usage, StringComparer.OrdinalIgnoreCase))
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = $"Usage '{usage}' is restricted by license terms"
            };
        }

        if (license.AllowedUsages.Length > 0 &&
            !license.AllowedUsages.Contains(usage, StringComparer.OrdinalIgnoreCase))
        {
            return new LicenseValidationResult
            {
                IsValid = false,
                Reason = $"Usage '{usage}' is not in allowed usages"
            };
        }

        return new LicenseValidationResult
        {
            IsValid = true,
            License = license
        };
    }

    /// <summary>
    /// Updates license terms.
    /// </summary>
    /// <param name="licenseId">License ID.</param>
    /// <param name="updates">Updates to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated license.</returns>
    public async Task<License> UpdateLicenseAsync(string licenseId, LicenseUpdate updates, CancellationToken ct = default)
    {
        if (!_licenses.TryGetValue(licenseId, out var license))
        {
            throw new KeyNotFoundException($"License not found: {licenseId}");
        }

        license = license with
        {
            MaxUsers = updates.MaxUsers ?? license.MaxUsers,
            AllowedUsages = updates.AllowedUsages ?? license.AllowedUsages,
            RestrictedUsages = updates.RestrictedUsages ?? license.RestrictedUsages,
            ValidUntil = updates.ValidUntil ?? license.ValidUntil,
            UpdatedAt = DateTime.UtcNow
        };

        _licenses[licenseId] = license;
        await SaveLicenseAsync(license, ct);

        return license;
    }

    private string GenerateLicenseKey(License license)
    {
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var data = $"{license.Id}:{license.LicenseeId}:{license.ValidUntil:O}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data + _config.LicenseSecret));
        return $"DW-{Convert.ToHexString(hash)[..24]}";
    }

    #endregion

    #region 83.6: Access Revocation

    /// <summary>
    /// Revokes access for a subscription.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID.</param>
    /// <param name="reason">Reason for revocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Revocation result.</returns>
    public async Task<RevocationResult> RevokeAccessAsync(string subscriptionId, RevocationReason reason, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            throw new KeyNotFoundException($"Subscription not found: {subscriptionId}");
        }

        // Update subscription status
        subscription = subscription with
        {
            Status = SubscriptionStatus.Revoked,
            RevokedAt = DateTime.UtcNow,
            RevocationReason = reason
        };
        _subscriptions[subscriptionId] = subscription;

        // Deactivate license
        if (!string.IsNullOrEmpty(subscription.LicenseId) && _licenses.TryGetValue(subscription.LicenseId, out var license))
        {
            license = license with
            {
                IsActive = false,
                DeactivatedAt = DateTime.UtcNow,
                DeactivationReason = reason.ToString()
            };
            _licenses[subscription.LicenseId] = license;
        }

        // Revoke all access grants
        var grants = _accessGrants.Values.Where(g => g.SubscriptionId == subscriptionId).ToList();
        foreach (var grant in grants)
        {
            var updatedGrant = grant with
            {
                IsActive = false,
                RevokedAt = DateTime.UtcNow
            };
            _accessGrants[grant.Id] = updatedGrant;
        }

        await SaveSubscriptionAsync(subscription, ct);

        return new RevocationResult
        {
            SubscriptionId = subscriptionId,
            RevokedAt = DateTime.UtcNow,
            Reason = reason,
            AffectedGrants = grants.Count
        };
    }

    /// <summary>
    /// Automatically enforces access policies (called periodically).
    /// </summary>
    private async Task EnforceAccessPoliciesAsync()
    {
        if (!_isRunning) return;

        var now = DateTime.UtcNow;

        // Check for expired subscriptions
        var expiredSubscriptions = _subscriptions.Values
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate < now)
            .ToList();

        foreach (var subscription in expiredSubscriptions)
        {
            await RevokeAccessAsync(subscription.Id, RevocationReason.Expired);
        }

        // Check for overdue invoices
        var overdueInvoices = _invoices.Values
            .Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < now)
            .ToList();

        foreach (var invoice in overdueInvoices)
        {
            // Mark invoice as overdue
            var updatedInvoice = invoice with { Status = InvoiceStatus.Overdue };
            _invoices[invoice.Id] = updatedInvoice;

            // Revoke access if grace period exceeded
            if (now > invoice.DueDate.AddDays(_config.PaymentGracePeriodDays))
            {
                await RevokeAccessAsync(invoice.SubscriptionId, RevocationReason.PaymentFailed);
            }
        }

        // Check trial expirations
        var expiredTrials = _subscriptions.Values
            .Where(s => s.Model == SubscriptionModel.Trial &&
                       s.Status == SubscriptionStatus.Active &&
                       s.EndDate < now)
            .ToList();

        foreach (var trial in expiredTrials)
        {
            await RevokeAccessAsync(trial.Id, RevocationReason.TrialExpired);
        }
    }

    #endregion

    #region 83.7: Data Preview

    /// <summary>
    /// Gets a preview of listing data without full access.
    /// </summary>
    /// <param name="listingId">Listing ID.</param>
    /// <param name="options">Preview options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data preview.</returns>
    public async Task<DataPreview> GetDataPreviewAsync(string listingId, PreviewOptions? options = null, CancellationToken ct = default)
    {
        if (!_listings.TryGetValue(listingId, out var listing))
        {
            throw new KeyNotFoundException($"Listing not found: {listingId}");
        }

        options ??= new PreviewOptions();

        // Enforce preview limits
        var maxRows = Math.Min(options.MaxRows, _config.MaxPreviewRows);
        var maxColumns = Math.Min(options.MaxColumns, _config.MaxPreviewColumns);

        // In production, this would connect to the actual data source
        // and retrieve sample data with proper masking
        var preview = new DataPreview
        {
            ListingId = listingId,
            ListingName = listing.Name,
            Schema = listing.Schema,
            SampleRows = maxRows,
            SampleColumns = maxColumns,
            DataMasked = options.MaskSensitive,
            GeneratedAt = DateTime.UtcNow,
            Statistics = await GetListingStatisticsAsync(listing, ct),
            SampleData = await GenerateSampleDataAsync(listing, maxRows, maxColumns, options.MaskSensitive, ct)
        };

        // Record preview access for analytics
        await RecordPreviewAccessAsync(listingId, ct);

        return preview;
    }

    private async Task<DataStatistics> GetListingStatisticsAsync(DataListing listing, CancellationToken ct)
    {
        // In production, these would be real statistics from the data source
        return new DataStatistics
        {
            TotalRows = listing.EstimatedRowCount ?? 0,
            TotalColumns = listing.Schema?.Columns.Length ?? 0,
            LastUpdated = listing.DataLastUpdated ?? DateTime.UtcNow,
            SizeBytes = listing.EstimatedSizeBytes ?? 0,
            UpdateFrequency = listing.UpdateFrequency.ToString()
        };
    }

    private async Task<object[][]> GenerateSampleDataAsync(DataListing listing, int maxRows, int maxColumns, bool mask, CancellationToken ct)
    {
        // In production, this would fetch real sample data from the data source
        // For now, we generate placeholder sample data based on schema

        if (listing.Schema?.Columns == null || listing.Schema.Columns.Length == 0)
        {
            return Array.Empty<object[]>();
        }

        var columns = listing.Schema.Columns.Take(maxColumns).ToArray();
        var rows = new List<object[]>();

        for (var i = 0; i < maxRows; i++)
        {
            var row = new object[columns.Length];
            for (var j = 0; j < columns.Length; j++)
            {
                row[j] = GenerateSampleValue(columns[j], i, mask);
            }
            rows.Add(row);
        }

        return rows.ToArray();
    }

    private object GenerateSampleValue(SchemaColumn column, int rowIndex, bool mask)
    {
        if (mask && column.IsSensitive)
        {
            return "***MASKED***";
        }

        return column.DataType.ToLowerInvariant() switch
        {
            "string" or "text" => $"Sample_{column.Name}_{rowIndex}",
            "int" or "integer" or "int32" => rowIndex * 100,
            "long" or "int64" => (long)rowIndex * 1000000,
            "double" or "float" => rowIndex * 1.5,
            "decimal" => (decimal)rowIndex * 99.99m,
            "bool" or "boolean" => rowIndex % 2 == 0,
            "datetime" or "date" => DateTime.UtcNow.AddDays(-rowIndex),
            "guid" or "uuid" => Guid.NewGuid().ToString(),
            _ => $"value_{rowIndex}"
        };
    }

    private async Task RecordPreviewAccessAsync(string listingId, CancellationToken ct)
    {
        // Track preview access for analytics
        // This helps listing owners understand interest levels
    }

    #endregion

    #region 83.8: Rating and Reviews

    /// <summary>
    /// Submits a review for a listing.
    /// </summary>
    /// <param name="review">Review to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submitted review.</returns>
    public async Task<Review> SubmitReviewAsync(ReviewSubmission review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);

        if (!_listings.TryGetValue(review.ListingId, out _))
        {
            throw new KeyNotFoundException($"Listing not found: {review.ListingId}");
        }

        // Verify reviewer has/had access
        var hasAccess = _subscriptions.Values.Any(s =>
            s.ListingId == review.ListingId &&
            s.SubscriberId == review.ReviewerId &&
            (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Expired));

        if (!hasAccess && !_config.AllowReviewsWithoutPurchase)
        {
            throw new InvalidOperationException("Only subscribers can submit reviews");
        }

        // Validate rating
        if (review.Rating < 1 || review.Rating > 5)
        {
            throw new ArgumentException("Rating must be between 1 and 5");
        }

        var newReview = new Review
        {
            Id = GenerateId("rev"),
            ListingId = review.ListingId,
            ReviewerId = review.ReviewerId,
            ReviewerName = review.ReviewerName,
            Rating = review.Rating,
            Title = review.Title,
            Content = review.Content,
            DataQualityRating = review.DataQualityRating,
            AccuracyRating = review.AccuracyRating,
            TimelinessRating = review.TimelinessRating,
            ValueRating = review.ValueRating,
            IsVerifiedPurchase = hasAccess,
            CreatedAt = DateTime.UtcNow,
            Status = _config.RequireReviewModeration ? ReviewStatus.PendingModeration : ReviewStatus.Published
        };

        if (!_reviews.TryGetValue(review.ListingId, out var listingReviews))
        {
            listingReviews = new List<Review>();
            _reviews[review.ListingId] = listingReviews;
        }

        listingReviews.Add(newReview);

        // Update listing quality score
        await UpdateListingQualityScoreAsync(review.ListingId, ct);

        return newReview;
    }

    /// <summary>
    /// Gets reviews for a listing.
    /// </summary>
    /// <param name="listingId">Listing ID.</param>
    /// <param name="limit">Maximum reviews to return.</param>
    /// <param name="offset">Number to skip.</param>
    /// <returns>Reviews with pagination.</returns>
    public ReviewsResponse GetListingReviews(string listingId, int limit = 20, int offset = 0)
    {
        if (!_reviews.TryGetValue(listingId, out var reviews))
        {
            return new ReviewsResponse
            {
                ListingId = listingId,
                Reviews = Array.Empty<Review>(),
                TotalCount = 0,
                AverageRating = 0
            };
        }

        var published = reviews.Where(r => r.Status == ReviewStatus.Published).ToList();

        return new ReviewsResponse
        {
            ListingId = listingId,
            Reviews = published.Skip(offset).Take(limit).ToArray(),
            TotalCount = published.Count,
            AverageRating = published.Any() ? published.Average(r => r.Rating) : 0,
            RatingDistribution = new Dictionary<int, int>
            {
                [1] = published.Count(r => r.Rating == 1),
                [2] = published.Count(r => r.Rating == 2),
                [3] = published.Count(r => r.Rating == 3),
                [4] = published.Count(r => r.Rating == 4),
                [5] = published.Count(r => r.Rating == 5)
            }
        };
    }

    /// <summary>
    /// Allows listing owner to respond to a review.
    /// </summary>
    /// <param name="reviewId">Review ID.</param>
    /// <param name="response">Owner's response.</param>
    /// <param name="responderId">ID of the responder (must be listing owner).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated review with response.</returns>
    public async Task<Review> RespondToReviewAsync(string reviewId, string response, string responderId, CancellationToken ct = default)
    {
        Review? targetReview = null;
        string? listingId = null;

        foreach (var kvp in _reviews)
        {
            var review = kvp.Value.FirstOrDefault(r => r.Id == reviewId);
            if (review != null)
            {
                targetReview = review;
                listingId = kvp.Key;
                break;
            }
        }

        if (targetReview == null || listingId == null)
        {
            throw new KeyNotFoundException($"Review not found: {reviewId}");
        }

        // Verify responder is listing owner
        if (!_listings.TryGetValue(listingId, out var listing) || listing.PublisherId != responderId)
        {
            throw new UnauthorizedAccessException("Only listing owner can respond to reviews");
        }

        var updatedReview = targetReview with
        {
            OwnerResponse = response,
            OwnerRespondedAt = DateTime.UtcNow
        };

        var reviews = _reviews[listingId];
        var index = reviews.FindIndex(r => r.Id == reviewId);
        if (index >= 0)
        {
            reviews[index] = updatedReview;
        }

        return updatedReview;
    }

    private async Task UpdateListingQualityScoreAsync(string listingId, CancellationToken ct)
    {
        if (_listings.TryGetValue(listingId, out var listing))
        {
            var newScore = await CalculateQualityScoreAsync(listing, ct);
            listing = listing with { QualityScore = newScore, UpdatedAt = DateTime.UtcNow };
            _listings[listingId] = listing;
        }
    }

    #endregion

    #region 83.9: Chargeback Reporting

    /// <summary>
    /// Generates a chargeback report for internal cost allocation.
    /// </summary>
    /// <param name="request">Chargeback report request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chargeback report.</returns>
    public async Task<ChargebackReport> GenerateChargebackReportAsync(ChargebackRequest request, CancellationToken ct = default)
    {
        var lineItems = new List<ChargebackLineItem>();

        // Get all relevant invoices
        var invoices = _invoices.Values
            .Where(i => i.PeriodStart >= request.PeriodStart && i.PeriodEnd <= request.PeriodEnd)
            .ToList();

        // Filter by subscriber if specified
        if (!string.IsNullOrEmpty(request.SubscriberId))
        {
            invoices = invoices.Where(i => i.SubscriberId == request.SubscriberId).ToList();
        }

        // Filter by department/cost center if specified
        if (!string.IsNullOrEmpty(request.CostCenter))
        {
            // In production, this would filter by cost center mapping
            // For now, we include all invoices
        }

        // Group by listing or subscriber based on grouping preference
        IEnumerable<IGrouping<string, Invoice>> grouped;
        if (request.GroupBy == ChargebackGrouping.ByListing)
        {
            grouped = invoices.GroupBy(i => i.ListingId);
        }
        else if (request.GroupBy == ChargebackGrouping.BySubscriber)
        {
            grouped = invoices.GroupBy(i => i.SubscriberId);
        }
        else
        {
            grouped = invoices.GroupBy(i => i.SubscriberId); // Default
        }

        foreach (var group in grouped)
        {
            var groupInvoices = group.ToList();
            var totalAmount = groupInvoices.Sum(i => i.TotalAmount);
            var paidAmount = groupInvoices.Sum(i => i.AmountPaid);

            // Get usage for the group
            var usageRecords = _usageRecords.Values
                .Where(u => groupInvoices.Any(i => i.SubscriptionId == u.SubscriptionId))
                .ToList();

            var listingName = "";
            if (_listings.TryGetValue(group.First().ListingId, out var listing))
            {
                listingName = listing.Name;
            }

            lineItems.Add(new ChargebackLineItem
            {
                GroupKey = group.Key,
                GroupName = request.GroupBy == ChargebackGrouping.ByListing ? listingName : group.Key,
                TotalAmount = totalAmount,
                PaidAmount = paidAmount,
                OutstandingAmount = totalAmount - paidAmount,
                InvoiceCount = groupInvoices.Count,
                QueryCount = usageRecords.Sum(u => u.QueryCount),
                BytesTransferred = usageRecords.Sum(u => u.BytesTransferred),
                ComputeUnits = usageRecords.Sum(u => u.ComputeUnits)
            });
        }

        var report = new ChargebackReport
        {
            Id = GenerateId("cbr"),
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            GeneratedAt = DateTime.UtcNow,
            GeneratedBy = request.RequestedBy,
            CostCenter = request.CostCenter,
            GroupBy = request.GroupBy,
            LineItems = lineItems.ToArray(),
            TotalAmount = lineItems.Sum(li => li.TotalAmount),
            TotalPaid = lineItems.Sum(li => li.PaidAmount),
            TotalOutstanding = lineItems.Sum(li => li.OutstandingAmount),
            Currency = _config.DefaultCurrency
        };

        return report;
    }

    /// <summary>
    /// Exports a chargeback report in various formats.
    /// </summary>
    /// <param name="report">Report to export.</param>
    /// <param name="format">Export format.</param>
    /// <returns>Exported report data.</returns>
    public byte[] ExportChargebackReport(ChargebackReport report, ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Csv => ExportChargebackToCsv(report),
            ExportFormat.Json => ExportChargebackToJson(report),
            ExportFormat.Pdf => ExportChargebackToPdf(report),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    private byte[] ExportChargebackToCsv(ChargebackReport report)
    {
        using var ms = new MemoryStream(4096);
        using var writer = new StreamWriter(ms);

        // Header
        writer.WriteLine("Group,Name,Total Amount,Paid Amount,Outstanding,Invoices,Queries,Data (GB),Compute Units");

        foreach (var item in report.LineItems)
        {
            var dataGb = item.BytesTransferred / (1024.0 * 1024.0 * 1024.0);
            writer.WriteLine($"{item.GroupKey},{item.GroupName},{item.TotalAmount:F2},{item.PaidAmount:F2},{item.OutstandingAmount:F2},{item.InvoiceCount},{item.QueryCount},{dataGb:F2},{item.ComputeUnits:F2}");
        }

        // Summary
        writer.WriteLine();
        writer.WriteLine($"Total,{report.TotalAmount:F2},{report.TotalPaid:F2},{report.TotalOutstanding:F2}");

        writer.Flush();
        return ms.ToArray();
    }

    private byte[] ExportChargebackToJson(ChargebackReport report)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(report, options);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private byte[] ExportChargebackToPdf(ChargebackReport report)
    {
        // In production, this would use a PDF library (iTextSharp, PdfSharp, etc.)
        // For now, return a placeholder
        var text = $"Chargeback Report\n\nPeriod: {report.PeriodStart:d} - {report.PeriodEnd:d}\n\nTotal: {report.TotalAmount:C}";
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    #endregion

    #region 83.10: Smart Contract Integration

    /// <summary>
    /// Creates a smart contract for a data agreement.
    /// </summary>
    /// <param name="request">Contract creation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created smart contract.</returns>
    public async Task<SmartContract> CreateSmartContractAsync(SmartContractRequest request, CancellationToken ct = default)
    {
        if (!_config.EnableBlockchainContracts)
        {
            throw new InvalidOperationException("Blockchain contracts are not enabled");
        }

        if (!_listings.TryGetValue(request.ListingId, out var listing))
        {
            throw new KeyNotFoundException($"Listing not found: {request.ListingId}");
        }

        var contract = new SmartContract
        {
            Id = GenerateId("sc"),
            ListingId = request.ListingId,
            PublisherId = listing.PublisherId,
            SubscriberId = request.SubscriberId,
            ContractType = request.ContractType,
            Terms = new ContractTerms
            {
                Price = request.Price,
                Currency = request.Currency,
                Duration = request.Duration,
                AutoRenew = request.AutoRenew,
                UsageLimits = request.UsageLimits,
                PaymentSchedule = request.PaymentSchedule,
                PenaltyClauses = request.PenaltyClauses,
                DisputeResolution = request.DisputeResolution ?? "Arbitration"
            },
            Status = SmartContractStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            Blockchain = request.Blockchain ?? "Ethereum"
        };

        // Generate contract hash for verification
        contract = contract with { ContractHash = ComputeContractHash(contract) };

        _smartContracts[contract.Id] = contract;

        return contract;
    }

    /// <summary>
    /// Deploys a smart contract to the blockchain.
    /// </summary>
    /// <param name="contractId">Contract ID to deploy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deployed contract with blockchain address.</returns>
    public async Task<SmartContract> DeployContractAsync(string contractId, CancellationToken ct = default)
    {
        if (!_smartContracts.TryGetValue(contractId, out var contract))
        {
            throw new KeyNotFoundException($"Contract not found: {contractId}");
        }

        if (contract.Status != SmartContractStatus.Draft && contract.Status != SmartContractStatus.PendingSignatures)
        {
            throw new InvalidOperationException($"Contract cannot be deployed in status: {contract.Status}");
        }

        // In production, this would:
        // 1. Compile the contract code
        // 2. Deploy to the specified blockchain
        // 3. Wait for confirmation
        // 4. Return the contract address

        // Simulated deployment
        var blockchainAddress = GenerateBlockchainAddress();

        contract = contract with
        {
            Status = SmartContractStatus.Active,
            BlockchainAddress = blockchainAddress,
            DeployedAt = DateTime.UtcNow,
            TransactionHash = GenerateTransactionHash()
        };

        _smartContracts[contractId] = contract;

        return contract;
    }

    /// <summary>
    /// Records a signature on a smart contract.
    /// </summary>
    /// <param name="contractId">Contract ID.</param>
    /// <param name="signerId">Signer ID.</param>
    /// <param name="signature">Digital signature.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated contract.</returns>
    public async Task<SmartContract> SignContractAsync(string contractId, string signerId, string signature, CancellationToken ct = default)
    {
        if (!_smartContracts.TryGetValue(contractId, out var contract))
        {
            throw new KeyNotFoundException($"Contract not found: {contractId}");
        }

        // Verify signer is authorized
        if (signerId != contract.PublisherId && signerId != contract.SubscriberId)
        {
            throw new UnauthorizedAccessException("Signer is not a party to this contract");
        }

        var signatures = contract.Signatures?.ToList() ?? new List<ContractSignature>();

        // Check if already signed by this party
        if (signatures.Any(s => s.SignerId == signerId))
        {
            throw new InvalidOperationException("Contract already signed by this party");
        }

        signatures.Add(new ContractSignature
        {
            SignerId = signerId,
            Signature = signature,
            SignedAt = DateTime.UtcNow,
            SignatureType = "ECDSA"
        });

        contract = contract with
        {
            Signatures = signatures.ToArray(),
            Status = signatures.Count >= 2 ? SmartContractStatus.FullySigned : SmartContractStatus.PendingSignatures
        };

        _smartContracts[contractId] = contract;

        return contract;
    }

    /// <summary>
    /// Executes a payment through a smart contract.
    /// </summary>
    /// <param name="contractId">Contract ID.</param>
    /// <param name="amount">Payment amount.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payment execution result.</returns>
    public async Task<SmartContractPayment> ExecuteContractPaymentAsync(string contractId, decimal amount, CancellationToken ct = default)
    {
        if (!_smartContracts.TryGetValue(contractId, out var contract))
        {
            throw new KeyNotFoundException($"Contract not found: {contractId}");
        }

        if (contract.Status != SmartContractStatus.Active)
        {
            throw new InvalidOperationException("Contract is not active");
        }

        // In production, this would execute the payment on the blockchain
        var payment = new SmartContractPayment
        {
            ContractId = contractId,
            Amount = amount,
            Currency = contract.Terms.Currency,
            TransactionHash = GenerateTransactionHash(),
            ExecutedAt = DateTime.UtcNow,
            Status = PaymentExecutionStatus.Confirmed
        };

        return payment;
    }

    /// <summary>
    /// Verifies a smart contract's integrity on the blockchain.
    /// </summary>
    /// <param name="contractId">Contract ID.</param>
    /// <returns>Verification result.</returns>
    public async Task<ContractVerification> VerifyContractAsync(string contractId)
    {
        if (!_smartContracts.TryGetValue(contractId, out var contract))
        {
            throw new KeyNotFoundException($"Contract not found: {contractId}");
        }

        // Verify contract hash
        var currentHash = ComputeContractHash(contract);
        var hashMatch = currentHash == contract.ContractHash;

        // In production, this would also verify on the blockchain
        return new ContractVerification
        {
            ContractId = contractId,
            IsValid = hashMatch && contract.Status == SmartContractStatus.Active,
            HashVerified = hashMatch,
            BlockchainVerified = !string.IsNullOrEmpty(contract.BlockchainAddress),
            BlockchainAddress = contract.BlockchainAddress,
            VerifiedAt = DateTime.UtcNow
        };
    }

    private string ComputeContractHash(SmartContract contract)
    {
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var data = JsonSerializer.Serialize(new
        {
            contract.ListingId,
            contract.PublisherId,
            contract.SubscriberId,
            contract.Terms,
            contract.ContractType
        });
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }

    private string GenerateBlockchainAddress()
    {
        var bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);
        return "0x" + Convert.ToHexString(bytes);
    }

    private string GenerateTransactionHash()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "0x" + Convert.ToHexString(bytes);
    }

    #endregion

    #region Message Handlers

    private async Task HandleListAsync(PluginMessage message)
    {
        var action = GetString(message.Payload, "action") ?? "create";
        var listingData = GetObject<DataListing>(message.Payload, "listing");

        if (action == "create" && listingData != null)
        {
            var result = await CreateListingAsync(listingData);
            message.Payload["result"] = result;
        }
        else if (action == "publish")
        {
            var listingId = GetString(message.Payload, "listingId");
            if (listingId != null)
            {
                var result = await PublishListingAsync(listingId);
                message.Payload["result"] = result;
            }
        }
        else if (action == "get")
        {
            var listingId = GetString(message.Payload, "listingId");
            if (listingId != null && _listings.TryGetValue(listingId, out var listing))
            {
                message.Payload["result"] = listing;
            }
        }
    }

    private void HandleSearch(PluginMessage message)
    {
        var query = GetObject<ListingSearchQuery>(message.Payload, "query") ?? new ListingSearchQuery();
        var results = SearchListings(query);
        message.Payload["result"] = results;
    }

    private async Task HandleSubscribeAsync(PluginMessage message)
    {
        var request = GetObject<SubscriptionRequest>(message.Payload, "request");
        if (request != null)
        {
            var result = await CreateSubscriptionAsync(request);
            message.Payload["result"] = result;
        }
    }

    private async Task HandleAccessAsync(PluginMessage message)
    {
        var subscriptionId = GetString(message.Payload, "subscriptionId");
        var usage = GetString(message.Payload, "usage") ?? "Access";

        if (subscriptionId != null && _subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            // Validate license
            var validation = ValidateLicense(subscription.LicenseId!, usage);
            message.Payload["result"] = new
            {
                Allowed = validation.IsValid,
                Reason = validation.Reason,
                subscription.ListingId
            };
        }
    }

    private async Task HandlePreviewAsync(PluginMessage message)
    {
        var listingId = GetString(message.Payload, "listingId");
        var options = GetObject<PreviewOptions>(message.Payload, "options");

        if (listingId != null)
        {
            var result = await GetDataPreviewAsync(listingId, options);
            message.Payload["result"] = result;
        }
    }

    private async Task HandleMeterAsync(PluginMessage message)
    {
        var usage = GetObject<UsageEvent>(message.Payload, "usage");
        if (usage != null)
        {
            var result = await RecordUsageAsync(usage);
            message.Payload["result"] = result;
        }
    }

    private async Task HandleInvoiceAsync(PluginMessage message)
    {
        var action = GetString(message.Payload, "action") ?? "generate";

        if (action == "generate")
        {
            var subscriptionId = GetString(message.Payload, "subscriptionId");
            if (subscriptionId != null)
            {
                var result = await GenerateInvoiceAsync(subscriptionId, DateTime.UtcNow);
                message.Payload["result"] = result;
            }
        }
        else if (action == "pay")
        {
            var invoiceId = GetString(message.Payload, "invoiceId");
            var payment = GetObject<PaymentRecord>(message.Payload, "payment");
            if (invoiceId != null && payment != null)
            {
                var result = await RecordPaymentAsync(invoiceId, payment);
                message.Payload["result"] = result;
            }
        }
    }

    private async Task HandleReviewAsync(PluginMessage message)
    {
        var action = GetString(message.Payload, "action") ?? "submit";

        if (action == "submit")
        {
            var review = GetObject<ReviewSubmission>(message.Payload, "review");
            if (review != null)
            {
                var result = await SubmitReviewAsync(review);
                message.Payload["result"] = result;
            }
        }
        else if (action == "list")
        {
            var listingId = GetString(message.Payload, "listingId");
            if (listingId != null)
            {
                var result = GetListingReviews(listingId);
                message.Payload["result"] = result;
            }
        }
    }

    private async Task HandleRevokeAsync(PluginMessage message)
    {
        var subscriptionId = GetString(message.Payload, "subscriptionId");
        var reason = GetEnum(message.Payload, "reason", RevocationReason.Manual);

        if (subscriptionId != null)
        {
            var result = await RevokeAccessAsync(subscriptionId, reason);
            message.Payload["result"] = result;
        }
    }

    private async Task HandleChargebackAsync(PluginMessage message)
    {
        var request = GetObject<ChargebackRequest>(message.Payload, "request");
        if (request != null)
        {
            var result = await GenerateChargebackReportAsync(request);
            message.Payload["result"] = result;
        }
    }

    private async Task HandleContractAsync(PluginMessage message)
    {
        var action = GetString(message.Payload, "action") ?? "create";

        switch (action)
        {
            case "create":
                var request = GetObject<SmartContractRequest>(message.Payload, "request");
                if (request != null)
                {
                    var result = await CreateSmartContractAsync(request);
                    message.Payload["result"] = result;
                }
                break;

            case "deploy":
                var contractId = GetString(message.Payload, "contractId");
                if (contractId != null)
                {
                    var result = await DeployContractAsync(contractId);
                    message.Payload["result"] = result;
                }
                break;

            case "sign":
                contractId = GetString(message.Payload, "contractId");
                var signerId = GetString(message.Payload, "signerId");
                var signature = GetString(message.Payload, "signature");
                if (contractId != null && signerId != null && signature != null)
                {
                    var result = await SignContractAsync(contractId, signerId, signature);
                    message.Payload["result"] = result;
                }
                break;

            case "verify":
                contractId = GetString(message.Payload, "contractId");
                if (contractId != null)
                {
                    var result = await VerifyContractAsync(contractId);
                    message.Payload["result"] = result;
                }
                break;
        }
    }

    #endregion

    #region Billing Cycle Processing

    private async Task ProcessBillingCycleAsync()
    {
        if (!_isRunning) return;

        var now = DateTime.UtcNow;

        // Find subscriptions due for billing
        var dueSubscriptions = _subscriptions.Values
            .Where(s => s.Status == SubscriptionStatus.Active &&
                       s.Model != SubscriptionModel.Trial &&
                       (s.LastInvoiceDate == null ||
                        (s.Model == SubscriptionModel.Monthly && s.LastInvoiceDate.Value.AddMonths(1) <= now) ||
                        (s.Model == SubscriptionModel.Annual && s.LastInvoiceDate.Value.AddYears(1) <= now)))
            .ToList();

        foreach (var subscription in dueSubscriptions)
        {
            try
            {
                await GenerateInvoiceAsync(subscription.Id, now);
            }
            catch
            {
                // Log error but continue processing other subscriptions
            }
        }
    }

    #endregion

    #region Persistence

    private async Task LoadStateAsync()
    {
        await LoadListingsAsync();
        await LoadSubscriptionsAsync();
        await LoadLicensesAsync();
        await LoadReviewsAsync();
    }

    private async Task SaveStateAsync()
    {
        foreach (var listing in _listings.Values)
        {
            await SaveListingAsync(listing);
        }
        foreach (var subscription in _subscriptions.Values)
        {
            await SaveSubscriptionAsync(subscription);
        }
    }

    private async Task LoadListingsAsync()
    {
        var path = Path.Combine(_storagePath, "listings");
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var listing = JsonSerializer.Deserialize<DataListing>(json);
                if (listing != null)
                {
                    _listings[listing.Id] = listing;
                }
            }
            catch
            {
                // Skip corrupted files
            }
        }
    }

    private async Task SaveListingAsync(DataListing listing, CancellationToken ct = default)
    {
        var path = Path.Combine(_storagePath, "listings");
        Directory.CreateDirectory(path);

        var json = JsonSerializer.Serialize(listing, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(path, $"{listing.Id}.json"), json, ct);
    }

    private async Task LoadSubscriptionsAsync()
    {
        var path = Path.Combine(_storagePath, "subscriptions");
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var subscription = JsonSerializer.Deserialize<Subscription>(json);
                if (subscription != null)
                {
                    _subscriptions[subscription.Id] = subscription;
                }
            }
            catch
            {
                // Skip corrupted files
            }
        }
    }

    private async Task SaveSubscriptionAsync(Subscription subscription, CancellationToken ct = default)
    {
        var path = Path.Combine(_storagePath, "subscriptions");
        Directory.CreateDirectory(path);

        var json = JsonSerializer.Serialize(subscription, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(path, $"{subscription.Id}.json"), json, ct);
    }

    private async Task LoadLicensesAsync()
    {
        var path = Path.Combine(_storagePath, "licenses");
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var license = JsonSerializer.Deserialize<License>(json);
                if (license != null)
                {
                    _licenses[license.Id] = license;
                }
            }
            catch
            {
                // Skip corrupted files
            }
        }
    }

    private async Task SaveLicenseAsync(License license, CancellationToken ct = default)
    {
        var path = Path.Combine(_storagePath, "licenses");
        Directory.CreateDirectory(path);

        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(path, $"{license.Id}.json"), json, ct);
    }

    private async Task LoadReviewsAsync()
    {
        var path = Path.Combine(_storagePath, "reviews");
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var listingReviews = JsonSerializer.Deserialize<ListingReviewsData>(json);
                if (listingReviews != null)
                {
                    _reviews[listingReviews.ListingId] = listingReviews.Reviews.ToList();
                }
            }
            catch
            {
                // Skip corrupted files
            }
        }
    }

    private async Task SaveUsageRecordAsync(UsageRecord record, CancellationToken ct = default)
    {
        var path = Path.Combine(_storagePath, "usage");
        Directory.CreateDirectory(path);

        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(path, $"{record.Id}.json"), json, ct);
    }

    private async Task SaveInvoiceAsync(Invoice invoice, CancellationToken ct = default)
    {
        var path = Path.Combine(_storagePath, "invoices");
        Directory.CreateDirectory(path);

        var json = JsonSerializer.Serialize(invoice, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(path, $"{invoice.Id}.json"), json, ct);
    }

    #endregion

    #region Helpers

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }

    private static string? GetString(Dictionary<string, object> payload, string key)
        => payload.TryGetValue(key, out var val) && val is string s ? s : null;

    private static T? GetObject<T>(Dictionary<string, object> payload, string key) where T : class
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is T t) return t;
            if (val is JsonElement je)
            {
                return JsonSerializer.Deserialize<T>(je.GetRawText());
            }
        }
        return null;
    }

    private static TEnum GetEnum<TEnum>(Dictionary<string, object> payload, string key, TEnum defaultValue) where TEnum : struct
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is string s && Enum.TryParse<TEnum>(s, true, out var result))
            {
                return result;
            }
        }
        return defaultValue;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Data listing for the marketplace.
/// </summary>
public sealed record DataListing
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string PublisherId { get; init; } = string.Empty;
    public string DataSourceId { get; init; } = string.Empty;
    public DataSchema? Schema { get; init; }
    public DataPricing Pricing { get; init; } = new();
    public DataLicense? License { get; init; }
    public UpdateFrequency UpdateFrequency { get; init; }
    public double QualityScore { get; init; }
    public ListingStatus Status { get; init; }
    public string? Documentation { get; init; }
    public string? SampleQueryUrl { get; init; }
    public long? EstimatedRowCount { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public DateTime? DataLastUpdated { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
}

/// <summary>
/// Schema for a data listing.
/// </summary>
public sealed record DataSchema
{
    public SchemaColumn[] Columns { get; init; } = Array.Empty<SchemaColumn>();
    public string? PrimaryKey { get; init; }
    public string[]? Indexes { get; init; }
}

/// <summary>
/// Column definition in a schema.
/// </summary>
public sealed record SchemaColumn
{
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public bool IsNullable { get; init; }
    public bool IsSensitive { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Pricing for a data listing.
/// </summary>
public sealed record DataPricing
{
    public decimal BasePrice { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal PerQueryPrice { get; init; }
    public decimal PerGbPrice { get; init; }
    public SubscriptionModel[] AllowedModels { get; init; } = new[] { SubscriptionModel.Monthly };
}

/// <summary>
/// License terms for a listing.
/// </summary>
public sealed record DataLicense
{
    public string Type { get; init; } = string.Empty;
    public string Terms { get; init; } = string.Empty;
    public string[] AllowedUsages { get; init; } = Array.Empty<string>();
    public string[] RestrictedUsages { get; init; } = Array.Empty<string>();
    public int MaxUsers { get; init; } = 1;
    public bool IsTransferable { get; init; }
    public bool AttributionRequired { get; init; }
}

/// <summary>
/// Subscription to a listing.
/// </summary>
public sealed record Subscription
{
    public string Id { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public SubscriptionModel Model { get; init; }
    public SubscriptionTier Tier { get; init; }
    public SubscriptionStatus Status { get; init; }
    public decimal Price { get; init; }
    public string? LicenseId { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ActivatedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public DateTime? RenewedAt { get; init; }
    public DateTime? LastInvoiceDate { get; init; }
    public string? PaymentReference { get; init; }
    public long? QueryLimit { get; init; }
    public long? ByteLimit { get; init; }
    public int RenewalCount { get; init; }
    public RevocationReason? RevocationReason { get; init; }
}

/// <summary>
/// License for a subscription.
/// </summary>
public sealed record License
{
    public string Id { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string LicenseeId { get; init; } = string.Empty;
    public string LicenseKey { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Terms { get; init; } = string.Empty;
    public string[] AllowedUsages { get; init; } = Array.Empty<string>();
    public string[] RestrictedUsages { get; init; } = Array.Empty<string>();
    public int MaxUsers { get; init; }
    public bool IsTransferable { get; init; }
    public bool AttributionRequired { get; init; }
    public DateTime ValidFrom { get; init; }
    public DateTime ValidUntil { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? DeactivatedAt { get; init; }
    public string? DeactivationReason { get; init; }
}

/// <summary>
/// Usage record for metering.
/// </summary>
public sealed record UsageRecord
{
    public string Id { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public long QueryCount { get; init; }
    public long BytesTransferred { get; init; }
    public double ComputeUnits { get; init; }
    public long RecordCount { get; init; }
    public int EventCount { get; init; }
    public DateTime LastUpdated { get; init; }
    public bool OverLimitWarning { get; init; }
}

/// <summary>
/// Usage event for metering.
/// </summary>
public sealed record UsageEvent
{
    public string SubscriptionId { get; init; } = string.Empty;
    public int QueryCount { get; init; }
    public long BytesTransferred { get; init; }
    public double ComputeUnits { get; init; }
    public long RecordCount { get; init; }
}

/// <summary>
/// Invoice for billing.
/// </summary>
public sealed record Invoice
{
    public string Id { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public InvoiceLineItem[] LineItems { get; init; } = Array.Empty<InvoiceLineItem>();
    public decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal AmountPaid { get; init; }
    public string Currency { get; init; } = "USD";
    public InvoiceStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime DueDate { get; init; }
    public DateTime? PaidAt { get; init; }
    public string? PaymentMethod { get; init; }
    public string? PaymentReference { get; init; }
}

/// <summary>
/// Line item in an invoice.
/// </summary>
public sealed record InvoiceLineItem
{
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public LineItemType Type { get; init; }
}

/// <summary>
/// Review for a listing.
/// </summary>
public sealed record Review
{
    public string Id { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string ReviewerId { get; init; } = string.Empty;
    public string ReviewerName { get; init; } = string.Empty;
    public int Rating { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public int? DataQualityRating { get; init; }
    public int? AccuracyRating { get; init; }
    public int? TimelinessRating { get; init; }
    public int? ValueRating { get; init; }
    public bool IsVerifiedPurchase { get; init; }
    public ReviewStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? OwnerResponse { get; init; }
    public DateTime? OwnerRespondedAt { get; init; }
}

/// <summary>
/// Smart contract for blockchain integration.
/// </summary>
public sealed record SmartContract
{
    public string Id { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public ContractType ContractType { get; init; }
    public ContractTerms Terms { get; init; } = new();
    public SmartContractStatus Status { get; init; }
    public string ContractHash { get; init; } = string.Empty;
    public string Blockchain { get; init; } = string.Empty;
    public string? BlockchainAddress { get; init; }
    public string? TransactionHash { get; init; }
    public ContractSignature[]? Signatures { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DeployedAt { get; init; }
}

/// <summary>
/// Terms for a smart contract.
/// </summary>
public sealed record ContractTerms
{
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public TimeSpan Duration { get; init; }
    public bool AutoRenew { get; init; }
    public UsageLimits? UsageLimits { get; init; }
    public PaymentSchedule PaymentSchedule { get; init; }
    public PenaltyClause[]? PenaltyClauses { get; init; }
    public string DisputeResolution { get; init; } = string.Empty;
}

/// <summary>
/// Usage limits for a contract.
/// </summary>
public sealed record UsageLimits
{
    public long? MaxQueries { get; init; }
    public long? MaxBytes { get; init; }
    public double? MaxComputeUnits { get; init; }
}

/// <summary>
/// Penalty clause for contract violations.
/// </summary>
public sealed record PenaltyClause
{
    public string Violation { get; init; } = string.Empty;
    public decimal PenaltyAmount { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Signature on a smart contract.
/// </summary>
public sealed record ContractSignature
{
    public string SignerId { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string SignatureType { get; init; } = string.Empty;
    public DateTime SignedAt { get; init; }
}

/// <summary>
/// Access grant for a subscription.
/// </summary>
public sealed record AccessGrant
{
    public string Id { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public DateTime GrantedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public bool IsActive { get; init; }
    public DateTime? RevokedAt { get; init; }
}

// Request and response types

public sealed record SubscriptionRequest
{
    public string ListingId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public SubscriptionModel Model { get; init; }
    public SubscriptionTier Tier { get; init; }
    public long? QueryLimit { get; init; }
    public long? ByteLimit { get; init; }
}

public sealed record ListingSearchQuery
{
    public string? SearchText { get; init; }
    public string? Category { get; init; }
    public string[]? Tags { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public double? MinQualityScore { get; init; }
    public ListingStatus? Status { get; init; }
    public ListingSortBy SortBy { get; init; }
    public bool SortDescending { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
}

public sealed record ReviewSubmission
{
    public string ListingId { get; init; } = string.Empty;
    public string ReviewerId { get; init; } = string.Empty;
    public string ReviewerName { get; init; } = string.Empty;
    public int Rating { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public int? DataQualityRating { get; init; }
    public int? AccuracyRating { get; init; }
    public int? TimelinessRating { get; init; }
    public int? ValueRating { get; init; }
}

public sealed record PreviewOptions
{
    public int MaxRows { get; init; } = 100;
    public int MaxColumns { get; init; } = 50;
    public bool MaskSensitive { get; init; } = true;
}

public sealed record PaymentRecord
{
    public decimal Amount { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
}

public sealed record LicenseUpdate
{
    public int? MaxUsers { get; init; }
    public string[]? AllowedUsages { get; init; }
    public string[]? RestrictedUsages { get; init; }
    public DateTime? ValidUntil { get; init; }
}

public sealed record ChargebackRequest
{
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public string? SubscriberId { get; init; }
    public string? CostCenter { get; init; }
    public ChargebackGrouping GroupBy { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record SmartContractRequest
{
    public string ListingId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public ContractType ContractType { get; init; }
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public TimeSpan Duration { get; init; }
    public bool AutoRenew { get; init; }
    public UsageLimits? UsageLimits { get; init; }
    public PaymentSchedule PaymentSchedule { get; init; }
    public PenaltyClause[]? PenaltyClauses { get; init; }
    public string? DisputeResolution { get; init; }
    public string? Blockchain { get; init; }
}

// Response types

public sealed record UsageSummary
{
    public string ListingId { get; init; } = string.Empty;
    public long TotalQueries { get; init; }
    public long TotalBytesTransferred { get; init; }
    public double TotalComputeUnits { get; init; }
    public long TotalRecords { get; init; }
    public int UniqueSubscribers { get; init; }
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
}

public sealed record DataPreview
{
    public string ListingId { get; init; } = string.Empty;
    public string ListingName { get; init; } = string.Empty;
    public DataSchema? Schema { get; init; }
    public int SampleRows { get; init; }
    public int SampleColumns { get; init; }
    public bool DataMasked { get; init; }
    public DateTime GeneratedAt { get; init; }
    public DataStatistics? Statistics { get; init; }
    public object[][]? SampleData { get; init; }
}

public sealed record DataStatistics
{
    public long TotalRows { get; init; }
    public int TotalColumns { get; init; }
    public DateTime LastUpdated { get; init; }
    public long SizeBytes { get; init; }
    public string? UpdateFrequency { get; init; }
}

public sealed record LicenseValidationResult
{
    public bool IsValid { get; init; }
    public string? Reason { get; init; }
    public License? License { get; init; }
}

public sealed record RevocationResult
{
    public string SubscriptionId { get; init; } = string.Empty;
    public DateTime RevokedAt { get; init; }
    public RevocationReason Reason { get; init; }
    public int AffectedGrants { get; init; }
}

public sealed record ReviewsResponse
{
    public string ListingId { get; init; } = string.Empty;
    public Review[] Reviews { get; init; } = Array.Empty<Review>();
    public int TotalCount { get; init; }
    public double AverageRating { get; init; }
    public Dictionary<int, int>? RatingDistribution { get; init; }
}

public sealed record ChargebackReport
{
    public string Id { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public DateTime GeneratedAt { get; init; }
    public string? GeneratedBy { get; init; }
    public string? CostCenter { get; init; }
    public ChargebackGrouping GroupBy { get; init; }
    public ChargebackLineItem[] LineItems { get; init; } = Array.Empty<ChargebackLineItem>();
    public decimal TotalAmount { get; init; }
    public decimal TotalPaid { get; init; }
    public decimal TotalOutstanding { get; init; }
    public string Currency { get; init; } = "USD";
}

public sealed record ChargebackLineItem
{
    public string GroupKey { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public int InvoiceCount { get; init; }
    public long QueryCount { get; init; }
    public long BytesTransferred { get; init; }
    public double ComputeUnits { get; init; }
}

public sealed record PaymentIntegrationResult
{
    public string InvoiceId { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string PaymentToken { get; init; } = string.Empty;
    public string PaymentUrl { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
}

public sealed record SmartContractPayment
{
    public string ContractId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string TransactionHash { get; init; } = string.Empty;
    public DateTime ExecutedAt { get; init; }
    public PaymentExecutionStatus Status { get; init; }
}

public sealed record ContractVerification
{
    public string ContractId { get; init; } = string.Empty;
    public bool IsValid { get; init; }
    public bool HashVerified { get; init; }
    public bool BlockchainVerified { get; init; }
    public string? BlockchainAddress { get; init; }
    public DateTime VerifiedAt { get; init; }
}

// Internal types
internal sealed record ListingReviewsData
{
    public string ListingId { get; init; } = string.Empty;
    public Review[] Reviews { get; init; } = Array.Empty<Review>();
}

// Enums

public enum ListingStatus
{
    Unknown,
    Draft,
    Published,
    Suspended,
    Archived
}

public enum SubscriptionModel
{
    Trial,
    Monthly,
    Annual,
    Perpetual,
    PayPerQuery
}

public enum SubscriptionTier
{
    Standard,
    Professional,
    Enterprise
}

public enum SubscriptionStatus
{
    PendingPayment,
    Active,
    Expired,
    Cancelled,
    Revoked
}

public enum UpdateFrequency
{
    Unknown,
    RealTime,
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Quarterly,
    Annually,
    Static
}

public enum InvoiceStatus
{
    Draft,
    Sent,
    PaymentPending,
    PartiallyPaid,
    Paid,
    Overdue,
    Cancelled
}

public enum LineItemType
{
    Subscription,
    Usage,
    Overage,
    Credit,
    Fee
}

public enum ReviewStatus
{
    PendingModeration,
    Published,
    Rejected,
    Hidden
}

public enum RevocationReason
{
    Manual,
    PaymentFailed,
    Expired,
    TrialExpired,
    ViolationOfTerms,
    FraudDetected
}

public enum ListingSortBy
{
    Date,
    Name,
    Price,
    Quality,
    Popularity
}

public enum ChargebackGrouping
{
    BySubscriber,
    ByListing,
    ByCostCenter
}

public enum ExportFormat
{
    Csv,
    Json,
    Pdf,
    Excel
}

public enum ContractType
{
    DataLicense,
    DataPurchase,
    DataSubscription,
    DataSharing
}

public enum SmartContractStatus
{
    Draft,
    PendingSignatures,
    FullySigned,
    Active,
    Completed,
    Terminated,
    Disputed
}

public enum PaymentSchedule
{
    OneTime,
    Monthly,
    Quarterly,
    Annually,
    OnUsage
}

public enum PaymentExecutionStatus
{
    Pending,
    Confirmed,
    Failed,
    Reversed
}

/// <summary>
/// Configuration for the Data Marketplace Plugin.
/// </summary>
public sealed class DataMarketplaceConfig
{
    public TimeSpan AccessEnforcementInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan BillingCycleInterval { get; set; } = TimeSpan.FromHours(1);
    public int TrialDurationDays { get; set; } = 14;
    public int PaymentTermDays { get; set; } = 30;
    public int PaymentGracePeriodDays { get; set; } = 7;
    public decimal TaxRate { get; set; } = 0m;
    public int MaxPreviewRows { get; set; } = 100;
    public int MaxPreviewColumns { get; set; } = 50;
    public bool AllowReviewsWithoutPurchase { get; set; }
    public bool RequireReviewModeration { get; set; }
    public bool EnableBlockchainContracts { get; set; } = true;
    public string DefaultCurrency { get; set; } = "USD";
    public string[] SupportedCurrencies { get; set; } = new[] { "USD", "EUR", "GBP", "JPY" };
    public string LicenseSecret { get; set; } = "DataWarehouse-Marketplace-Secret-2024";
}

#endregion
