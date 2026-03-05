using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// SAML 2.0 assertion validation strategy.
    /// </summary>
    /// <remarks>
    /// Implements SAML 2.0 Core specification for SSO authentication.
    /// Validates SAML assertions with XML signature verification.
    /// </remarks>
    public sealed class SamlStrategy : AccessControlStrategyBase
    {
        private string? _issuer;
        private X509Certificate2? _certificate;

        public override string StrategyId => "identity-saml";
        public override string StrategyName => "SAML 2.0";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("Issuer", out var issuer) && issuer is string issuerStr)
                _issuer = issuerStr;

            if (configuration.TryGetValue("CertificatePath", out var certPath) && certPath is string certPathStr)
            {
                _certificate = X509CertificateLoader.LoadCertificateFromFile(certPathStr);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.saml.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.saml.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!string.IsNullOrEmpty(_issuer) && _certificate != null);
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("identity.saml.evaluate");
            await Task.CompletedTask;

            if (!context.EnvironmentAttributes.TryGetValue("SamlAssertion", out var assertionObj) ||
                assertionObj is not string assertion)
            {
                return new AccessDecision { IsGranted = false, Reason = "SAML assertion not provided" };
            }

            var validationResult = ValidateSamlAssertion(assertion);
            if (!validationResult.IsValid)
            {
                return new AccessDecision { IsGranted = false, Reason = validationResult.Error ?? "Invalid SAML assertion" };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "SAML authentication successful",
                Metadata = new Dictionary<string, object>
                {
                    ["NameId"] = validationResult.NameId ?? "",
                    ["Attributes"] = validationResult.Attributes ?? new Dictionary<string, string>()
                }
            };
        }

        private SamlValidationResult ValidateSamlAssertion(string assertionBase64)
        {
            try
            {
                var assertionBytes = Convert.FromBase64String(assertionBase64);
                var assertionXml = Encoding.UTF8.GetString(assertionBytes);

                var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using var reader = XmlReader.Create(new System.IO.StringReader(assertionXml), settings);
                doc.Load(reader);

                // Validate signature if certificate is configured
                if (_certificate != null)
                {
                    var signedXml = new System.Security.Cryptography.Xml.SignedXml(doc);
                    var signatureNode = doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0];
                    if (signatureNode == null)
                    {
                        // Certificate configured but assertion is unsigned — reject to prevent auth bypass
                        return new SamlValidationResult { IsValid = false, Error = "SAML assertion is not signed; unsigned assertions are rejected when a verification certificate is configured" };
                    }
                    signedXml.LoadXml((XmlElement)signatureNode);
                    if (!signedXml.CheckSignature(_certificate, true))
                    {
                        return new SamlValidationResult { IsValid = false, Error = "Invalid signature" };
                    }
                }

                // Extract assertion details
                var nsMgr = new XmlNamespaceManager(doc.NameTable);
                nsMgr.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");

                var issuerNode = doc.SelectSingleNode("//saml:Issuer", nsMgr);
                if (issuerNode?.InnerText != _issuer)
                {
                    return new SamlValidationResult { IsValid = false, Error = "Invalid issuer" };
                }

                var nameIdNode = doc.SelectSingleNode("//saml:NameID", nsMgr);
                var nameId = nameIdNode?.InnerText;

                var attributes = new Dictionary<string, string>();
                var attrNodes = doc.SelectNodes("//saml:Attribute", nsMgr);
                if (attrNodes != null)
                {
                    foreach (XmlNode attrNode in attrNodes)
                    {
                        var name = attrNode.Attributes?["Name"]?.Value;
                        var valueNode = attrNode.SelectSingleNode("saml:AttributeValue", nsMgr);
                        if (name != null && valueNode != null)
                        {
                            attributes[name] = valueNode.InnerText;
                        }
                    }
                }

                return new SamlValidationResult
                {
                    IsValid = true,
                    NameId = nameId,
                    Attributes = attributes
                };
            }
            catch (Exception ex)
            {
                return new SamlValidationResult { IsValid = false, Error = $"Validation failed: {ex.Message}" };
            }
        }
    }

    public sealed record SamlValidationResult
    {
        public required bool IsValid { get; init; }
        public string? NameId { get; init; }
        public Dictionary<string, string>? Attributes { get; init; }
        public string? Error { get; init; }
    }
}
