using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages cert-manager integration for DataWarehouse Kubernetes resources.
/// Provides Certificate CRD creation, Issuer/ClusterIssuer management, TLS secret rotation,
/// ACME integration, and self-signed CA support.
/// </summary>
public sealed class CertManagerIntegration
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the CertManagerIntegration.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public CertManagerIntegration(IKubernetesClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Creates a Certificate resource.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">Certificate name.</param>
    /// <param name="config">Certificate configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the Certificate creation.</returns>
    public async Task<CertManagerResult> CreateCertificateAsync(
        string namespaceName,
        string name,
        CertificateConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var certificate = new Certificate
        {
            ApiVersion = "cert-manager.io/v1",
            Kind = "Certificate",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Spec = new CertificateSpec
            {
                SecretName = config.SecretName ?? $"{name}-tls",
                IssuerRef = new IssuerRef
                {
                    Name = config.IssuerName,
                    Kind = config.IssuerKind.ToString(),
                    Group = "cert-manager.io"
                },
                CommonName = config.CommonName,
                DnsNames = config.DnsNames,
                IpAddresses = config.IpAddresses,
                UriSans = config.UriSans,
                EmailAddresses = config.EmailAddresses,
                Duration = config.Duration ?? "2160h", // 90 days
                RenewBefore = config.RenewBefore ?? "360h", // 15 days
                IsCA = config.IsCA,
                Usages = config.Usages ?? new List<string>
                {
                    "server auth",
                    "client auth"
                },
                PrivateKey = config.PrivateKey != null ? new PrivateKeySpec
                {
                    Algorithm = config.PrivateKey.Algorithm,
                    Size = config.PrivateKey.Size,
                    Encoding = config.PrivateKey.Encoding,
                    RotationPolicy = config.PrivateKey.RotationPolicy
                } : null,
                Subject = config.Subject != null ? new X509Subject
                {
                    Organizations = config.Subject.Organizations,
                    Countries = config.Subject.Countries,
                    OrganizationalUnits = config.Subject.OrganizationalUnits,
                    Localities = config.Subject.Localities,
                    Provinces = config.Subject.Provinces,
                    StreetAddresses = config.Subject.StreetAddresses,
                    PostalCodes = config.Subject.PostalCodes,
                    SerialNumber = config.Subject.SerialNumber
                } : null,
                SecretTemplate = config.SecretTemplate != null ? new SecretTemplate
                {
                    Annotations = config.SecretTemplate.Annotations,
                    Labels = config.SecretTemplate.Labels
                } : null,
                AdditionalOutputFormats = config.AdditionalOutputFormats?.Select(f => new CertificateAdditionalOutputFormat
                {
                    Type = f.Type
                }).ToList(),
                RevisionHistoryLimit = config.RevisionHistoryLimit
            }
        };

        var json = JsonSerializer.Serialize(certificate, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "cert-manager.io/v1",
            "certificates",
            namespaceName,
            name,
            json,
            ct);

        return new CertManagerResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "Certificate",
            SecretName = config.SecretName ?? $"{name}-tls",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a namespace-scoped Issuer.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">Issuer name.</param>
    /// <param name="config">Issuer configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the Issuer creation.</returns>
    public async Task<CertManagerResult> CreateIssuerAsync(
        string namespaceName,
        string name,
        IssuerConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var issuer = new Issuer
        {
            ApiVersion = "cert-manager.io/v1",
            Kind = "Issuer",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Spec = BuildIssuerSpec(config)
        };

        var json = JsonSerializer.Serialize(issuer, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "cert-manager.io/v1",
            "issuers",
            namespaceName,
            name,
            json,
            ct);

        return new CertManagerResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "Issuer",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a cluster-scoped ClusterIssuer.
    /// </summary>
    /// <param name="name">ClusterIssuer name.</param>
    /// <param name="config">Issuer configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ClusterIssuer creation.</returns>
    public async Task<CertManagerResult> CreateClusterIssuerAsync(
        string name,
        IssuerConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var clusterIssuer = new ClusterIssuer
        {
            ApiVersion = "cert-manager.io/v1",
            Kind = "ClusterIssuer",
            Metadata = new ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Spec = BuildIssuerSpec(config)
        };

        var json = JsonSerializer.Serialize(clusterIssuer, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "cert-manager.io/v1",
            "clusterissuers",
            null!,
            name,
            json,
            ct);

        return new CertManagerResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "ClusterIssuer",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a self-signed CA issuer and root certificate.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="caName">CA name.</param>
    /// <param name="config">CA configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the CA setup.</returns>
    public async Task<CaSetupResult> CreateSelfSignedCaAsync(
        string namespaceName,
        string caName,
        SelfSignedCaConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(caName);
        ArgumentNullException.ThrowIfNull(config);

        var results = new List<CertManagerResult>();

        // 1. Create self-signed issuer for the CA certificate
        var selfSignedIssuer = await CreateIssuerAsync(
            namespaceName,
            $"{caName}-selfsigned",
            new IssuerConfig { Type = IssuerType.SelfSigned },
            ct);
        results.Add(selfSignedIssuer);

        // 2. Create the CA certificate using the self-signed issuer
        var caCert = await CreateCertificateAsync(
            namespaceName,
            $"{caName}-ca",
            new CertificateConfig
            {
                SecretName = $"{caName}-ca-secret",
                IssuerName = $"{caName}-selfsigned",
                IssuerKind = IssuerKind.Issuer,
                CommonName = config.CommonName ?? $"{caName} CA",
                IsCA = true,
                Duration = config.CaDuration ?? "87600h", // 10 years
                RenewBefore = config.CaRenewBefore ?? "2160h", // 90 days
                Subject = config.Subject,
                PrivateKey = new PrivateKeyConfig
                {
                    Algorithm = config.KeyAlgorithm ?? "RSA",
                    Size = config.KeySize ?? 4096
                }
            },
            ct);
        results.Add(caCert);

        // 3. Create CA issuer using the CA secret
        var caIssuer = await CreateIssuerAsync(
            namespaceName,
            caName,
            new IssuerConfig
            {
                Type = IssuerType.CA,
                CaSecretName = $"{caName}-ca-secret"
            },
            ct);
        results.Add(caIssuer);

        return new CaSetupResult
        {
            Success = results.All(r => r.Success),
            IssuerName = caName,
            CaSecretName = $"{caName}-ca-secret",
            Results = results,
            Message = results.All(r => r.Success)
                ? "Self-signed CA created successfully"
                : "Failed to create self-signed CA"
        };
    }

    /// <summary>
    /// Creates an ACME issuer (e.g., Let's Encrypt).
    /// </summary>
    /// <param name="name">Issuer name.</param>
    /// <param name="config">ACME configuration.</param>
    /// <param name="clusterScoped">Whether to create a ClusterIssuer.</param>
    /// <param name="namespaceName">Namespace for namespace-scoped issuer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ACME issuer creation.</returns>
    public async Task<CertManagerResult> CreateAcmeIssuerAsync(
        string name,
        AcmeIssuerConfig config,
        bool clusterScoped = true,
        string? namespaceName = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var issuerConfig = new IssuerConfig
        {
            Type = IssuerType.ACME,
            AcmeServer = config.Server ?? "https://acme-v02.api.letsencrypt.org/directory",
            AcmeEmail = config.Email,
            AcmePrivateKeySecretName = config.PrivateKeySecretName ?? $"{name}-acme-key",
            AcmeSolvers = config.Solvers
        };

        if (clusterScoped)
        {
            return await CreateClusterIssuerAsync(name, issuerConfig, ct);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            return await CreateIssuerAsync(namespaceName!, name, issuerConfig, ct);
        }
    }

    /// <summary>
    /// Triggers certificate renewal.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="certificateName">Certificate name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the renewal trigger.</returns>
    public async Task<CertManagerResult> TriggerCertificateRenewalAsync(
        string namespaceName,
        string certificateName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateName);

        // Trigger renewal by updating the certificate's annotation
        var patch = new
        {
            metadata = new
            {
                annotations = new Dictionary<string, string>
                {
                    ["cert-manager.io/renew-trigger"] = DateTime.UtcNow.ToString("O")
                }
            }
        };

        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);
        var result = await _client.PatchResourceAsync(
            "cert-manager.io/v1",
            "certificates",
            namespaceName,
            certificateName,
            patchJson,
            ct: ct);

        return new CertManagerResult
        {
            Success = result.Success,
            ResourceName = certificateName,
            ResourceKind = "Certificate",
            Message = result.Success ? "Certificate renewal triggered" : result.Message
        };
    }

    /// <summary>
    /// Gets certificate status.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="certificateName">Certificate name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Certificate status information.</returns>
    public async Task<CertificateStatus> GetCertificateStatusAsync(
        string namespaceName,
        string certificateName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateName);

        var result = await _client.GetResourceAsync(
            "cert-manager.io/v1",
            "certificates",
            namespaceName,
            certificateName,
            ct);

        if (!result.Found)
        {
            return new CertificateStatus
            {
                Found = false,
                Message = "Certificate not found"
            };
        }

        var cert = JsonSerializer.Deserialize<JsonElement>(result.Json!);
        var status = cert.TryGetProperty("status", out var s) ? s : default;

        var conditions = new List<CertificateCondition>();
        if (status.TryGetProperty("conditions", out var conds))
        {
            foreach (var cond in conds.EnumerateArray())
            {
                conditions.Add(new CertificateCondition
                {
                    Type = cond.GetProperty("type").GetString() ?? "",
                    Status = cond.GetProperty("status").GetString() ?? "",
                    Reason = cond.TryGetProperty("reason", out var r) ? r.GetString() : null,
                    Message = cond.TryGetProperty("message", out var m) ? m.GetString() : null,
                    LastTransitionTime = cond.TryGetProperty("lastTransitionTime", out var t)
                        ? DateTime.Parse(t.GetString()!)
                        : null
                });
            }
        }

        var isReady = conditions.Any(c => c.Type == "Ready" && c.Status == "True");
        var notAfter = status.TryGetProperty("notAfter", out var na)
            ? DateTime.Parse(na.GetString()!)
            : (DateTime?)null;
        var notBefore = status.TryGetProperty("notBefore", out var nb)
            ? DateTime.Parse(nb.GetString()!)
            : (DateTime?)null;
        var renewalTime = status.TryGetProperty("renewalTime", out var rt)
            ? DateTime.Parse(rt.GetString()!)
            : (DateTime?)null;

        return new CertificateStatus
        {
            Found = true,
            Ready = isReady,
            NotBefore = notBefore,
            NotAfter = notAfter,
            RenewalTime = renewalTime,
            Conditions = conditions,
            Message = isReady ? "Certificate is ready" : "Certificate is not ready"
        };
    }

    /// <summary>
    /// Sets up complete TLS for a service.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="config">TLS setup configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the TLS setup.</returns>
    public async Task<TlsSetupResult> SetupServiceTlsAsync(
        string namespaceName,
        string serviceName,
        ServiceTlsConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(config);

        var results = new List<CertManagerResult>();

        // Determine issuer
        string issuerName;
        IssuerKind issuerKind;

        if (config.CreateSelfSignedCa)
        {
            // Create a self-signed CA for this service
            var caResult = await CreateSelfSignedCaAsync(
                namespaceName,
                $"{serviceName}-ca",
                new SelfSignedCaConfig
                {
                    CommonName = $"{serviceName} CA"
                },
                ct);
            results.AddRange(caResult.Results);
            issuerName = $"{serviceName}-ca";
            issuerKind = IssuerKind.Issuer;
        }
        else
        {
            issuerName = config.IssuerName ?? "letsencrypt-prod";
            issuerKind = config.IssuerKind ?? IssuerKind.ClusterIssuer;
        }

        // Create certificate for the service
        var certResult = await CreateCertificateAsync(
            namespaceName,
            $"{serviceName}-tls",
            new CertificateConfig
            {
                SecretName = $"{serviceName}-tls",
                IssuerName = issuerName,
                IssuerKind = issuerKind,
                CommonName = config.CommonName ?? $"{serviceName}.{namespaceName}.svc.cluster.local",
                DnsNames = config.DnsNames ?? new List<string>
                {
                    serviceName,
                    $"{serviceName}.{namespaceName}",
                    $"{serviceName}.{namespaceName}.svc",
                    $"{serviceName}.{namespaceName}.svc.cluster.local"
                },
                Duration = config.Duration,
                RenewBefore = config.RenewBefore,
                Usages = new List<string> { "server auth", "client auth" }
            },
            ct);
        results.Add(certResult);

        return new TlsSetupResult
        {
            Success = results.All(r => r.Success),
            CertificateName = $"{serviceName}-tls",
            SecretName = $"{serviceName}-tls",
            IssuerName = issuerName,
            Results = results,
            Message = results.All(r => r.Success)
                ? "TLS setup completed successfully"
                : "Failed to setup TLS"
        };
    }

    /// <summary>
    /// Deletes certificate resources.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="certificateName">Certificate name.</param>
    /// <param name="deleteSecret">Whether to also delete the TLS secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the deletion.</returns>
    public async Task<CertManagerResult> DeleteCertificateAsync(
        string namespaceName,
        string certificateName,
        bool deleteSecret = false,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // Get certificate to find secret name
        string? secretName = null;
        if (deleteSecret)
        {
            var certResult = await _client.GetResourceAsync(
                "cert-manager.io/v1",
                "certificates",
                namespaceName,
                certificateName,
                ct);

            if (certResult.Found)
            {
                var cert = JsonSerializer.Deserialize<JsonElement>(certResult.Json!);
                if (cert.TryGetProperty("spec", out var spec) &&
                    spec.TryGetProperty("secretName", out var sn))
                {
                    secretName = sn.GetString();
                }
            }
        }

        // Delete certificate
        var delResult = await _client.DeleteResourceAsync(
            "cert-manager.io/v1",
            "certificates",
            namespaceName,
            certificateName,
            ct);
        if (!delResult.Success && !delResult.NotFound)
            errors.Add($"Certificate: {delResult.Message}");

        // Delete secret if requested
        if (deleteSecret && !string.IsNullOrEmpty(secretName))
        {
            var secretResult = await _client.DeleteResourceAsync(
                "v1",
                "secrets",
                namespaceName,
                secretName,
                ct);
            if (!secretResult.Success && !secretResult.NotFound)
                errors.Add($"Secret: {secretResult.Message}");
        }

        return new CertManagerResult
        {
            Success = errors.Count == 0,
            ResourceName = certificateName,
            ResourceKind = "Certificate",
            Message = errors.Count > 0 ? string.Join("; ", errors) : "Certificate deleted"
        };
    }

    private IssuerSpec BuildIssuerSpec(IssuerConfig config)
    {
        return config.Type switch
        {
            IssuerType.SelfSigned => new IssuerSpec
            {
                SelfSigned = new SelfSignedIssuer { }
            },
            IssuerType.CA => new IssuerSpec
            {
                Ca = new CaIssuer
                {
                    SecretName = config.CaSecretName!
                }
            },
            IssuerType.ACME => new IssuerSpec
            {
                Acme = new AcmeIssuer
                {
                    Server = config.AcmeServer!,
                    Email = config.AcmeEmail!,
                    PrivateKeySecretRef = new SecretKeySelector
                    {
                        Name = config.AcmePrivateKeySecretName!
                    },
                    Solvers = config.AcmeSolvers?.Select(s => new AcmeSolver
                    {
                        Http01 = s.Type == SolverType.HTTP01 ? new Http01Solver
                        {
                            Ingress = s.IngressClass != null ? new IngressSolver
                            {
                                Class = s.IngressClass
                            } : null
                        } : null,
                        Dns01 = s.Type == SolverType.DNS01 ? new Dns01Solver
                        {
                            Route53 = s.Route53 != null ? new Route53Solver
                            {
                                Region = s.Route53.Region,
                                HostedZoneID = s.Route53.HostedZoneId
                            } : null,
                            CloudDNS = s.CloudDns != null ? new CloudDnsSolver
                            {
                                Project = s.CloudDns.Project
                            } : null,
                            AzureDNS = s.AzureDns != null ? new AzureDnsSolver
                            {
                                SubscriptionID = s.AzureDns.SubscriptionId,
                                ResourceGroupName = s.AzureDns.ResourceGroupName,
                                HostedZoneName = s.AzureDns.HostedZoneName
                            } : null
                        } : null,
                        Selector = s.Selector != null ? new SolverSelector
                        {
                            DnsNames = s.Selector.DnsNames,
                            DnsZones = s.Selector.DnsZones,
                            MatchLabels = s.Selector.MatchLabels
                        } : null
                    }).ToList()
                }
            },
            IssuerType.Vault => new IssuerSpec
            {
                Vault = new VaultIssuer
                {
                    Server = config.VaultServer!,
                    Path = config.VaultPath!,
                    Auth = new VaultAuth
                    {
                        Kubernetes = config.VaultKubernetesAuth != null ? new VaultKubernetesAuth
                        {
                            Role = config.VaultKubernetesAuth.Role,
                            MountPath = config.VaultKubernetesAuth.MountPath,
                            SecretRef = config.VaultKubernetesAuth.SecretRef != null ? new SecretKeySelector
                            {
                                Name = config.VaultKubernetesAuth.SecretRef,
                                Key = "token"
                            } : null
                        } : null
                    }
                }
            },
            _ => new IssuerSpec { SelfSigned = new SelfSignedIssuer() }
        };
    }
}

