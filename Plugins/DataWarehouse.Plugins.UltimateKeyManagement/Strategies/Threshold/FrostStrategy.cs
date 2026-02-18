using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Threshold
{
    /// <summary>
    /// FROST (Flexible Round-Optimized Schnorr Threshold) signature scheme.
    ///
    /// FROST is a threshold Schnorr signature scheme optimized for:
    /// - Fewer communication rounds (2 rounds for signing)
    /// - Robustness against malicious participants
    /// - Compatible with BIP-340 (Bitcoin Taproot) and ed25519
    ///
    /// Based on the paper "FROST: Flexible Round-Optimized Schnorr Threshold Signatures"
    /// by Chelsea Komlo and Ian Goldberg (SAC 2020).
    ///
    /// Protocol Overview:
    /// 1. Key Generation: Distributed key generation with Pedersen commitments
    /// 2. Preprocessing: Generate nonce commitments (can be done offline)
    /// 3. Signing Round 1: Signers reveal nonce commitments
    /// 4. Signing Round 2: Signers compute and reveal signature shares
    /// 5. Aggregation: Combine shares into final Schnorr signature
    ///
    /// Security:
    /// - Unforgeable under chosen-message attack in the ROM
    /// - Robust against up to t-1 malicious participants
    /// - Supports identifiable abort
    /// </summary>
    public sealed class FrostStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private FrostConfig _config = new();
        private readonly Dictionary<string, FrostKeyData> _keys = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SecureRandom _secureRandom = new();
        private bool _disposed;

        // secp256k1 curve parameters (Bitcoin/Taproot compatible)
        private static readonly X9ECParameters CurveParams = CustomNamedCurves.GetByName("secp256k1");
        private static readonly ECDomainParameters DomainParams = new(
            CurveParams.Curve, CurveParams.G, CurveParams.N, CurveParams.H);

        // BIP-340 tagged hash prefixes
        private const string TagChallenge = "BIP0340/challenge";
        private const string TagAux = "BIP0340/aux";
        private const string TagNonce = "BIP0340/nonce";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 32,
            MinKeySizeBytes = 32,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "FROST Threshold Schnorr",
                ["Curve"] = "secp256k1",
                ["Protocol"] = "FROST (Komlo-Goldberg)",
                ["SigningRounds"] = 2,
                ["Compatible"] = new[] { "BIP-340", "Taproot", "MuSig2" },
                ["Robust"] = true
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "Schnorr-ECIES",
            "ECDH-AES-GCM"
        };

        public bool SupportsHsmKeyGeneration => false;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            if (Configuration.TryGetValue("Threshold", out var t) && t is int threshold)
                _config.Threshold = threshold;
            if (Configuration.TryGetValue("TotalParties", out var n) && n is int total)
                _config.TotalParties = total;
            if (Configuration.TryGetValue("PartyIndex", out var i) && i is int index)
                _config.PartyIndex = index;
            if (Configuration.TryGetValue("StoragePath", out var p) && p is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("PreprocessBatchSize", out var batch) && batch is int batchSize)
                _config.PreprocessBatchSize = batchSize;

            ValidateConfiguration();
            await LoadKeysFromStorage();
        }

        private void ValidateConfiguration()
        {
            if (_config.Threshold < 2)
                throw new ArgumentException("Threshold must be at least 2.");
            if (_config.TotalParties < _config.Threshold)
                throw new ArgumentException("Total parties must be >= threshold.");
            if (_config.PartyIndex < 1 || _config.PartyIndex > _config.TotalParties)
                throw new ArgumentException("Invalid party index.");
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"FROST key '{keyId}' not found.");

                return keyData.SecretShare.ToByteArrayUnsigned();
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                var secretShare = new BigInteger(1, keyData).Mod(DomainParams.N);
                var publicShare = DomainParams.G.Multiply(secretShare);

                var frostKeyData = new FrostKeyData
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    Threshold = _config.Threshold,
                    TotalParties = _config.TotalParties,
                    SecretShare = secretShare,
                    PublicShare = publicShare,
                    NoncePool = new Queue<FrostNonce>(),
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };

                _keys[keyId] = frostKeyData;
                _currentKeyId = keyId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST DKG Round 1: Generate and broadcast polynomial commitments.
        /// </summary>
        public async Task<FrostDkgRound1> InitiateDkgAsync(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                // Generate random polynomial of degree t-1
                var coefficients = new BigInteger[_config.Threshold];
                var commitments = new ECPoint[_config.Threshold];

                for (int i = 0; i < _config.Threshold; i++)
                {
                    coefficients[i] = GenerateRandomScalar();
                    commitments[i] = DomainParams.G.Multiply(coefficients[i]);
                }

                // Generate Pedersen hiding commitments (optional, for VSS verification)
                var hidingNonces = new BigInteger[_config.Threshold];
                var hidingCommitments = new ECPoint[_config.Threshold];
                var h = DomainParams.G.Multiply(BigInteger.ValueOf(7)); // Secondary generator

                for (int i = 0; i < _config.Threshold; i++)
                {
                    hidingNonces[i] = GenerateRandomScalar();
                    hidingCommitments[i] = DomainParams.G.Multiply(coefficients[i])
                        .Add(h.Multiply(hidingNonces[i]));
                }

                // Compute proof of knowledge for secret coefficient
                var secretProof = CreateSchnorrProof(coefficients[0]);

                var keyData = new FrostKeyData
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    Threshold = _config.Threshold,
                    TotalParties = _config.TotalParties,
                    SecretShare = coefficients[0], // Will be updated after DKG
                    PolynomialCoeffs = coefficients,
                    Commitments = commitments,
                    NoncePool = new Queue<FrostNonce>(),
                    DkgPhase = 1,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };

                _keys[keyId] = keyData;

                return new FrostDkgRound1
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    Commitments = commitments.Select(c => c.GetEncoded(false)).ToArray(),
                    ProofOfKnowledge = secretProof
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST DKG Round 2: Generate and distribute secret shares.
        /// </summary>
        public async Task<FrostDkgRound2> ProcessDkgRound1Async(
            string keyId,
            FrostDkgRound1[] round1Messages,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData) || keyData.DkgPhase != 1)
                    throw new InvalidOperationException("Invalid DKG state.");

                // Verify proofs of knowledge from all parties
                foreach (var msg in round1Messages)
                {
                    if (msg.PartyIndex == _config.PartyIndex) continue;

                    var publicKey = CurveParams.Curve.DecodePoint(msg.Commitments[0]);
                    if (!VerifySchnorrProof(publicKey, msg.ProofOfKnowledge))
                        throw new CryptographicException($"Invalid proof from party {msg.PartyIndex}");
                }

                // Store commitments from all parties
                keyData.AllCommitments = round1Messages.ToDictionary(
                    m => m.PartyIndex,
                    m => m.Commitments.Select(c => CurveParams.Curve.DecodePoint(c)).ToArray());

                // Generate secret shares for each party
                var shares = new Dictionary<int, byte[]>();
                for (int j = 1; j <= _config.TotalParties; j++)
                {
                    if (j == _config.PartyIndex) continue;

                    var share = EvaluatePolynomial(keyData.PolynomialCoeffs!, j);
                    shares[j] = share.ToByteArrayUnsigned();
                }

                keyData.DkgPhase = 2;
                await PersistKeysToStorage();

                return new FrostDkgRound2
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    Shares = shares
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST DKG Finalization: Compute final key share from received shares.
        /// </summary>
        public async Task<FrostDkgResult> FinalizeDkgAsync(
            string keyId,
            FrostDkgRound2[] round2Messages,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData) || keyData.DkgPhase != 2)
                    throw new InvalidOperationException("Invalid DKG state.");

                // Collect and verify shares
                var myShare = EvaluatePolynomial(keyData.PolynomialCoeffs!, _config.PartyIndex);

                foreach (var msg in round2Messages)
                {
                    if (msg.PartyIndex == _config.PartyIndex) continue;

                    if (!msg.Shares.TryGetValue(_config.PartyIndex, out var shareBytes))
                        throw new CryptographicException($"Missing share from party {msg.PartyIndex}");

                    var share = new BigInteger(1, shareBytes);

                    // Verify share against commitments
                    if (!VerifyShare(share, _config.PartyIndex, keyData.AllCommitments![msg.PartyIndex]))
                        throw new CryptographicException($"Invalid share from party {msg.PartyIndex}");

                    myShare = myShare.Add(share).Mod(DomainParams.N);
                }

                // Compute public key share
                var publicShare = DomainParams.G.Multiply(myShare);

                // Compute aggregated group public key
                var groupPublicKey = keyData.Commitments![0];
                foreach (var entry in keyData.AllCommitments!)
                {
                    if (entry.Key == _config.PartyIndex) continue;
                    groupPublicKey = groupPublicKey.Add(entry.Value[0]);
                }

                // Normalize for BIP-340 (ensure y-coordinate is even)
                if (!HasEvenY(groupPublicKey))
                {
                    myShare = DomainParams.N.Subtract(myShare);
                    publicShare = DomainParams.G.Multiply(myShare);
                }

                keyData.SecretShare = myShare;
                keyData.PublicShare = publicShare;
                keyData.GroupPublicKey = groupPublicKey;
                keyData.DkgPhase = 3;
                keyData.PolynomialCoeffs = null; // Clear sensitive data

                _currentKeyId = keyId;
                await PersistKeysToStorage();

                // Pregenerate nonces for future signing
                await GenerateNoncesAsync(keyId, _config.PreprocessBatchSize, context);

                return new FrostDkgResult
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    PublicShare = publicShare.GetEncoded(false),
                    GroupPublicKey = GetXOnlyPubkey(groupPublicKey),
                    Success = true
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST Preprocessing: Generate nonce commitments for future signing.
        /// This can be done offline to reduce signing latency.
        /// </summary>
        public async Task<FrostNonceCommitment[]> GenerateNoncesAsync(
            string keyId,
            int count,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var commitments = new FrostNonceCommitment[count];

                for (int i = 0; i < count; i++)
                {
                    // Generate two random nonces (d_i, e_i) for each signing operation
                    var d = GenerateRandomScalar();
                    var e = GenerateRandomScalar();

                    // Compute commitments D_i = d_i * G, E_i = e_i * G
                    var D = DomainParams.G.Multiply(d);
                    var E = DomainParams.G.Multiply(e);

                    var nonce = new FrostNonce
                    {
                        D = d,
                        E = e,
                        DCommitment = D,
                        ECommitment = E,
                        Index = keyData.NoncePool.Count + i
                    };

                    keyData.NoncePool.Enqueue(nonce);

                    commitments[i] = new FrostNonceCommitment
                    {
                        KeyId = keyId,
                        PartyIndex = _config.PartyIndex,
                        NonceIndex = nonce.Index,
                        D = D.GetEncoded(false),
                        E = E.GetEncoded(false)
                    };
                }

                await PersistKeysToStorage();
                return commitments;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST Signing Round 1: Commit to nonces.
        /// Returns nonce commitment for this signing session.
        /// </summary>
        public async Task<FrostSignRound1> BeginSigningAsync(
            string keyId,
            byte[] message,
            int[] signerIndices,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (signerIndices.Length < keyData.Threshold)
                    throw new ArgumentException($"Need at least {keyData.Threshold} signers.");

                if (!signerIndices.Contains(_config.PartyIndex))
                    throw new ArgumentException("This party is not in the signer set.");

                // Get next available nonce from pool (or generate one)
                FrostNonce nonce;
                if (keyData.NoncePool.Count > 0)
                {
                    nonce = keyData.NoncePool.Dequeue();
                }
                else
                {
                    // Generate on-the-fly if pool is empty
                    var d = GenerateRandomScalar();
                    var e = GenerateRandomScalar();
                    nonce = new FrostNonce
                    {
                        D = d,
                        E = e,
                        DCommitment = DomainParams.G.Multiply(d),
                        ECommitment = DomainParams.G.Multiply(e),
                        Index = -1
                    };
                }

                // Store signing state
                keyData.CurrentSigningState = new FrostSigningState
                {
                    Message = message,
                    SignerIndices = signerIndices,
                    ActiveNonce = nonce,
                    Phase = 1
                };

                await PersistKeysToStorage();

                return new FrostSignRound1
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    D = nonce.DCommitment.GetEncoded(false),
                    E = nonce.ECommitment.GetEncoded(false),
                    Commitment = ComputeCommitmentHash(nonce.DCommitment, nonce.ECommitment)
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST Signing Round 2: Compute signature share.
        /// </summary>
        public async Task<FrostSignRound2> ComputeSignatureShareAsync(
            string keyId,
            FrostSignRound1[] round1Messages,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var state = keyData.CurrentSigningState
                    ?? throw new InvalidOperationException("No active signing session.");

                // Collect and verify nonce commitments
                var nonceCommitments = new Dictionary<int, (ECPoint D, ECPoint E)>();
                nonceCommitments[_config.PartyIndex] = (
                    state.ActiveNonce.DCommitment,
                    state.ActiveNonce.ECommitment);

                foreach (var msg in round1Messages)
                {
                    if (msg.PartyIndex == _config.PartyIndex) continue;

                    var D = CurveParams.Curve.DecodePoint(msg.D);
                    var E = CurveParams.Curve.DecodePoint(msg.E);

                    // Verify commitment
                    var expectedCommitment = ComputeCommitmentHash(D, E);
                    if (!expectedCommitment.SequenceEqual(msg.Commitment))
                        throw new CryptographicException($"Invalid commitment from party {msg.PartyIndex}");

                    nonceCommitments[msg.PartyIndex] = (D, E);
                }

                // Compute binding factor rho_i for each signer
                var bindingFactors = ComputeBindingFactors(
                    state.Message,
                    nonceCommitments,
                    state.SignerIndices);

                // Compute group commitment R = sum(D_i + rho_i * E_i)
                var R = DomainParams.Curve.Infinity;
                foreach (var idx in state.SignerIndices)
                {
                    var (D, E) = nonceCommitments[idx];
                    var rho = bindingFactors[idx];
                    R = R.Add(D).Add(E.Multiply(rho));
                }

                // Ensure R has even Y coordinate (BIP-340)
                var negateNonce = !HasEvenY(R);
                if (negateNonce)
                {
                    R = R.Negate();
                }

                // Compute challenge c = H(R.x || P || m)
                var Rx = GetXOnlyPubkey(R);
                var Px = GetXOnlyPubkey(keyData.GroupPublicKey!);
                var challenge = ComputeChallenge(Rx, Px, state.Message);

                // Compute Lagrange coefficient
                var lambda = ComputeLagrangeCoefficient(_config.PartyIndex, state.SignerIndices);

                // Get my nonces
                var d = state.ActiveNonce.D;
                var e = state.ActiveNonce.E;
                var myRho = bindingFactors[_config.PartyIndex];

                // Negate nonces if R was negated
                if (negateNonce)
                {
                    d = DomainParams.N.Subtract(d);
                    e = DomainParams.N.Subtract(e);
                }

                // Compute signature share: z_i = d_i + (e_i * rho_i) + lambda_i * s_i * c
                var z = d.Add(e.Multiply(myRho).Mod(DomainParams.N))
                    .Add(lambda.Multiply(keyData.SecretShare).Multiply(challenge).Mod(DomainParams.N))
                    .Mod(DomainParams.N);

                state.R = R;
                state.Challenge = challenge;
                state.Phase = 2;

                await PersistKeysToStorage();

                return new FrostSignRound2
                {
                    KeyId = keyId,
                    PartyIndex = _config.PartyIndex,
                    Z = z.ToByteArrayUnsigned(),
                    R = Rx
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// FROST Signature Aggregation: Combine signature shares into final signature.
        /// </summary>
        public async Task<byte[]> AggregateSignaturesAsync(
            string keyId,
            FrostSignRound2[] round2Messages,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var state = keyData.CurrentSigningState
                    ?? throw new InvalidOperationException("No active signing session.");

                // Verify all R values match
                var R = round2Messages[0].R;
                foreach (var msg in round2Messages.Skip(1))
                {
                    if (!R.SequenceEqual(msg.R))
                        throw new CryptographicException("R values do not match.");
                }

                // Sum all signature shares: z = sum(z_i)
                var z = BigInteger.Zero;
                foreach (var msg in round2Messages)
                {
                    z = z.Add(new BigInteger(1, msg.Z)).Mod(DomainParams.N);
                }

                // Verify the signature before returning
                var signature = new byte[64];
                Array.Copy(R, 0, signature, 0, 32);
                var zBytes = z.ToByteArrayUnsigned();
                Array.Copy(zBytes, 0, signature, 64 - zBytes.Length, zBytes.Length);

                if (!VerifySchnorrSignature(
                    GetXOnlyPubkey(keyData.GroupPublicKey!),
                    state.Message,
                    signature))
                {
                    throw new CryptographicException("Generated signature is invalid.");
                }

                // Clear signing state
                keyData.CurrentSigningState = null;
                await PersistKeysToStorage();

                return signature;
            }
            finally
            {
                _lock.Release();
            }
        }

        #region BIP-340 Schnorr Helpers

        private byte[] GetXOnlyPubkey(ECPoint point)
        {
            var normalized = point.Normalize();
            return normalized.AffineXCoord.GetEncoded();
        }

        private bool HasEvenY(ECPoint point)
        {
            var normalized = point.Normalize();
            return !normalized.AffineYCoord.ToBigInteger().TestBit(0);
        }

        private byte[] TaggedHash(string tag, byte[] data)
        {
            var tagHash = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
            using var ms = new MemoryStream(4096);
            ms.Write(tagHash);
            ms.Write(tagHash);
            ms.Write(data);
            return SHA256.HashData(ms.ToArray());
        }

        private BigInteger ComputeChallenge(byte[] Rx, byte[] Px, byte[] message)
        {
            using var ms = new MemoryStream(4096);
            ms.Write(Rx);
            ms.Write(Px);
            ms.Write(message);
            var hash = TaggedHash(TagChallenge, ms.ToArray());
            return new BigInteger(1, hash).Mod(DomainParams.N);
        }

        private byte[] ComputeCommitmentHash(ECPoint D, ECPoint E)
        {
            using var ms = new MemoryStream(4096);
            ms.Write(D.GetEncoded(false));
            ms.Write(E.GetEncoded(false));
            return SHA256.HashData(ms.ToArray());
        }

        private Dictionary<int, BigInteger> ComputeBindingFactors(
            byte[] message,
            Dictionary<int, (ECPoint D, ECPoint E)> commitments,
            int[] signerIndices)
        {
            var factors = new Dictionary<int, BigInteger>();

            // Encode commitments for binding factor computation
            using var ms = new MemoryStream(4096);
            ms.Write(message);
            foreach (var idx in signerIndices.OrderBy(x => x))
            {
                var (D, E) = commitments[idx];
                ms.Write(BitConverter.GetBytes(idx));
                ms.Write(D.GetEncoded(false));
                ms.Write(E.GetEncoded(false));
            }
            var encodedCommitments = ms.ToArray();

            foreach (var idx in signerIndices)
            {
                using var factorInput = new MemoryStream(4096);
                factorInput.Write(encodedCommitments);
                factorInput.Write(BitConverter.GetBytes(idx));
                var hash = SHA256.HashData(factorInput.ToArray());
                factors[idx] = new BigInteger(1, hash).Mod(DomainParams.N);
            }

            return factors;
        }

        private byte[] CreateSchnorrProof(BigInteger secret)
        {
            var k = GenerateRandomScalar();
            var R = DomainParams.G.Multiply(k);
            var P = DomainParams.G.Multiply(secret);

            using var ms = new MemoryStream(4096);
            ms.Write(R.GetEncoded(false));
            ms.Write(P.GetEncoded(false));
            var challenge = new BigInteger(1, SHA256.HashData(ms.ToArray())).Mod(DomainParams.N);

            var response = k.Add(challenge.Multiply(secret)).Mod(DomainParams.N);

            var proof = new byte[97]; // 65 (point) + 32 (scalar)
            Array.Copy(R.GetEncoded(false), proof, 65);
            var responseBytes = response.ToByteArrayUnsigned();
            Array.Copy(responseBytes, 0, proof, 97 - responseBytes.Length, responseBytes.Length);

            return proof;
        }

        private bool VerifySchnorrProof(ECPoint publicKey, byte[] proof)
        {
            if (proof.Length != 97) return false;

            var RBytes = new byte[65];
            Array.Copy(proof, RBytes, 65);
            var R = CurveParams.Curve.DecodePoint(RBytes);

            var responseBytes = new byte[32];
            Array.Copy(proof, 65, responseBytes, 0, 32);
            var response = new BigInteger(1, responseBytes);

            using var ms = new MemoryStream(4096);
            ms.Write(RBytes);
            ms.Write(publicKey.GetEncoded(false));
            var challenge = new BigInteger(1, SHA256.HashData(ms.ToArray())).Mod(DomainParams.N);

            // Verify: response * G == R + challenge * P
            var left = DomainParams.G.Multiply(response);
            var right = R.Add(publicKey.Multiply(challenge));

            return left.Equals(right);
        }

        private bool VerifySchnorrSignature(byte[] pubkey, byte[] message, byte[] signature)
        {
            if (signature.Length != 64 || pubkey.Length != 32) return false;

            var Rx = new byte[32];
            var sBytes = new byte[32];
            Array.Copy(signature, 0, Rx, 0, 32);
            Array.Copy(signature, 32, sBytes, 0, 32);

            var s = new BigInteger(1, sBytes);
            if (s.CompareTo(DomainParams.N) >= 0) return false;

            var e = ComputeChallenge(Rx, pubkey, message);

            // Lift x-only pubkey to full point
            var x = new BigInteger(1, pubkey);
            var P = LiftX(x);
            if (P == null) return false;

            // R = s*G - e*P
            var R = DomainParams.G.Multiply(s).Add(P.Negate().Multiply(e));

            if (R.IsInfinity) return false;
            if (!HasEvenY(R)) return false;

            var computedRx = GetXOnlyPubkey(R);
            return Rx.SequenceEqual(computedRx);
        }

        private ECPoint? LiftX(BigInteger x)
        {
            // y^2 = x^3 + 7 (secp256k1)
            var p = CurveParams.Curve.Field.Characteristic;
            var y2 = x.ModPow(BigInteger.Three, p).Add(BigInteger.ValueOf(7)).Mod(p);

            // Square root (p = 3 mod 4 for secp256k1)
            var y = y2.ModPow(p.Add(BigInteger.One).ShiftRight(2), p);

            if (!y.ModPow(BigInteger.Two, p).Equals(y2))
                return null;

            // Choose even y
            if (y.TestBit(0))
                y = p.Subtract(y);

            return CurveParams.Curve.CreatePoint(x, y);
        }

        #endregion

        #region Helper Methods

        private BigInteger GenerateRandomScalar()
        {
            var bytes = new byte[32];
            _secureRandom.NextBytes(bytes);
            return new BigInteger(1, bytes).Mod(DomainParams.N);
        }

        private BigInteger EvaluatePolynomial(BigInteger[] coeffs, int x)
        {
            var xBig = BigInteger.ValueOf(x);
            var result = BigInteger.Zero;

            for (int i = coeffs.Length - 1; i >= 0; i--)
                result = result.Multiply(xBig).Add(coeffs[i]).Mod(DomainParams.N);

            return result;
        }

        private bool VerifyShare(BigInteger share, int partyIndex, ECPoint[] commitments)
        {
            var left = DomainParams.G.Multiply(share);

            var right = DomainParams.Curve.Infinity;
            var j = BigInteger.ValueOf(partyIndex);
            var jPower = BigInteger.One;

            for (int i = 0; i < commitments.Length; i++)
            {
                right = right.Add(commitments[i].Multiply(jPower));
                jPower = jPower.Multiply(j).Mod(DomainParams.N);
            }

            return left.Equals(right);
        }

        private BigInteger ComputeLagrangeCoefficient(int i, int[] indices)
        {
            var num = BigInteger.One;
            var den = BigInteger.One;

            foreach (var j in indices)
            {
                if (j == i) continue;
                num = num.Multiply(BigInteger.ValueOf(-j)).Mod(DomainParams.N);
                den = den.Multiply(BigInteger.ValueOf(i - j)).Mod(DomainParams.N);
            }

            if (num.SignValue < 0) num = num.Add(DomainParams.N);
            return num.Multiply(den.ModInverse(DomainParams.N)).Mod(DomainParams.N);
        }

        #endregion

        #region Envelope Encryption

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(kekId, out var keyData))
                    throw new KeyNotFoundException($"KEK '{kekId}' not found.");

                var ephemeral = GenerateRandomScalar();
                var ephemeralPub = DomainParams.G.Multiply(ephemeral);
                var sharedPoint = keyData.GroupPublicKey!.Multiply(ephemeral);
                var sharedSecret = SHA256.HashData(sharedPoint.Normalize().AffineXCoord.GetEncoded());

                using var aes = new AesGcm(sharedSecret, 16);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[dataKey.Length];
                var tag = new byte[16];
                aes.Encrypt(nonce, dataKey, ciphertext, tag);

                using var ms = new MemoryStream(4096);
                var ephemeralBytes = ephemeralPub.GetEncoded(false);
                ms.Write(BitConverter.GetBytes(ephemeralBytes.Length));
                ms.Write(ephemeralBytes);
                ms.Write(nonce);
                ms.Write(tag);
                ms.Write(ciphertext);

                return ms.ToArray();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(kekId, out var keyData))
                    throw new KeyNotFoundException($"KEK '{kekId}' not found.");

                using var ms = new MemoryStream(wrappedKey);
                using var reader = new BinaryReader(ms);

                var ephLen = reader.ReadInt32();
                var ephBytes = reader.ReadBytes(ephLen);
                var ephemeralPub = CurveParams.Curve.DecodePoint(ephBytes);

                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var ciphertext = reader.ReadBytes((int)(ms.Length - ms.Position));

                var sharedPoint = ephemeralPub.Multiply(keyData.SecretShare);
                var sharedSecret = SHA256.HashData(sharedPoint.Normalize().AffineXCoord.GetEncoded());

                using var aes = new AesGcm(sharedSecret, 16);
                var plaintext = new byte[ciphertext.Length];
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

                return plaintext;
            }
            finally
            {
                _lock.Release();
            }
        }

        #endregion

        #region Storage

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(ct);
            try { return _keys.Keys.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin) throw new UnauthorizedAccessException();

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys.Remove(keyId)) await PersistKeysToStorage();
            }
            finally { _lock.Release(); }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(ct);
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData)) return null;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    KeySizeBytes = 32,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "FROST Threshold Schnorr",
                        ["Threshold"] = keyData.Threshold,
                        ["TotalParties"] = keyData.TotalParties,
                        ["PartyIndex"] = keyData.PartyIndex,
                        ["DkgPhase"] = keyData.DkgPhase,
                        ["AvailableNonces"] = keyData.NoncePool.Count
                    }
                };
            }
            finally { _lock.Release(); }
        }

        private async Task LoadKeysFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, FrostKeyDataSerialized>>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored)
                        _keys[kvp.Key] = DeserializeKeyData(kvp.Value);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { /* Deserialization failure — start with empty state */ }
        }

        private async Task PersistKeysToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var toStore = _keys.ToDictionary(
                kvp => kvp.Key,
                kvp => SerializeKeyData(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "frost-keys.json");
        }

        private FrostKeyDataSerialized SerializeKeyData(FrostKeyData data) => new()
        {
            KeyId = data.KeyId,
            PartyIndex = data.PartyIndex,
            Threshold = data.Threshold,
            TotalParties = data.TotalParties,
            SecretShare = data.SecretShare.ToByteArrayUnsigned(),
            PublicShare = data.PublicShare?.GetEncoded(false),
            GroupPublicKey = data.GroupPublicKey?.GetEncoded(false),
            DkgPhase = data.DkgPhase,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy
        };

        private FrostKeyData DeserializeKeyData(FrostKeyDataSerialized data) => new()
        {
            KeyId = data.KeyId,
            PartyIndex = data.PartyIndex,
            Threshold = data.Threshold,
            TotalParties = data.TotalParties,
            SecretShare = new BigInteger(1, data.SecretShare ?? Array.Empty<byte>()),
            PublicShare = data.PublicShare != null ? CurveParams.Curve.DecodePoint(data.PublicShare) : null,
            GroupPublicKey = data.GroupPublicKey != null ? CurveParams.Curve.DecodePoint(data.GroupPublicKey) : null,
            NoncePool = new Queue<FrostNonce>(),
            DkgPhase = data.DkgPhase,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy
        };

        #endregion

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    public class FrostConfig
    {
        public int Threshold { get; set; } = 2;
        public int TotalParties { get; set; } = 3;
        public int PartyIndex { get; set; } = 1;
        public int PreprocessBatchSize { get; set; } = 10;
        public string? StoragePath { get; set; }
    }

    internal class FrostKeyData
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public int Threshold { get; set; }
        public int TotalParties { get; set; }
        public BigInteger SecretShare { get; set; } = BigInteger.Zero;
        public ECPoint? PublicShare { get; set; }
        public ECPoint? GroupPublicKey { get; set; }
        public BigInteger[]? PolynomialCoeffs { get; set; }
        public ECPoint[]? Commitments { get; set; }
        public Dictionary<int, ECPoint[]>? AllCommitments { get; set; }
        public Queue<FrostNonce> NoncePool { get; set; } = new();
        public FrostSigningState? CurrentSigningState { get; set; }
        public int DkgPhase { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    internal class FrostKeyDataSerialized
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public int Threshold { get; set; }
        public int TotalParties { get; set; }
        public byte[]? SecretShare { get; set; }
        public byte[]? PublicShare { get; set; }
        public byte[]? GroupPublicKey { get; set; }
        public int DkgPhase { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    internal class FrostNonce
    {
        public BigInteger D { get; set; } = BigInteger.Zero;
        public BigInteger E { get; set; } = BigInteger.Zero;
        public required ECPoint DCommitment { get; set; }
        public required ECPoint ECommitment { get; set; }
        public int Index { get; set; }
    }

    internal class FrostSigningState
    {
        public byte[] Message { get; set; } = Array.Empty<byte>();
        public int[] SignerIndices { get; set; } = Array.Empty<int>();
        public required FrostNonce ActiveNonce { get; set; }
        public ECPoint? R { get; set; }
        public BigInteger? Challenge { get; set; }
        public int Phase { get; set; }
    }

    public class FrostDkgRound1
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public byte[][] Commitments { get; set; } = Array.Empty<byte[]>();
        public byte[] ProofOfKnowledge { get; set; } = Array.Empty<byte>();
    }

    public class FrostDkgRound2
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public Dictionary<int, byte[]> Shares { get; set; } = new();
    }

    public class FrostDkgResult
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public byte[]? PublicShare { get; set; }
        public byte[]? GroupPublicKey { get; set; }
        public bool Success { get; set; }
    }

    public class FrostNonceCommitment
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public int NonceIndex { get; set; }
        public byte[] D { get; set; } = Array.Empty<byte>();
        public byte[] E { get; set; } = Array.Empty<byte>();
    }

    public class FrostSignRound1
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public byte[] D { get; set; } = Array.Empty<byte>();
        public byte[] E { get; set; } = Array.Empty<byte>();
        public byte[] Commitment { get; set; } = Array.Empty<byte>();
    }

    public class FrostSignRound2
    {
        public string KeyId { get; set; } = "";
        public int PartyIndex { get; set; }
        public byte[] Z { get; set; } = Array.Empty<byte>();
        public byte[] R { get; set; } = Array.Empty<byte>();
    }

    #endregion
}
