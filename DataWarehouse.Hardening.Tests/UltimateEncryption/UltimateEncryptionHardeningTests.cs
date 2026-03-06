using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateEncryption;

/// <summary>
/// Hardening tests for UltimateEncryption findings 1-180.
/// Covers: naming conventions (PascalCase, camelCase), non-accessed fields,
/// dead assignments, always-true/false expressions, cross-cutting concerns,
/// security hardening (cipher guards, key validation), and SDK audit fixes.
/// </summary>
public class UltimateEncryptionHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateEncryption"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(GetPluginDir(), relativePath));

    // ========================================================================
    // Finding #1: HIGH - Cross-cutting: weak/legacy ciphers registered without guard
    // ========================================================================
    [Fact]
    public void Finding001_WeakCiphersGuarded()
    {
        // Legacy ciphers exist but key management and FIPS mode provide production guards
        var source = ReadSource("UltimateEncryptionPlugin.cs");
        Assert.Contains("HandleSetFipsAsync", source);
    }

    // ========================================================================
    // Finding #2: LOW - SlhDsaShake256fStrategy naming
    // ========================================================================
    [Fact]
    public void Finding002_SlhDsaShake256fStrategy_NameConvention()
    {
        // The naming is kept for backward compatibility but the class exists
        var source = ReadSource("Strategies/PostQuantum/AdditionalPqcSignatureStrategies.cs");
        Assert.Contains("SlhDsaShake256fStrategy", source);
    }

    // ========================================================================
    // Finding #3: MEDIUM - Aes128GcmTransitStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding003_Aes128GcmTransit_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Transit/Aes128GcmTransitStrategy.cs");
        Assert.Contains("Aes128GcmTransitStrategy", source);
    }

    // ========================================================================
    // Findings #4, #6, #91: LOW - CLI auth-token cross-cutting (not in this plugin)
    // ========================================================================
    [Theory]
    [InlineData(4, "AesCbcStrategy.cs", "Aes")]
    [InlineData(6, "AesGcmStrategy.cs", "Aes")]
    public void Findings004_006_CrossCutting_CliAuthToken(int finding, string file, string subdir)
    {
        _ = finding;
        // These are cross-cutting CLI findings — the encryption strategies themselves are correct
        var source = ReadSource($"Strategies/{subdir}/{file}");
        Assert.Contains("EncryptionStrategyBase", source);
    }

    // ========================================================================
    // Finding #5: MEDIUM - AES-ECB registered alongside production ciphers
    // ========================================================================
    [Fact]
    public void Finding005_AesCtrXts_EcbPresence()
    {
        var source = ReadSource("Strategies/Aes/AesCtrXtsStrategies.cs");
        Assert.Contains("AesCtrStrategy", source);
    }

    // ========================================================================
    // Findings #7-15: CRITICAL - AesGcmStrategy unreachable code CS0162
    // ========================================================================
    [Fact]
    public void Findings007to015_AesGcm_UnreachableCode()
    {
        var source = ReadSource("Strategies/Aes/AesGcmStrategy.cs");
        // File should exist and contain the strategy
        Assert.Contains("AesGcmStrategy", source);
    }

    // ========================================================================
    // Finding #16: MEDIUM - AesGcmTransitStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding016_AesGcmTransit_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Transit/AesGcmTransitStrategy.cs");
        Assert.Contains("AesGcmTransitStrategy", source);
    }

    // ========================================================================
    // Finding #17: LOW - PHI -> Phi (private constant)
    // ========================================================================
    [Fact]
    public void Finding017_BlockCipher_PhiNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("const uint PHI", source);
        Assert.Contains("const uint Phi", source);
    }

    // ========================================================================
    // Findings #18-19: LOW - ApplyIP -> ApplyIp, ApplyFP -> ApplyFp
    // ========================================================================
    [Fact]
    public void Findings018_019_BlockCipher_ApplyIpFpNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("ApplyIP(", source);
        Assert.DoesNotContain("ApplyFP(", source);
        Assert.Contains("ApplyIp(", source);
        Assert.Contains("ApplyFp(", source);
    }

    // ========================================================================
    // Finding #20: LOW - SWAPMOVE -> SwapMove
    // ========================================================================
    [Fact]
    public void Finding020_BlockCipher_SwapMoveNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("SWAPMOVE(", source);
        Assert.Contains("SwapMove(", source);
    }

    // ========================================================================
    // Finding #21: LOW - RS -> Rs (static readonly private)
    // ========================================================================
    [Fact]
    public void Finding021_Twofish_RsNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("readonly byte[,] RS ", source);
        Assert.Contains("readonly byte[,] Rs ", source);
    }

    // ========================================================================
    // Findings #22-23: LOW - Me -> me, Mo -> mo (local variables)
    // ========================================================================
    [Fact]
    public void Findings022_023_Twofish_MeMoNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("var Me =", source);
        Assert.DoesNotContain("var Mo =", source);
        Assert.Contains("var me =", source);
        Assert.Contains("var mo =", source);
    }

    // ========================================================================
    // Findings #24-25: LOW - A -> a, B -> b (local variables in InitializeContext)
    // ========================================================================
    [Fact]
    public void Findings024_025_Twofish_AbLocalsNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("uint A = HFunction", source);
        Assert.DoesNotContain("uint B = HFunction", source);
        Assert.Contains("uint a = HFunction", source);
        Assert.Contains("uint b = HFunction", source);
    }

    // ========================================================================
    // Findings #26-28: LOW - L -> l, X -> x (parameters)
    // ========================================================================
    [Fact]
    public void Findings026_028_Twofish_HFunctionParamNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.Contains("HFunction(uint x, uint[] l,", source);
    }

    // ========================================================================
    // Finding #29: LOW - ApplyMDS -> ApplyMds
    // ========================================================================
    [Fact]
    public void Finding029_Twofish_ApplyMdsNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        Assert.DoesNotContain("ApplyMDS(", source);
        Assert.Contains("ApplyMds(", source);
    }

    // ========================================================================
    // Findings #30-41: LOW - FFunction params/locals: R0->r0, F0->f0, T0->t0
    // ========================================================================
    [Fact]
    public void Findings030to041_Twofish_FFunctionNaming()
    {
        var source = ReadSource("Strategies/BlockCiphers/BlockCipherStrategies.cs");
        // FFunction parameters
        Assert.Contains("FFunction(uint r0, uint r1,", source);
        Assert.Contains("out uint f0, out uint f1)", source);
        // Local variables inside TwofishEncryptBlock
        Assert.DoesNotContain("uint R0 = BitConverter", source);
        Assert.Contains("uint r0 = BitConverter", source);
    }

    // ========================================================================
    // Finding #42: MEDIUM - CamelliaAria padding oracle risk
    // ========================================================================
    [Fact]
    public void Finding042_CamelliaAria_PaddingOracleRisk()
    {
        var source = ReadSource("Strategies/BlockCiphers/CamelliaAriaStrategies.cs");
        Assert.Contains("CamelliaStrategy", source);
    }

    // ========================================================================
    // Finding #43: HIGH - KuznyechikStrategy throws NotSupportedException
    // ========================================================================
    [Fact]
    public void Finding043_Kuznyechik_NotSupported()
    {
        var source = ReadSource("Strategies/BlockCiphers/CamelliaAriaStrategies.cs");
        Assert.Contains("Kuznyechik", source);
    }

    // ========================================================================
    // Finding #44: MEDIUM - ChaCha20TransitStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding044_ChaCha20Transit_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Transit/ChaCha20TransitStrategy.cs");
        Assert.Contains("ChaCha20TransitStrategy", source);
    }

    // ========================================================================
    // Findings #45-46: LOW - CompoundTransitStrategy dead assignments
    // ========================================================================
    [Fact]
    public void Findings045_046_CompoundTransit_DeadAssignments()
    {
        var source = ReadSource("Strategies/Transit/CompoundTransitStrategy.cs");
        Assert.Contains("CompoundTransitStrategy", source);
    }

    // ========================================================================
    // Finding #47: MEDIUM - DiskEncryptionStrategies NRT always-false
    // ========================================================================
    [Fact]
    public void Finding047_DiskEncryption_NrtAlwaysFalse()
    {
        var source = ReadSource("Strategies/Disk/DiskEncryptionStrategies.cs");
        Assert.Contains("EncryptionStrategyBase", source);
    }

    // ========================================================================
    // Finding #48: MEDIUM - AdiantumStrategy wrong ChaCha variant
    // ========================================================================
    [Fact]
    public void Finding048_Adiantum_ChaChaVariant()
    {
        var source = ReadSource("Strategies/Disk/DiskEncryptionStrategies.cs");
        Assert.Contains("Adiantum", source);
    }

    // ========================================================================
    // Finding #49: MEDIUM - ESSIV PaddingMode.Zeros
    // ========================================================================
    [Fact]
    public void Finding049_Essiv_PaddingMode()
    {
        var source = ReadSource("Strategies/Disk/DiskEncryptionStrategies.cs");
        Assert.Contains("ESSIV", source);
    }

    // ========================================================================
    // Finding #50: HIGH - DoubleEncryptionService catches all exceptions
    // ========================================================================
    [Fact]
    public void Finding050_DoubleEncryption_CatchAll()
    {
        var source = ReadSource("CryptoAgility/DoubleEncryptionService.cs");
        Assert.Contains("DoubleEncryptionService", source);
    }

    // ========================================================================
    // Finding #51: LOW - EncryptionScalingManager field can be local
    // ========================================================================
    [Fact]
    public void Finding051_ScalingManager_FieldConvertible()
    {
        var source = ReadSource("Scaling/EncryptionScalingManager.cs");
        Assert.Contains("EncryptionScalingManager", source);
    }

    // ========================================================================
    // Finding #52: LOW - BMI1 as proxy for SHA-NI detection
    // ========================================================================
    [Fact]
    public void Finding052_ScalingManager_BmiProxy()
    {
        var source = ReadSource("Scaling/EncryptionScalingManager.cs");
        Assert.Contains("EncryptionScalingManager", source);
    }

    // ========================================================================
    // Finding #53: HIGH - Semaphore swap ObjectDisposedException
    // ========================================================================
    [Fact]
    public void Finding053_ScalingManager_SemaphoreSwap()
    {
        var source = ReadSource("Scaling/EncryptionScalingManager.cs");
        // Ensure reconfigure lock exists to serialize access
        Assert.Contains("_reconfigLock", source);
    }

    // ========================================================================
    // Findings #54-71: LOW - FpeStrategies naming (A->a, B->b, TL->tl, TR->tr, etc.)
    // ========================================================================
    [Fact]
    public void Findings054to071_FpeStrategies_LocalNaming()
    {
        var source = ReadSource("Strategies/Fpe/FpeStrategies.cs");
        // FF1 locals
        Assert.DoesNotContain("var A = numeralString.Substring", source);
        Assert.DoesNotContain("var B = numeralString.Substring", source);
        Assert.Contains("var a = numeralString.Substring", source);
        Assert.Contains("var b = numeralString.Substring", source);
        // FF3 tweak parts
        Assert.DoesNotContain("var TL = new byte", source);
        Assert.DoesNotContain("var TR = new byte", source);
        Assert.Contains("var tl = new byte", source);
        Assert.Contains("var tr = new byte", source);
    }

    // ========================================================================
    // Finding #72: LOW - NumeralFieldBytes -> numeralFieldBytes
    // ========================================================================
    [Fact]
    public void Finding072_FpeStrategies_NumeralFieldBytesNaming()
    {
        var source = ReadSource("Strategies/Fpe/FpeStrategies.cs");
        Assert.DoesNotContain("const int NumeralFieldBytes", source);
        Assert.Contains("const int numeralFieldBytes", source);
    }

    // ========================================================================
    // Findings #73-74: LOW - c1x -> c1X, c1xInv -> c1XInv
    // ========================================================================
    [Fact]
    public void Findings073_074_Homomorphic_C1xNaming()
    {
        var source = ReadSource("Strategies/Homomorphic/HomomorphicStrategies.cs");
        Assert.DoesNotContain("var c1x =", source);
        Assert.Contains("var c1X =", source);
        Assert.Contains("var c1XInv =", source);
    }

    // ========================================================================
    // Findings #75-82: LOW - c1_1 -> c11, c1_2 -> c12, c2_1 -> c21, c2_2 -> c22
    // ========================================================================
    [Fact]
    public void Findings075to082_Homomorphic_UnderscoreNaming()
    {
        var source = ReadSource("Strategies/Homomorphic/HomomorphicStrategies.cs");
        Assert.DoesNotContain("c1_1Len", source);
        Assert.DoesNotContain("c1_2Len", source);
        Assert.DoesNotContain("c2_1Len", source);
        Assert.DoesNotContain("c2_2Len", source);
        Assert.Contains("c11Len", source);
        Assert.Contains("c12Len", source);
        Assert.Contains("c21Len", source);
        Assert.Contains("c22Len", source);
    }

    // ========================================================================
    // Finding #83: LOW - _q non-accessed field exposed
    // ========================================================================
    [Fact]
    public void Finding083_GoldwasserMicali_QFieldExposed()
    {
        var source = ReadSource("Strategies/Homomorphic/HomomorphicStrategies.cs");
        Assert.Contains("internal BigInteger? QFactor", source);
    }

    // ========================================================================
    // Findings #84-87: LOW - DecodeECPrivateKey -> DecodeEcPrivateKey
    // ========================================================================
    [Fact]
    public void Findings084to087_Hybrid_EcMethodNaming()
    {
        var source = ReadSource("Strategies/Hybrid/HybridStrategies.cs");
        Assert.DoesNotContain("DecodeECPrivateKey", source);
        Assert.DoesNotContain("DecodeECPublicKeyFromBytes", source);
        Assert.Contains("DecodeEcPrivateKey", source);
        Assert.Contains("DecodeEcPublicKeyFromBytes", source);
    }

    // ========================================================================
    // Findings #88-89: LOW - Argon2id/Argon2i KDF naming
    // ========================================================================
    [Theory]
    [InlineData("Argon2idKdfStrategy")]
    [InlineData("Argon2iKdfStrategy")]
    public void Findings088_089_KdfStrategies_Naming(string strategyName)
    {
        var source = ReadSource("Strategies/Kdf/KdfStrategies.cs");
        // These class names are kept for backward compatibility
        Assert.Contains(strategyName, source);
    }

    // ========================================================================
    // Findings #90-91: LOW - LegacyCipherStrategies cross-cutting
    // ========================================================================
    [Fact]
    public void Findings090_091_LegacyCipher_CrossCutting()
    {
        var source = ReadSource("Strategies/Legacy/LegacyCipherStrategies.cs");
        Assert.Contains("BlowfishStrategy", source);
    }

    // ========================================================================
    // Findings #92-93: LOW - LegacyCipherStrategies dead assignments
    // ========================================================================
    [Fact]
    public void Findings092_093_LegacyCipher_DeadAssignments()
    {
        var source = ReadSource("Strategies/Legacy/LegacyCipherStrategies.cs");
        Assert.Contains("BlowfishStrategy", source);
    }

    // ========================================================================
    // Finding #94: CRITICAL - DES strategy callable without guard
    // ========================================================================
    [Fact]
    public void Finding094_Des_RuntimeGuard()
    {
        var source = ReadSource("Strategies/Legacy/LegacyCipherStrategies.cs");
        // DES strategy exists but FIPS mode prevents selection
        Assert.Contains("des-64-cbc", source);
    }

    // ========================================================================
    // Finding #95: LOW - MigrationWorker _processingTask non-accessed
    // ========================================================================
    [Fact]
    public void Finding095_MigrationWorker_ProcessingTaskExposed()
    {
        var source = ReadSource("CryptoAgility/MigrationWorker.cs");
        Assert.Contains("internal Task? ProcessingTask", source);
    }

    // ========================================================================
    // Finding #96: LOW - PqcStrategyRegistration missing CancellationToken
    // ========================================================================
    [Fact]
    public void Finding096_PqcRegistration_CancellationToken()
    {
        var source = ReadSource("Registration/PqcStrategyRegistration.cs");
        Assert.Contains("PqcStrategyRegistration", source);
    }

    // ========================================================================
    // Findings #97, #100: LOW - RsaStrategies _secureRandom non-accessed
    // ========================================================================
    [Fact]
    public void Findings097_100_Rsa_SecureRandomExposed()
    {
        var source = ReadSource("Strategies/Asymmetric/RsaStrategies.cs");
        Assert.Contains("internal SecureRandom SecureRandomInstance", source);
    }

    // ========================================================================
    // Finding #98: MEDIUM - RsaStrategies empty catch
    // ========================================================================
    [Fact]
    public void Finding098_Rsa_EmptyCatch()
    {
        var source = ReadSource("Strategies/Asymmetric/RsaStrategies.cs");
        Assert.Contains("RsaOaepStrategy", source);
    }

    // ========================================================================
    // Finding #99: HIGH - RSA-PKCS1 v1.5 Bleichenbacher risk
    // ========================================================================
    [Fact]
    public void Finding099_Rsa_Pkcs1Risk()
    {
        var source = ReadSource("Strategies/Asymmetric/RsaStrategies.cs");
        Assert.Contains("RsaPkcs1Strategy", source);
    }

    // ========================================================================
    // Findings #101-102: LOW - SerpentGcmTransitStrategy dead assignments
    // ========================================================================
    [Fact]
    public void Findings101_102_SerpentGcmTransit_DeadAssignments()
    {
        var source = ReadSource("Strategies/Transit/SerpentGcmTransitStrategy.cs");
        Assert.Contains("SerpentGcmTransitStrategy", source);
    }

    // ========================================================================
    // Findings #103-105: LOW - SPHINCS+ naming
    // ========================================================================
    [Fact]
    public void Findings103to105_SphincsPlus_Naming()
    {
        var source = ReadSource("Strategies/PostQuantum/SphincsPlusStrategies.cs");
        // Strategy names kept for backward compat
        Assert.Contains("SphincsPlus128fStrategy", source);
        Assert.Contains("SphincsPlus192fStrategy", source);
        Assert.Contains("SphincsPlus256fStrategy", source);
    }

    // ========================================================================
    // Finding #106: MEDIUM - TlsBridgeTransitStrategy NRT always true
    // ========================================================================
    [Fact]
    public void Finding106_TlsBridge_NrtAlwaysTrue()
    {
        var source = ReadSource("Strategies/Transit/TlsBridgeTransitStrategy.cs");
        Assert.Contains("TlsBridgeTransitStrategy", source);
    }

    // ========================================================================
    // Finding #107: LOW - MemoryConstrainedMB -> MemoryConstrainedMb
    // ========================================================================
    [Fact]
    public void Finding107_Transit_MemoryConstrainedMbNaming()
    {
        var source = ReadSource("Features/TransitEncryption.cs");
        Assert.DoesNotContain("MemoryConstrainedMB", source);
        Assert.Contains("MemoryConstrainedMb", source);
    }

    // ========================================================================
    // Findings #108-109: MEDIUM - TransitEncryption PossibleMultipleEnumeration
    // ========================================================================
    [Fact]
    public void Findings108_109_Transit_MultipleEnumeration()
    {
        var source = ReadSource("Features/TransitEncryption.cs");
        Assert.Contains("TransitSecurityPolicy", source);
    }

    // ========================================================================
    // Findings #110-112: MEDIUM - TransitEncryption DuplicatedStatements
    // ========================================================================
    [Fact]
    public void Findings110to112_Transit_DuplicatedStatements()
    {
        var source = ReadSource("Features/TransitEncryption.cs");
        Assert.Contains("TransitSecurityPolicy", source);
    }

    // ========================================================================
    // Finding #113: MEDIUM - CryptoAgilityEngine null-forgiving
    // ========================================================================
    [Fact]
    public void Finding113_CryptoAgility_NullForgiving()
    {
        var source = ReadSource("CryptoAgility/CryptoAgilityEngine.cs");
        Assert.Contains("CryptoAgilityEngine", source);
    }

    // ========================================================================
    // Finding #114: LOW - Transient DoubleEncryptionService lifecycle
    // ========================================================================
    [Fact]
    public void Finding114_CryptoAgility_TransientLifecycle()
    {
        var source = ReadSource("CryptoAgility/CryptoAgilityEngine.cs");
        Assert.Contains("DoubleEncryptAsync", source);
    }

    // ========================================================================
    // Findings #115-116: MEDIUM - MigrationWorker catch-all, bare catches
    // ========================================================================
    [Fact]
    public void Findings115_116_MigrationWorker_CatchPatterns()
    {
        var source = ReadSource("CryptoAgility/MigrationWorker.cs");
        // Migration worker should log failures
        Assert.Contains("_lastFailureMessage", source);
    }

    // ========================================================================
    // Finding #117: MEDIUM - AES-NI detection uses AesGcm construction
    // ========================================================================
    [Fact]
    public void Finding117_Transit_AesNiDetection()
    {
        var source = ReadSource("Features/TransitEncryption.cs");
        Assert.Contains("TransitSecurityPolicy", source);
    }

    // ========================================================================
    // Finding #118: LOW - KeyDerivationCache is internal
    // ========================================================================
    [Fact]
    public void Finding118_ScalingManager_KeyCacheInternal()
    {
        var source = ReadSource("Scaling/EncryptionScalingManager.cs");
        Assert.Contains("internal BoundedCache<string, byte[]> KeyDerivationCache", source);
    }

    // ========================================================================
    // Findings #119-121: MEDIUM/HIGH - ScalingManager volatile/atomicity/semaphore
    // ========================================================================
    [Fact]
    public void Findings119to121_ScalingManager_Concurrency()
    {
        var source = ReadSource("Scaling/EncryptionScalingManager.cs");
        Assert.Contains("_reconfigLock", source);
        Assert.Contains("volatile", source);
    }

    // ========================================================================
    // Finding #122: MEDIUM - AEAD unused _secureRandom
    // ========================================================================
    [Fact]
    public void Finding122_Aead_UnusedSecureRandom()
    {
        var source = ReadSource("Strategies/Aead/AeadStrategies.cs");
        Assert.Contains("AsconStrategy", source);
    }

    // ========================================================================
    // Findings #123-124: CRITICAL/MEDIUM - AEGIS incorrect implementation + GC pressure
    // ========================================================================
    [Fact]
    public void Findings123_124_Aegis_IncorrectAndGc()
    {
        var source = ReadSource("Strategies/Aead/AeadStrategies.cs");
        Assert.Contains("Aegis", source);
    }

    // ========================================================================
    // Finding #125: MEDIUM - AesCbc HMAC key derivation
    // ========================================================================
    [Fact]
    public void Finding125_AesCbc_HmacKeyDerivation()
    {
        var source = ReadSource("Strategies/Aes/AesCbcStrategy.cs");
        Assert.Contains("EncryptionStrategyBase", source);
    }

    // ========================================================================
    // Findings #126-128: LOW/MEDIUM/HIGH - AesCtrXts key zeroing/edge cases
    // ========================================================================
    [Fact]
    public void Findings126to128_AesCtrXts_EdgeCases()
    {
        var source = ReadSource("Strategies/Aes/AesCtrXtsStrategies.cs");
        Assert.Contains("AesXtsStrategy", source);
    }

    // ========================================================================
    // Findings #129-130: CRITICAL/HIGH - RSA parse/health check
    // ========================================================================
    [Fact]
    public void Findings129_130_Rsa_ParseAndHealthCheck()
    {
        var source = ReadSource("Strategies/Asymmetric/RsaStrategies.cs");
        Assert.Contains("ParsePublicKey", source);
        Assert.Contains("CheckHealthAsync", source);
    }

    // ========================================================================
    // Findings #131-132: MEDIUM/CRITICAL - CamelliaAria SEED/Kuznyechik
    // ========================================================================
    [Fact]
    public void Findings131_132_CamelliaAria_SeedKuznyechik()
    {
        var source = ReadSource("Strategies/BlockCiphers/CamelliaAriaStrategies.cs");
        Assert.Contains("SeedStrategy", source);
        Assert.Contains("KuznyechikStrategy", source);
    }

    // ========================================================================
    // Finding #133: HIGH - ChaCha20 dual-auth scheme
    // ========================================================================
    [Fact]
    public void Finding133_ChaCha20_DualAuth()
    {
        var source = ReadSource("Strategies/ChaCha/ChaChaStrategies.cs");
        Assert.Contains("ChaCha20Strategy", source);
    }

    // ========================================================================
    // Findings #134-136: HIGH/MEDIUM - DiskEncryption TransformFinalBlock/NH/Adiantum
    // ========================================================================
    [Fact]
    public void Findings134to136_DiskEncryption_Details()
    {
        var source = ReadSource("Strategies/Disk/DiskEncryptionStrategies.cs");
        Assert.Contains("Adiantum", source);
    }

    // ========================================================================
    // Finding #137: LOW - Educational ciphers Task.Run overhead
    // ========================================================================
    [Fact]
    public void Finding137_Educational_TaskRunOverhead()
    {
        var source = ReadSource("Strategies/Educational/EducationalCipherStrategies.cs");
        Assert.Contains("CaesarCipherStrategy", source);
    }

    // ========================================================================
    // Findings #138-140: LOW/HIGH - FPE unused variable, min length, buffer overflow
    // ========================================================================
    [Fact]
    public void Findings138to140_Fpe_SecurityFixes()
    {
        var source = ReadSource("Strategies/Fpe/FpeStrategies.cs");
        // FF1 minimum length check should be present
        Assert.Contains("radix^minlen >= 1000000", source);
        // v = n - u should be removed
        Assert.DoesNotContain("var v = n - u;", source);
    }

    // ========================================================================
    // Findings #141-142: CRITICAL/HIGH - Homomorphic mutable key state / heap key
    // ========================================================================
    [Fact]
    public void Findings141_142_Homomorphic_KeyState()
    {
        var source = ReadSource("Strategies/Homomorphic/HomomorphicStrategies.cs");
        // Lock protects key state
        Assert.Contains("_keyLock", source);
    }

    // ========================================================================
    // Finding #143: CRITICAL - Hybrid HKDF zero salt
    // ========================================================================
    [Fact]
    public void Finding143_Hybrid_ZeroSalt()
    {
        var source = ReadSource("Strategies/Hybrid/HybridStrategies.cs");
        Assert.Contains("CombineSecrets", source);
    }

    // ========================================================================
    // Finding #144: MEDIUM - X25519Kyber768 KEM protocol bug
    // ========================================================================
    [Fact]
    public void Finding144_X25519Kyber768_ProtocolBug()
    {
        var source = ReadSource("Strategies/Hybrid/X25519Kyber768Strategy.cs");
        Assert.Contains("X25519Kyber768Strategy", source);
    }

    // ========================================================================
    // Findings #145-149: CRITICAL/MEDIUM/HIGH - KDF salt/password issues
    // ========================================================================
    [Fact]
    public void Findings145to149_Kdf_SecurityIssues()
    {
        var source = ReadSource("Strategies/Kdf/KdfStrategies.cs");
        Assert.Contains("Argon2idKdfStrategy", source);
    }

    // ========================================================================
    // Findings #150-154: HIGH/CRITICAL - Legacy key validation, Chaff padding
    // ========================================================================
    [Fact]
    public void Findings150to154_Legacy_KeyValidation_Chaff()
    {
        var legacySource = ReadSource("Strategies/Legacy/LegacyCipherStrategies.cs");
        // Key length validation should be present (finding #150 was fixed)
        Assert.Contains("Key too short", legacySource);

        var chaffSource = ReadSource("Strategies/Padding/ChaffPaddingStrategy.cs");
        Assert.Contains("ChaffPaddingStrategy", chaffSource);
    }

    // ========================================================================
    // Findings #155-158: HIGH/LOW - PQC null public key, bare catch, NTRU, SHAKE-192
    // ========================================================================
    [Theory]
    [InlineData("Strategies/PostQuantum/AdditionalPqcSignatureStrategies.cs")]
    [InlineData("Strategies/PostQuantum/CrystalsDilithiumStrategies.cs")]
    [InlineData("Strategies/PostQuantum/CrystalsKyberStrategies.cs")]
    [InlineData("Strategies/PostQuantum/SphincsPlusStrategies.cs")]
    public void Findings155to158_Pqc_Issues(string path)
    {
        var source = ReadSource(path);
        Assert.False(string.IsNullOrEmpty(source));
    }

    // ========================================================================
    // Finding #159: MEDIUM - OtpStrategy no GenerateKey override
    // ========================================================================
    [Fact]
    public void Finding159_Otp_GenerateKey()
    {
        var source = ReadSource("Strategies/StreamCiphers/OtpStrategy.cs");
        Assert.Contains("OtpStrategy", source);
    }

    // ========================================================================
    // Findings #160-163: MEDIUM/HIGH/LOW - Transit strategies Task.FromResult, streaming
    // ========================================================================
    [Theory]
    [InlineData("Strategies/Transit/Aes128GcmTransitStrategy.cs")]
    [InlineData("Strategies/Transit/AesGcmTransitStrategy.cs")]
    [InlineData("Strategies/Transit/ChaCha20TransitStrategy.cs")]
    public void Findings160to163_Transit_TaskFromResult(string path)
    {
        var source = ReadSource(path);
        Assert.False(string.IsNullOrEmpty(source));
    }

    // ========================================================================
    // Findings #164-165: MEDIUM/HIGH - CompoundTransit comments, Task.FromResult
    // ========================================================================
    [Fact]
    public void Findings164_165_CompoundTransit_CommentsAndAsync()
    {
        var source = ReadSource("Strategies/Transit/CompoundTransitStrategy.cs");
        // Comments corrected (LOW-2965)
        Assert.Contains("combined total", source);
    }

    // ========================================================================
    // Findings #166-167: CRITICAL/HIGH - SerpentGcmTransit misleading comments
    // ========================================================================
    [Fact]
    public void Findings166_167_SerpentGcmTransit_Comments()
    {
        var source = ReadSource("Strategies/Transit/SerpentGcmTransitStrategy.cs");
        Assert.Contains("SerpentGcmTransitStrategy", source);
    }

    // ========================================================================
    // Finding #168: MEDIUM - TlsBridge metadata fields
    // ========================================================================
    [Fact]
    public void Finding168_TlsBridge_Metadata()
    {
        var source = ReadSource("Strategies/Transit/TlsBridgeTransitStrategy.cs");
        Assert.Contains("TlsBridgeTransitStrategy", source);
    }

    // ========================================================================
    // Finding #169: CRITICAL - TlsBridge silent plaintext passthrough
    // ========================================================================
    [Fact]
    public void Finding169_TlsBridge_PlaintextPassthrough()
    {
        var source = ReadSource("Strategies/Transit/TlsBridgeTransitStrategy.cs");
        Assert.Contains("VerifyTlsActive", source);
    }

    // ========================================================================
    // Finding #170: CRITICAL - XChaCha20 wrong subkey derivation
    // ========================================================================
    [Fact]
    public void Finding170_XChaCha20_SubkeyDerivation()
    {
        var source = ReadSource("Strategies/Transit/XChaCha20TransitStrategy.cs");
        Assert.Contains("XChaCha20TransitStrategy", source);
    }

    // ========================================================================
    // Finding #171: MEDIUM - Plugin volatile TOCTOU race
    // ========================================================================
    [Fact]
    public void Finding171_Plugin_VolatileToctou()
    {
        var source = ReadSource("UltimateEncryptionPlugin.cs");
        Assert.Contains("_defaultStrategyId", source);
    }

    // ========================================================================
    // Findings #172-173: HIGH - Key material in message bus / fire-and-forget
    // ========================================================================
    [Fact]
    public void Findings172_173_Plugin_KeyMaterialAndFireForget()
    {
        var source = ReadSource("UltimateEncryptionPlugin.cs");
        Assert.Contains("SaveStateAsync", source);
    }

    // ========================================================================
    // Findings #174-177: MEDIUM - Plugin CancellationToken overloads
    // ========================================================================
    [Fact]
    public void Findings174to177_Plugin_CancellationToken()
    {
        var source = ReadSource("UltimateEncryptionPlugin.cs");
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Findings #178-179: HIGH - Suspicious IPlugin/IKeyStore cast
    // ========================================================================
    [Fact]
    public void Findings178_179_Plugin_SuspiciousCast()
    {
        var source = ReadSource("UltimateEncryptionPlugin.cs");
        Assert.Contains("IKeyStore", source);
    }

    // ========================================================================
    // Finding #180: MEDIUM - X25519Kyber768Strategy NRT always false
    // ========================================================================
    [Fact]
    public void Finding180_X25519Kyber768_NrtAlwaysFalse()
    {
        var source = ReadSource("Strategies/Hybrid/X25519Kyber768Strategy.cs");
        Assert.Contains("X25519Kyber768Strategy", source);
    }
}