#region Configuration Classes

/// <summary>Configuration for Certificate creation.</summary>
public sealed class CertificateConfig
{
    /// <summary>Name of the secret to store the certificate.</summary>
    public string? SecretName { get; set; }

    /// <summary>Name of the issuer to use.</summary>
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>Kind of the issuer (Issuer or ClusterIssuer).</summary>
    public IssuerKind IssuerKind { get; set; } = IssuerKind.ClusterIssuer;

    /// <summary>Common name for the certificate.</summary>
    public string? CommonName { get; set; }

    /// <summary>DNS names for the certificate.</summary>
    public List<string>? DnsNames { get; set; }

    /// <summary>IP addresses for the certificate.</summary>
    public List<string>? IpAddresses { get; set; }

    /// <summary>URI SANs for the certificate.</summary>
    public List<string>? UriSans { get; set; }

    /// <summary>Email addresses for the certificate.</summary>
    public List<string>? EmailAddresses { get; set; }

    /// <summary>Duration of the certificate validity.</summary>
    public string? Duration { get; set; }

    /// <summary>Time before expiry to renew.</summary>
    public string? RenewBefore { get; set; }

    /// <summary>Whether this is a CA certificate.</summary>
    public bool IsCA { get; set; }

    /// <summary>Key usages.</summary>
    public List<string>? Usages { get; set; }

