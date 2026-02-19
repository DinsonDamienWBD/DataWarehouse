---
phase: 58-zero-gravity-storage
plan: "06"
subsystem: Storage/Billing
tags: [cloud-billing, aws, azure, gcp, cost-explorer, rest-api]
dependency_graph:
  requires: ["58-01"]
  provides: ["AwsCostExplorerProvider", "AzureCostManagementProvider", "GcpBillingProvider", "BillingProviderFactory"]
  affects: ["58-09"]
tech_stack:
  added: []
  patterns: ["AWS SigV4 signing", "OAuth2 client_credentials", "JWT RS256 service account", "HttpClient-only cloud API"]
key_files:
  created:
    - DataWarehouse.SDK/Storage/Billing/AwsCostExplorerProvider.cs
    - DataWarehouse.SDK/Storage/Billing/AzureCostManagementProvider.cs
    - DataWarehouse.SDK/Storage/Billing/GcpBillingProvider.cs
    - DataWarehouse.SDK/Storage/Billing/BillingProviderFactory.cs
  modified: []
decisions:
  - "Used raw HttpClient + System.Text.Json for all cloud APIs to avoid NuGet SDK dependencies"
  - "Implemented minimal AWS SigV4 signing inline rather than adding AWS SDK package"
  - "JWT RS256 signing for GCP uses System.Security.Cryptography RSA, no external library"
metrics:
  duration: "4m 32s"
  completed: "2026-02-19"
  tasks_completed: 2
  tasks_total: 2
  files_created: 4
  files_modified: 0
---

# Phase 58 Plan 06: Cloud Billing Providers Summary

Three cloud billing providers with factory using raw HttpClient REST calls and native crypto for auth signing.

## What Was Built

### Task 1: AWS, Azure, GCP Billing Providers (508e14fe)

**AwsCostExplorerProvider** -- Full AWS Cost Explorer integration with minimal SigV4 signing:
- HMAC-SHA256 signature chain: DateKey -> DateRegionKey -> DateRegionServiceKey -> SigningKey -> Signature
- GetCostAndUsage (Cost Explorer), DescribeSpotPriceHistory (EC2), DescribeReservedInstances (EC2), GetCostForecast (Cost Explorer), GetCallerIdentity (STS)
- Service categorization into Storage/Compute/Transfer/Other

**AzureCostManagementProvider** -- Azure Cost Management with OAuth2 client_credentials:
- Token acquisition from login.microsoftonline.com with SemaphoreSlim-guarded caching
- Cost Management query API for billing reports, retail pricing API (public, no auth) for spot pricing
- Reservation Orders API for reserved capacity, Forecast API for projections

**GcpBillingProvider** -- GCP Cloud Billing with JWT/RS256 service account auth:
- JWT construction with iss/scope/aud/iat/exp claims, RS256 signing using System.Security.Cryptography.RSA
- Cloud Billing API for service/SKU pricing, Compute API for committed use discounts
- Billing Budgets API for cost forecasting, PEM private key parsing for RSA import

All providers share: exponential backoff retry (3 attempts, 200ms base), 429/5xx retryable detection, CancellationToken propagation, environment variable credential fallback.

### Task 2: BillingProviderFactory (43465038)

- Static factory with `Create(CloudProvider, HttpClient?, IDictionary?)` method
- Credential resolution: explicit config > environment variables
- GCP special case: GOOGLE_APPLICATION_CREDENTIALS reads file content if path exists
- `CreateAllAvailableAsync` probes all 3 providers and returns those with valid credentials

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- All 4 files exist in DataWarehouse.SDK/Storage/Billing/
- SDK builds with 0 errors, 0 warnings
- No new NuGet package references added (still 11 packages)
- All 3 providers implement IBillingProvider interface
- All types marked with [SdkCompatibility("5.0.0", Notes = "Phase 58: Cloud billing providers")]

## Self-Check: PASSED

- FOUND: DataWarehouse.SDK/Storage/Billing/AwsCostExplorerProvider.cs
- FOUND: DataWarehouse.SDK/Storage/Billing/AzureCostManagementProvider.cs
- FOUND: DataWarehouse.SDK/Storage/Billing/GcpBillingProvider.cs
- FOUND: DataWarehouse.SDK/Storage/Billing/BillingProviderFactory.cs
- FOUND: commit 508e14fe
- FOUND: commit 43465038
