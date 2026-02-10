using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.DataProtection
{
    /// <summary>
    /// Dynamic data masking (show last 4 digits, mask emails, etc.).
    /// </summary>
    public sealed class DataMaskingStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "dataprotection-masking";
        public override string StrategyName => "Data Masking";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        public string MaskCreditCard(string value) =>
            value.Length > 4 ? "****-****-****-" + value.Substring(value.Length - 4) : value;

        public string MaskEmail(string email) =>
            Regex.Replace(email, @"(?<=[\w]{1})[\w-\._\+%]*(?=[\w]{1}@)", m => new string('*', m.Length));

        public string MaskPhoneNumber(string phone) =>
            phone.Length > 4 ? "***-***-" + phone.Substring(phone.Length - 4) : phone;

        public string MaskGeneric(string value, int visibleChars = 4, char maskChar = '*') =>
            value.Length > visibleChars ? new string(maskChar, value.Length - visibleChars) + value.Substring(value.Length - visibleChars) : value;

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Data masking available",
                Metadata = new Dictionary<string, object> { ["MaskingEnabled"] = true }
            });
        }
    }
}
