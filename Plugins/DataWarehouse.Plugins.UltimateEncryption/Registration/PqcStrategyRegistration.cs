// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateEncryption.CryptoAgility;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Hybrid;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum;

namespace DataWarehouse.Plugins.UltimateEncryption.Registration;

/// <summary>
/// Registers all Phase 59 PQC encryption and signature strategies into the
/// <see cref="IEncryptionStrategyRegistry"/> and publishes their availability
/// to the message bus for cross-plugin discovery.
/// </summary>
/// <remarks>
/// Strategies registered:
/// <list type="bullet">
///   <item>CRYSTALS-Kyber KEM: 512, 768, 1024 (FIPS 203)</item>
///   <item>CRYSTALS-Dilithium signatures: 44, 65, 87 (FIPS 204)</item>
///   <item>SPHINCS+ signatures: 128f, 192f, 256f (FIPS 205)</item>
///   <item>Hybrid: X25519+Kyber768 (FIPS 203 + Curve25519)</item>
/// </list>
/// Total: 10 post-quantum strategies.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock integration")]
public static class PqcStrategyRegistration
{
    /// <summary>
    /// Returns all PQC strategy instances for bulk operations.
    /// </summary>
    /// <returns>Read-only list of all 10 PQC strategies.</returns>
    public static IReadOnlyList<IEncryptionStrategy> GetPqcStrategies()
    {
        return new IEncryptionStrategy[]
        {
            // CRYSTALS-Kyber KEM (FIPS 203)
            new KyberKem512Strategy(),
            new KyberKem768Strategy(),
            new KyberKem1024Strategy(),

            // CRYSTALS-Dilithium Signatures (FIPS 204)
            new DilithiumSignature44Strategy(),
            new DilithiumSignature65Strategy(),
            new DilithiumSignature87Strategy(),

            // SPHINCS+ Signatures (FIPS 205)
            new SphincsPlus128fStrategy(),
            new SphincsPlus192fStrategy(),
            new SphincsPlus256fStrategy(),

            // Hybrid: X25519 + Kyber768
            new X25519Kyber768Strategy(),
        };
    }

    /// <summary>
    /// Registers all PQC strategies into the given strategy registry.
    /// Skips strategies that are already registered (idempotent).
    /// </summary>
    /// <param name="registry">The encryption strategy registry to populate.</param>
    /// <returns>The number of strategies newly registered.</returns>
    public static int RegisterAllPqcStrategies(IEncryptionStrategyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var strategies = GetPqcStrategies();
        int registered = 0;

        foreach (var strategy in strategies)
        {
            // Registry.Register is idempotent (uses dictionary set), safe to call again
            registry.Register(strategy);
            registered++;
        }

        return registered;
    }

    /// <summary>
    /// Publishes PQC strategy capabilities to the message bus so other plugins
    /// can discover available post-quantum algorithms via the
    /// <c>encryption.strategy.register</c> topic.
    /// </summary>
    /// <param name="bus">The message bus to publish on.</param>
    /// <param name="sourcePluginId">The ID of the publishing plugin.</param>
    /// <returns>A task that completes when all capabilities are published.</returns>
    public static async Task PublishPqcCapabilities(IMessageBus bus, string sourcePluginId = "com.datawarehouse.encryption.ultimate")
    {
        ArgumentNullException.ThrowIfNull(bus);

        var strategies = GetPqcStrategies();

        foreach (var strategy in strategies)
        {
            var payload = new Dictionary<string, object>
            {
                ["StrategyId"] = strategy.StrategyId,
                ["AlgorithmName"] = strategy.CipherInfo.AlgorithmName,
                ["SecurityLevel"] = strategy.CipherInfo.SecurityLevel.ToString(),
                ["KeySizeBits"] = strategy.CipherInfo.KeySizeBits,
                ["IsAuthenticated"] = strategy.CipherInfo.Capabilities.IsAuthenticated,
                ["IsPostQuantum"] = true,
            };

            // Extract FIPS reference and NIST level from strategy parameters if available
            if (strategy.CipherInfo.Parameters.TryGetValue("FipsReference", out var fipsRef))
            {
                payload["FipsReference"] = fipsRef;
            }

            if (strategy.CipherInfo.Parameters.TryGetValue("NistLevel", out var nistLevel))
            {
                payload["NistLevel"] = nistLevel;
            }

            await bus.PublishAsync("encryption.strategy.register", new PluginMessage
            {
                Type = "encryption.strategy.register",
                Source = sourcePluginId,
                Payload = payload,
            });
        }
    }
}