    /// <summary>Private key configuration.</summary>
    public PrivateKeyConfig? PrivateKey { get; set; }

    /// <summary>Subject configuration.</summary>
    public SubjectConfig? Subject { get; set; }

    /// <summary>Secret template.</summary>
    public SecretTemplateConfig? SecretTemplate { get; set; }

    /// <summary>Additional output formats.</summary>
    public List<OutputFormatConfig>? AdditionalOutputFormats { get; set; }

    /// <summary>Revision history limit.</summary>
    public int? RevisionHistoryLimit { get; set; }
}

/// <summary>Issuer kind enumeration.</summary>
public enum IssuerKind
{
    /// <summary>Namespace-scoped Issuer.</summary>
    Issuer,
    /// <summary>Cluster-scoped ClusterIssuer.</summary>
    ClusterIssuer
}

/// <summary>Private key configuration.</summary>
public sealed class PrivateKeyConfig
{
    /// <summary>Algorithm (RSA, ECDSA, Ed25519).</summary>
    public string Algorithm { get; set; } = "RSA";

    /// <summary>Key size (for RSA: 2048, 4096; for ECDSA: 256, 384, 521).</summary>
    public int Size { get; set; } = 2048;

    /// <summary>Encoding format (PKCS1, PKCS8).</summary>
    public string? Encoding { get; set; }

