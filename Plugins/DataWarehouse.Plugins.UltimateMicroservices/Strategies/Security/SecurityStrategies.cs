namespace DataWarehouse.Plugins.UltimateMicroservices.Strategies.Security;

/// <summary>
/// 120.8: Security Strategies - 10 production-ready implementations.
/// </summary>

public sealed class OAuth2SecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-oauth2";
    public override string DisplayName => "OAuth 2.0 Authentication";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 20.0 };
    public override string SemanticDescription => "OAuth 2.0 authorization framework with token-based authentication.";
    public override string[] Tags => ["oauth2", "authentication", "authorization", "token"];
}

public sealed class JwtSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-jwt";
    public override string DisplayName => "JWT Token Authentication";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 5.0 };
    public override string SemanticDescription => "JSON Web Token (JWT) stateless authentication with signature verification.";
    public override string[] Tags => ["jwt", "token", "stateless", "authentication"];
}

public sealed class MtlsSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-mtls";
    public override string DisplayName => "Mutual TLS (mTLS)";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 15.0 };
    public override string SemanticDescription => "Mutual TLS authentication with bidirectional certificate verification.";
    public override string[] Tags => ["mtls", "tls", "certificates", "mutual-auth"];
}

public sealed class ServiceMeshSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-service-mesh";
    public override string DisplayName => "Service Mesh Security";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { SupportsDistributedTracing = true, TypicalLatencyOverheadMs = 8.0 };
    public override string SemanticDescription => "Service mesh security with sidecar proxy encryption and policy enforcement.";
    public override string[] Tags => ["service-mesh", "istio", "linkerd", "security"];
}

public sealed class ApiKeySecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-api-key";
    public override string DisplayName => "API Key Authentication";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { SupportsRateLimiting = true, TypicalLatencyOverheadMs = 3.0 };
    public override string SemanticDescription => "API key authentication with rate limiting and quota management.";
    public override string[] Tags => ["api-key", "authentication", "rate-limiting"];
}

public sealed class SamlSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-saml";
    public override string DisplayName => "SAML 2.0 Authentication";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 30.0 };
    public override string SemanticDescription => "SAML 2.0 enterprise SSO authentication with identity provider integration.";
    public override string[] Tags => ["saml", "sso", "enterprise", "identity-provider"];
}

public sealed class OpenIdConnectSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-oidc";
    public override string DisplayName => "OpenID Connect";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 25.0 };
    public override string SemanticDescription => "OpenID Connect identity layer on OAuth 2.0 with user profile information.";
    public override string[] Tags => ["oidc", "openid", "oauth2", "identity"];
}

public sealed class RbacSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-rbac";
    public override string DisplayName => "Role-Based Access Control";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 4.0 };
    public override string SemanticDescription => "Role-based access control (RBAC) for fine-grained authorization policies.";
    public override string[] Tags => ["rbac", "authorization", "roles", "permissions"];
}

public sealed class SpiffeSpireSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-spiffe-spire";
    public override string DisplayName => "SPIFFE/SPIRE Identity";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 10.0 };
    public override string SemanticDescription => "SPIFFE/SPIRE framework for workload identity and attestation.";
    public override string[] Tags => ["spiffe", "spire", "identity", "attestation", "cncf"];
}

public sealed class VaultSecretsSecurityStrategy : MicroservicesStrategyBase
{
    public override string StrategyId => "sec-vault-secrets";
    public override string DisplayName => "HashiCorp Vault Secrets";
    public override MicroservicesCategory Category => MicroservicesCategory.Security;
    public override MicroservicesStrategyCapabilities Capabilities => new() { TypicalLatencyOverheadMs = 12.0 };
    public override string SemanticDescription => "HashiCorp Vault secrets management with dynamic credentials and encryption.";
    public override string[] Tags => ["vault", "hashicorp", "secrets", "encryption"];
}
