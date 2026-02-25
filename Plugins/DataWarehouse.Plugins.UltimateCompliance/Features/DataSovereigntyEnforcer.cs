using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Enforces data residency and sovereignty requirements, blocking non-compliant
    /// data transfers across geographic boundaries.
    /// </summary>
    public sealed class DataSovereigntyEnforcer
    {
        private readonly Dictionary<string, RegionPolicy> _regionPolicies = new();
        private readonly Dictionary<string, List<string>> _allowedTransfers = new();

        /// <summary>
        /// Adds a data residency policy for a region.
        /// </summary>
        public void AddRegionPolicy(string region, RegionPolicy policy)
        {
            _regionPolicies[region] = policy;
        }

        /// <summary>
        /// Configures allowed data transfer between two regions.
        /// </summary>
        public void AllowTransfer(string sourceRegion, string destinationRegion, TransferMechanism mechanism)
        {
            var key = $"{sourceRegion}:{destinationRegion}";
            if (!_allowedTransfers.ContainsKey(key))
            {
                _allowedTransfers[key] = new List<string>();
            }

            if (!_allowedTransfers[key].Contains(mechanism.ToString()))
            {
                _allowedTransfers[key].Add(mechanism.ToString());
            }
        }

        /// <summary>
        /// Validates whether a data transfer is compliant with sovereignty rules.
        /// </summary>
        public TransferValidationResult ValidateTransfer(string sourceRegion, string destinationRegion, string dataClassification, TransferMechanism mechanism)
        {
            if (sourceRegion == destinationRegion)
            {
                return new TransferValidationResult
                {
                    IsAllowed = true,
                    Message = "Transfer within same region is allowed",
                    RequiredMechanisms = Array.Empty<string>()
                };
            }

            if (!_regionPolicies.TryGetValue(sourceRegion, out var sourcePolicy))
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"No policy defined for source region: {sourceRegion}",
                    RequiredMechanisms = Array.Empty<string>()
                };
            }

            if (!_regionPolicies.TryGetValue(destinationRegion, out var destPolicy))
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"No policy defined for destination region: {destinationRegion}",
                    RequiredMechanisms = Array.Empty<string>()
                };
            }

            if (sourcePolicy.ProhibitedDestinations.Contains(destinationRegion))
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"Transfer from {sourceRegion} to {destinationRegion} is explicitly prohibited",
                    RequiredMechanisms = Array.Empty<string>()
                };
            }

            var transferKey = $"{sourceRegion}:{destinationRegion}";
            if (!_allowedTransfers.ContainsKey(transferKey))
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"No transfer mechanism configured for {sourceRegion} to {destinationRegion}",
                    RequiredMechanisms = sourcePolicy.RequiredMechanisms.ToArray()
                };
            }

            var allowedMechanisms = _allowedTransfers[transferKey];
            var mechanismAllowed = allowedMechanisms.Contains(mechanism.ToString());

            if (!mechanismAllowed)
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"Transfer mechanism {mechanism} is not allowed. Allowed mechanisms: {string.Join(", ", allowedMechanisms)}",
                    RequiredMechanisms = allowedMechanisms.ToArray()
                };
            }

            var classificationAllowed = sourcePolicy.AllowedClassifications.Contains(dataClassification);
            if (!classificationAllowed && sourcePolicy.AllowedClassifications.Any())
            {
                return new TransferValidationResult
                {
                    IsAllowed = false,
                    Message = $"Data classification '{dataClassification}' is not allowed for transfer from {sourceRegion}",
                    RequiredMechanisms = allowedMechanisms.ToArray()
                };
            }

            return new TransferValidationResult
            {
                IsAllowed = true,
                Message = $"Transfer allowed using {mechanism}",
                RequiredMechanisms = Array.Empty<string>()
            };
        }

        /// <summary>
        /// Gets all allowed destination regions for a source region.
        /// </summary>
        public IReadOnlyList<string> GetAllowedDestinations(string sourceRegion)
        {
            return _allowedTransfers.Keys
                .Where(k => k.StartsWith($"{sourceRegion}:"))
                .Select(k => k.Split(':')[1])
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Gets the region policy for a specific region.
        /// </summary>
        public RegionPolicy? GetRegionPolicy(string region)
        {
            _regionPolicies.TryGetValue(region, out var policy);
            return policy;
        }
    }

    /// <summary>
    /// Data residency policy for a geographic region.
    /// </summary>
    public sealed class RegionPolicy
    {
        public required string Region { get; init; }
        public required string RegulatoryFramework { get; init; }
        public IReadOnlyList<string> ProhibitedDestinations { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RequiredMechanisms { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AllowedClassifications { get; init; } = Array.Empty<string>();
        public bool RequiresLocalProcessing { get; init; }
        public bool AllowsCloudStorage { get; init; } = true;
    }

    /// <summary>
    /// Result of a data transfer validation check.
    /// </summary>
    public sealed class TransferValidationResult
    {
        public required bool IsAllowed { get; init; }
        public required string Message { get; init; }
        public required string[] RequiredMechanisms { get; init; }
    }

    /// <summary>
    /// Mechanisms for compliant cross-border data transfer.
    /// </summary>
    public enum TransferMechanism
    {
        StandardContractualClauses,
        BindingCorporateRules,
        AdequacyDecision,
        ConsentBased,
        PublicInterest,
        LegalClaims,
        VitalInterests
    }
}