    /// <summary>Rotation policy (Always, Never).</summary>
    public string? RotationPolicy { get; set; }
}

/// <summary>Subject configuration.</summary>
public sealed class SubjectConfig
{
    /// <summary>Organizations.</summary>
    public List<string>? Organizations { get; set; }

    /// <summary>Countries.</summary>
    public List<string>? Countries { get; set; }

    /// <summary>Organizational units.</summary>
    public List<string>? OrganizationalUnits { get; set; }

    /// <summary>Localities.</summary>
    public List<string>? Localities { get; set; }

    /// <summary>Provinces/States.</summary>
    public List<string>? Provinces { get; set; }

    /// <summary>Street addresses.</summary>
    public List<string>? StreetAddresses { get; set; }

    /// <summary>Postal codes.</summary>
    public List<string>? PostalCodes { get; set; }

    /// <summary>Serial number.</summary>
    public string? SerialNumber { get; set; }
}

/// <summary>Secret template configuration.</summary>
public sealed class SecretTemplateConfig
{
    /// <summary>Annotations for the secret.</summary>
    public Dictionary<string, string>? Annotations { get; set; }

    /// <summary>Labels for the secret.</summary>
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>Output format configuration.</summary>
public sealed class OutputFormatConfig
{
    /// <summary>Type (DER, CombinedPEM).</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>Issuer configuration.</summary>
public sealed class IssuerConfig
{
    /// <summary>Type of issuer.</summary>
    public IssuerType Type { get; set; } = IssuerType.SelfSigned;

    /// <summary>Secret name for CA issuer.</summary>
    public string? CaSecretName { get; set; }

    /// <summary>ACME server URL.</summary>
    public string? AcmeServer { get; set; }

    /// <summary>Email for ACME registration.</summary>
    public string? AcmeEmail { get; set; }

    /// <summary>Private key secret name for ACME.</summary>
    public string? AcmePrivateKeySecretName { get; set; }

    /// <summary>ACME solvers configuration.</summary>
    public List<AcmeSolverConfig>? AcmeSolvers { get; set; }

    /// <summary>Vault server URL.</summary>
    public string? VaultServer { get; set; }

    /// <summary>Vault PKI path.</summary>
    public string? VaultPath { get; set; }

    /// <summary>Vault Kubernetes auth configuration.</summary>
    public VaultKubernetesAuthConfig? VaultKubernetesAuth { get; set; }
}

/// <summary>Issuer type enumeration.</summary>
public enum IssuerType
{
    /// <summary>Self-signed issuer.</summary>
    SelfSigned,
    /// <summary>CA issuer.</summary>
    CA,
    /// <summary>ACME issuer (Let's Encrypt).</summary>
    ACME,
    /// <summary>HashiCorp Vault issuer.</summary>
    Vault,
    /// <summary>Venafi issuer.</summary>
    Venafi
}

/// <summary>ACME solver configuration.</summary>
public sealed class AcmeSolverConfig
{
    /// <summary>Solver type.</summary>
    public SolverType Type { get; set; } = SolverType.HTTP01;

    /// <summary>Ingress class for HTTP01 solver.</summary>
    public string? IngressClass { get; set; }

    /// <summary>Route53 configuration for DNS01.</summary>
    public Route53Config? Route53 { get; set; }

    /// <summary>Google Cloud DNS configuration for DNS01.</summary>
    public CloudDnsConfig? CloudDns { get; set; }

    /// <summary>Azure DNS configuration for DNS01.</summary>
    public AzureDnsConfig? AzureDns { get; set; }

    /// <summary>Selector for this solver.</summary>
    public SolverSelectorConfig? Selector { get; set; }
}

/// <summary>ACME solver type.</summary>
public enum SolverType
{
    /// <summary>HTTP-01 challenge.</summary>
    HTTP01,
    /// <summary>DNS-01 challenge.</summary>
    DNS01
}

/// <summary>Route53 configuration.</summary>
public sealed class Route53Config
{
    /// <summary>AWS region.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Hosted zone ID.</summary>
    public string? HostedZoneId { get; set; }
}

/// <summary>Google Cloud DNS configuration.</summary>
public sealed class CloudDnsConfig
{
    /// <summary>GCP project ID.</summary>
    public string Project { get; set; } = string.Empty;
}

/// <summary>Azure DNS configuration.</summary>
public sealed class AzureDnsConfig
{
    /// <summary>Azure subscription ID.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Resource group name.</summary>
    public string ResourceGroupName { get; set; } = string.Empty;

    /// <summary>Hosted zone name.</summary>
    public string HostedZoneName { get; set; } = string.Empty;
}

/// <summary>Solver selector configuration.</summary>
public sealed class SolverSelectorConfig
{
    /// <summary>DNS names to match.</summary>
    public List<string>? DnsNames { get; set; }

    /// <summary>DNS zones to match.</summary>
    public List<string>? DnsZones { get; set; }

    /// <summary>Match labels.</summary>
    public Dictionary<string, string>? MatchLabels { get; set; }
}

/// <summary>Vault Kubernetes auth configuration.</summary>
public sealed class VaultKubernetesAuthConfig
{
    /// <summary>Vault role.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Mount path.</summary>
    public string? MountPath { get; set; }

    /// <summary>Secret reference for token.</summary>
    public string? SecretRef { get; set; }
}

/// <summary>Self-signed CA configuration.</summary>
public sealed class SelfSignedCaConfig
{
    /// <summary>Common name for the CA.</summary>
    public string? CommonName { get; set; }

    /// <summary>CA certificate duration.</summary>
    public string? CaDuration { get; set; }

    /// <summary>CA certificate renew before time.</summary>
    public string? CaRenewBefore { get; set; }

    /// <summary>Key algorithm.</summary>
    public string? KeyAlgorithm { get; set; }

    /// <summary>Key size.</summary>
    public int? KeySize { get; set; }

    /// <summary>Subject configuration.</summary>
    public SubjectConfig? Subject { get; set; }
}

/// <summary>ACME issuer configuration.</summary>
public sealed class AcmeIssuerConfig
{
    /// <summary>ACME server URL (default: Let's Encrypt production).</summary>
    public string? Server { get; set; }

    /// <summary>Email for registration.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Private key secret name.</summary>
    public string? PrivateKeySecretName { get; set; }

    /// <summary>ACME solvers.</summary>
    public List<AcmeSolverConfig> Solvers { get; set; } = new();
}

/// <summary>Service TLS configuration.</summary>
public sealed class ServiceTlsConfig
{
    /// <summary>Whether to create a self-signed CA.</summary>
    public bool CreateSelfSignedCa { get; set; }

    /// <summary>Existing issuer name.</summary>
    public string? IssuerName { get; set; }

    /// <summary>Existing issuer kind.</summary>
    public IssuerKind? IssuerKind { get; set; }

    /// <summary>Common name for the certificate.</summary>
    public string? CommonName { get; set; }

    /// <summary>DNS names for the certificate.</summary>
    public List<string>? DnsNames { get; set; }

    /// <summary>Certificate duration.</summary>
    public string? Duration { get; set; }

    /// <summary>Renew before time.</summary>
    public string? RenewBefore { get; set; }
}

/// <summary>Result of a cert-manager operation.</summary>
public sealed class CertManagerResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the resource.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Kind of the resource.</summary>
    public string ResourceKind { get; set; } = string.Empty;

    /// <summary>Name of the TLS secret.</summary>
    public string? SecretName { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of a CA setup operation.</summary>
public sealed class CaSetupResult
{
    /// <summary>Whether the setup succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the CA issuer.</summary>
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>Name of the CA secret.</summary>
    public string CaSecretName { get; set; } = string.Empty;

    /// <summary>Individual operation results.</summary>
    public List<CertManagerResult> Results { get; set; } = new();

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of a TLS setup operation.</summary>
public sealed class TlsSetupResult
{
    /// <summary>Whether the setup succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the certificate.</summary>
    public string CertificateName { get; set; } = string.Empty;

    /// <summary>Name of the TLS secret.</summary>
    public string SecretName { get; set; } = string.Empty;

    /// <summary>Name of the issuer used.</summary>
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>Individual operation results.</summary>
    public List<CertManagerResult> Results { get; set; } = new();

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Certificate status information.</summary>
public sealed class CertificateStatus
{
    /// <summary>Whether the certificate was found.</summary>
    public bool Found { get; set; }

    /// <summary>Whether the certificate is ready.</summary>
    public bool Ready { get; set; }

    /// <summary>Not before time.</summary>
    public DateTime? NotBefore { get; set; }

    /// <summary>Not after (expiry) time.</summary>
    public DateTime? NotAfter { get; set; }

    /// <summary>Next renewal time.</summary>
    public DateTime? RenewalTime { get; set; }

    /// <summary>Certificate conditions.</summary>
    public List<CertificateCondition> Conditions { get; set; } = new();

    /// <summary>Status message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Certificate condition.</summary>
public sealed class CertificateCondition
{
    /// <summary>Condition type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Condition status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Reason for the condition.</summary>
    public string? Reason { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Last transition time.</summary>
    public DateTime? LastTransitionTime { get; set; }
}

#endregion

#region Kubernetes Resource Types

internal sealed class Certificate
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public CertificateSpec Spec { get; set; } = new();
}

internal sealed class CertificateSpec
{
    public string? SecretName { get; set; }
    public IssuerRef? IssuerRef { get; set; }
    public string? CommonName { get; set; }
    public List<string>? DnsNames { get; set; }
    public List<string>? IpAddresses { get; set; }
    public List<string>? UriSans { get; set; }
    public List<string>? EmailAddresses { get; set; }
    public string? Duration { get; set; }
    public string? RenewBefore { get; set; }
    public bool? IsCA { get; set; }
    public List<string>? Usages { get; set; }
    public PrivateKeySpec? PrivateKey { get; set; }
    public X509Subject? Subject { get; set; }
    public SecretTemplate? SecretTemplate { get; set; }
    public List<CertificateAdditionalOutputFormat>? AdditionalOutputFormats { get; set; }
    public int? RevisionHistoryLimit { get; set; }
}

internal sealed class IssuerRef
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "ClusterIssuer";
    public string? Group { get; set; }
}

internal sealed class PrivateKeySpec
{
    public string? Algorithm { get; set; }
    public int? Size { get; set; }
    public string? Encoding { get; set; }
    public string? RotationPolicy { get; set; }
}

internal sealed class X509Subject
{
    public List<string>? Organizations { get; set; }
    public List<string>? Countries { get; set; }
    public List<string>? OrganizationalUnits { get; set; }
    public List<string>? Localities { get; set; }
    public List<string>? Provinces { get; set; }
    public List<string>? StreetAddresses { get; set; }
    public List<string>? PostalCodes { get; set; }
    public string? SerialNumber { get; set; }
}

internal sealed class SecretTemplate
{
    public Dictionary<string, string>? Annotations { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class CertificateAdditionalOutputFormat
{
    public string? Type { get; set; }
}

internal sealed class Issuer
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public IssuerSpec Spec { get; set; } = new();
}

internal sealed class ClusterIssuer
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public IssuerSpec Spec { get; set; } = new();
}

internal sealed class IssuerSpec
{
    public SelfSignedIssuer? SelfSigned { get; set; }
    public CaIssuer? Ca { get; set; }
    public AcmeIssuer? Acme { get; set; }
    public VaultIssuer? Vault { get; set; }
}

internal sealed class SelfSignedIssuer { }

internal sealed class CaIssuer
{
    public string SecretName { get; set; } = string.Empty;
}

internal sealed class AcmeIssuer
{
    public string Server { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public SecretKeySelector? PrivateKeySecretRef { get; set; }
    public List<AcmeSolver>? Solvers { get; set; }
}

internal sealed class AcmeSolver
{
    public Http01Solver? Http01 { get; set; }
    public Dns01Solver? Dns01 { get; set; }
    public SolverSelector? Selector { get; set; }
}

internal sealed class Http01Solver
{
    public IngressSolver? Ingress { get; set; }
}

internal sealed class IngressSolver
{
    public string? Class { get; set; }
}

internal sealed class Dns01Solver
{
    public Route53Solver? Route53 { get; set; }
    public CloudDnsSolver? CloudDNS { get; set; }
    public AzureDnsSolver? AzureDNS { get; set; }
}

internal sealed class Route53Solver
{
    public string? Region { get; set; }
    public string? HostedZoneID { get; set; }
}

internal sealed class CloudDnsSolver
{
    public string? Project { get; set; }
}

internal sealed class AzureDnsSolver
{
    public string? SubscriptionID { get; set; }
    public string? ResourceGroupName { get; set; }
    public string? HostedZoneName { get; set; }
}

internal sealed class SolverSelector
{
    public List<string>? DnsNames { get; set; }
    public List<string>? DnsZones { get; set; }
    public Dictionary<string, string>? MatchLabels { get; set; }
}

internal sealed class VaultIssuer
{
    public string Server { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public VaultAuth? Auth { get; set; }
}

internal sealed class VaultAuth
{
    public VaultKubernetesAuth? Kubernetes { get; set; }
}

internal sealed class VaultKubernetesAuth
{
    public string Role { get; set; } = string.Empty;
    public string? MountPath { get; set; }
    public SecretKeySelector? SecretRef { get; set; }
}

#endregion
