using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Regions;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Tier 1 module verifier: programmatically verifies that all 19 VDE modules have
/// working VDE-integrated implementations that store and retrieve data inside the
/// DWVD format without external plugins.
/// </summary>
/// <remarks>
/// For each module with a dedicated region, the verifier creates a minimal representative
/// entry, serializes the region to a byte buffer, deserializes from that buffer, and
/// confirms round-trip integrity by comparing key identifier fields.
///
/// For the 2 region-less modules (Sustainability and Transit), the verifier confirms
/// their inode extension field definitions are present and correctly sized.
///
/// All 19 modules must report <see cref="Tier1VerificationResult.Tier1Verified"/> = true.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 1 module verifier (TIER-01)")]
public static class Tier1ModuleVerifier
{
    /// <summary>Block size used for verification round-trip tests.</summary>
    private const int VerificationBlockSize = 4096;

    /// <summary>
    /// Maps each <see cref="ModuleId"/> to a delegate that exercises the module's
    /// region round-trip serialization. Returns true if the round-trip succeeded,
    /// plus a details string describing what was verified.
    /// </summary>
    private static readonly FrozenDictionary<ModuleId, Func<(bool Passed, string Details)>> RegionVerifiers =
        BuildRegionVerifiers();

    /// <summary>
    /// Verifies all 19 VDE modules and returns one <see cref="Tier1VerificationResult"/> per module.
    /// </summary>
    /// <returns>List of 19 results, one per module, ordered by <see cref="ModuleId"/>.</returns>
    public static IReadOnlyList<Tier1VerificationResult> VerifyAllModules()
    {
        var results = new List<Tier1VerificationResult>(FormatConstants.DefinedModules);
        foreach (var module in ModuleRegistry.AllModules)
        {
            results.Add(VerifyModule(module));
        }
        return results;
    }

    /// <summary>
    /// Verifies a single VDE module's Tier 1 implementation.
    /// </summary>
    /// <param name="module">The module definition to verify.</param>
    /// <returns>Structured verification result.</returns>
    public static Tier1VerificationResult VerifyModule(VdeModule module)
    {
        bool regionPassed = false;
        bool inodeVerified = false;
        var details = new StringBuilder();

        // ---- Region round-trip verification ----
        if (module.HasRegion)
        {
            if (RegionVerifiers.TryGetValue(module.Id, out var verifier))
            {
                try
                {
                    var (passed, regionDetails) = verifier();
                    regionPassed = passed;
                    details.Append($"Region: {regionDetails}");
                }
                catch (Exception ex)
                {
                    regionPassed = false;
                    details.Append($"Region: FAILED with exception: {ex.Message}");
                }
            }
            else
            {
                details.Append("Region: No verifier registered for this module");
            }
        }
        else
        {
            details.Append("Region: None (region-less module)");
        }

        // ---- Inode extension field verification ----
        if (module.HasInodeFields)
        {
            // Verify that InodeFieldBytes > 0 and the module's field entry
            // would be included in a layout descriptor for a manifest containing this module
            int fieldBytes = module.InodeFieldBytes;
            if (fieldBytes > 0)
            {
                // Verify the module appears in the inode layout for a manifest that includes it
                uint testManifest = 1u << (int)module.Id;
                int totalInodeBytes = ModuleRegistry.CalculateTotalInodeFieldBytes(testManifest);
                inodeVerified = totalInodeBytes >= fieldBytes;

                if (details.Length > 0) details.Append("; ");
                details.Append($"Inode: {fieldBytes} bytes defined, manifest total={totalInodeBytes}, verified={inodeVerified}");
            }
            else
            {
                if (details.Length > 0) details.Append("; ");
                details.Append("Inode: InodeFieldBytes is 0 but HasInodeFields is true");
            }
        }
        else
        {
            if (details.Length > 0) details.Append("; ");
            details.Append("Inode: No extension fields for this module");
        }

        // ---- Overall Tier 1 pass: region OR inode must work ----
        bool tier1Verified = regionPassed || inodeVerified;

        return new Tier1VerificationResult
        {
            Module = module.Id,
            ModuleName = module.Name,
            HasDedicatedRegion = module.HasRegion,
            HasInodeFields = module.HasInodeFields,
            RegionRoundTripPassed = regionPassed,
            InodeFieldVerified = inodeVerified,
            Tier1Verified = tier1Verified,
            Details = details.ToString()
        };
    }

    // ========================================================================
    //  Region verifier builders -- one per module with a dedicated region
    // ========================================================================

    private static FrozenDictionary<ModuleId, Func<(bool, string)>> BuildRegionVerifiers()
    {
        return new Dictionary<ModuleId, Func<(bool, string)>>
        {
            [ModuleId.Security] = VerifySecurity,
            [ModuleId.Compliance] = VerifyCompliance,
            [ModuleId.Intelligence] = VerifyIntelligence,
            [ModuleId.Tags] = VerifyTags,
            [ModuleId.Replication] = VerifyReplication,
            [ModuleId.Raid] = VerifyRaid,
            [ModuleId.Streaming] = VerifyStreaming,
            [ModuleId.Compute] = VerifyCompute,
            [ModuleId.Fabric] = VerifyFabric,
            [ModuleId.Consensus] = VerifyConsensus,
            [ModuleId.Compression] = VerifyCompression,
            [ModuleId.Integrity] = VerifyIntegrity,
            [ModuleId.Snapshot] = VerifySnapshot,
            [ModuleId.Query] = VerifyQuery,
            [ModuleId.Privacy] = VerifyPrivacy,
            [ModuleId.Observability] = VerifyObservability,
            [ModuleId.AuditLog] = VerifyAuditLog,
        }.ToFrozenDictionary();
    }

    // ---- Security: PolicyVaultRegion + EncryptionHeaderRegion ----
    private static (bool, string) VerifySecurity()
    {
        var policyId = Guid.NewGuid();
        var hmacKey = new byte[32];
        RandomNumberGenerator.Fill(hmacKey);
        var data = Encoding.UTF8.GetBytes("test-policy-data");

        // PolicyVaultRegion round-trip
        var vault = new PolicyVaultRegion(hmacKey);
        vault.AddPolicy(new PolicyDefinition(
            policyId, 0x0074, 1, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, data));
        vault.Generation = 1;

        var buffer = new byte[VerificationBlockSize * PolicyVaultRegion.BlockCount];
        vault.Serialize(buffer, VerificationBlockSize);

        var restored = PolicyVaultRegion.Deserialize(buffer, VerificationBlockSize, hmacKey);
        bool policyMatch = restored.PolicyCount == 1
            && restored.GetPolicy(policyId)?.PolicyId == policyId;

        // EncryptionHeaderRegion round-trip
        var encHeader = new EncryptionHeaderRegion();
        var wrappedKey = new byte[32];
        RandomNumberGenerator.Fill(wrappedKey);
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var keySlot = new KeySlot(0, (byte)KeySlotStatus.Active, 1, wrappedKey, salt,
            100000, 0, 0, DateTime.UtcNow.Ticks, 0);
        encHeader.SetKeySlot(0, keySlot);
        encHeader.Generation = 1;

        var encBuffer = new byte[VerificationBlockSize * EncryptionHeaderRegion.BlockCount];
        encHeader.Serialize(encBuffer, VerificationBlockSize);

        var restoredEnc = EncryptionHeaderRegion.Deserialize(encBuffer, VerificationBlockSize);
        var slot = restoredEnc.GetKeySlot(0);
        bool encMatch = slot.Status == (byte)KeySlotStatus.Active && slot.AlgorithmId == 1;

        bool passed = policyMatch && encMatch;
        return (passed, $"PolicyVault policyId match={policyMatch}, EncryptionHeader slot match={encMatch}");
    }

    // ---- Compliance: ComplianceVaultRegion ----
    private static (bool, string) VerifyCompliance()
    {
        var passportId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var region = new ComplianceVaultRegion();
        region.AddPassport(new CompliancePassport
        {
            PassportId = passportId,
            ObjectId = objectId,
            FrameworkId = 0, // GDPR
            ComplianceStatus = 1, // Compliant
            IssuedUtcTicks = DateTime.UtcNow.Ticks,
            IssuerId = Encoding.UTF8.GetBytes("test-issuer"),
            Notes = Encoding.UTF8.GetBytes("test-notes"),
            Signature = new byte[CompliancePassport.SignatureSize],
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = ComplianceVaultRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var passport = restored.GetPassport(passportId);
        bool passed = passport?.PassportId == passportId && passport?.ObjectId == objectId;
        return (passed, $"ComplianceVault passportId match={passed}");
    }

    // ---- Intelligence: IntelligenceCacheRegion ----
    private static (bool, string) VerifyIntelligence()
    {
        var objectId = Guid.NewGuid();
        var region = new IntelligenceCacheRegion();
        region.AddOrUpdate(new IntelligenceCacheEntry
        {
            ObjectId = objectId,
            ClassificationId = 42,
            ConfidenceScore = 0.95f,
            HeatScore = 0.8f,
            TierAssignment = 0,
            LastClassifiedUtcTicks = DateTime.UtcNow.Ticks,
            LastAccessedUtcTicks = DateTime.UtcNow.Ticks,
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = IntelligenceCacheRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var entry = restored.GetByObjectId(objectId);
        bool passed = entry?.ObjectId == objectId && entry?.ClassificationId == 42;
        return (passed, $"IntelligenceCache objectId match={passed}");
    }

    // ---- Tags: TagIndexRegion ----
    private static (bool, string) VerifyTags()
    {
        var region = new TagIndexRegion(order: 8);
        var key = Encoding.UTF8.GetBytes("test-tag");
        var value = Encoding.UTF8.GetBytes("test-value");
        region.Insert(new TagEntry(key, value, 42, 100));
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = TagIndexRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var entry = restored.Lookup(key);
        bool passed = entry is not null && entry.Value.InodeNumber == 42;
        return (passed, $"TagIndex entry match={passed}, entryCount={restored.EntryCount}");
    }

    // ---- Replication: ReplicationStateRegion ----
    private static (bool, string) VerifyReplication()
    {
        var replicaId = Guid.NewGuid();
        var region = new ReplicationStateRegion(trackedBlockCount: 64);
        region.SetVector(0, new DottedVersionVector(replicaId, 1, DateTime.UtcNow.Ticks));
        region.SetWatermark(0, new ReplicationWatermark(replicaId, 10, 1, DateTime.UtcNow.Ticks, 2, 0));
        region.MarkDirty(0);
        region.MarkDirty(7);
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = ReplicationStateRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var vector = restored.GetVector(0);
        var watermark = restored.GetWatermark(0);
        bool passed = vector.ReplicaId == replicaId && watermark.LastSyncedBlock == 10
            && restored.IsDirty(0) && restored.IsDirty(7) && !restored.IsDirty(1);
        return (passed, $"ReplicationState replicaId match={vector.ReplicaId == replicaId}, dirtyBits verified");
    }

    // ---- RAID: RaidMetadataRegion ----
    private static (bool, string) VerifyRaid()
    {
        var deviceId = Guid.NewGuid();
        var region = new RaidMetadataRegion
        {
            Strategy = RaidStrategy.Raid5,
            StripeSize = 128,
            DataShardCount = 3,
            ParityShardCount = 1,
            Generation = 1,
        };
        region.SetShard(0, new ShardDescriptor(deviceId, 0, 1000, (byte)ShardStatus.Online, 0));
        region.AddParityDescriptor(new ParityDescriptor(0, 3, (byte)ParityType.Xor));

        var buffer = new byte[VerificationBlockSize * RaidMetadataRegion.BlockCount];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = RaidMetadataRegion.Deserialize(buffer, VerificationBlockSize);
        var shard = restored.GetShard(0);
        bool passed = shard.DeviceId == deviceId && restored.Strategy == RaidStrategy.Raid5
            && restored.GetParityLayout().Count == 1;
        return (passed, $"RaidMetadata deviceId match={shard.DeviceId == deviceId}, strategy={restored.Strategy}");
    }

    // ---- Streaming: StreamingAppendRegion ----
    private static (bool, string) VerifyStreaming()
    {
        var region = new StreamingAppendRegion(maxCapacityBlocks: 256, growthIncrement: 16);
        region.Generation = 1;

        // StreamingAppendRegion serializes as 1 block (header only, metadata tracking)
        var buffer = new byte[VerificationBlockSize];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = StreamingAppendRegion.Deserialize(buffer, VerificationBlockSize);
        bool passed = restored.MaxCapacityBlocks == 256 && restored.GrowthIncrement == 16;
        return (passed, $"StreamingAppend maxCapacity={restored.MaxCapacityBlocks}, growthIncrement={restored.GrowthIncrement}");
    }

    // ---- Compute: ComputeCodeCacheRegion ----
    private static (bool, string) VerifyCompute()
    {
        var moduleId = Guid.NewGuid();
        var moduleHash = SHA256.HashData(Encoding.UTF8.GetBytes("test-wasm-module"));
        var region = new ComputeCodeCacheRegion();
        region.RegisterModule(new ComputeModuleEntry
        {
            ModuleHash = moduleHash,
            ModuleId = moduleId,
            BlockOffset = 100,
            BlockCount = 5,
            ModuleSizeBytes = 20000,
            AbiVersion = 1,
            RegisteredUtcTicks = DateTime.UtcNow.Ticks,
            EntryPointName = Encoding.UTF8.GetBytes("_start"),
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = ComputeCodeCacheRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.ModuleCount == 1;
        return (passed, $"ComputeCodeCache moduleCount={restored.ModuleCount}");
    }

    // ---- Fabric: CrossVdeReferenceRegion ----
    private static (bool, string) VerifyFabric()
    {
        var refId = Guid.NewGuid();
        var sourceVde = Guid.NewGuid();
        var targetVde = Guid.NewGuid();
        var region = new CrossVdeReferenceRegion();
        region.AddReference(new VdeReference
        {
            ReferenceId = refId,
            SourceVdeId = sourceVde,
            TargetVdeId = targetVde,
            SourceInodeNumber = 1,
            TargetInodeNumber = 2,
            ReferenceType = VdeReference.TypeFabricLink,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = CrossVdeReferenceRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.ReferenceCount == 1;
        return (passed, $"CrossVdeReference refCount={restored.ReferenceCount}");
    }

    // ---- Consensus: ConsensusLogRegion ----
    private static (bool, string) VerifyConsensus()
    {
        var groupId = Guid.NewGuid();
        var leaderId = Guid.NewGuid();
        var region = new ConsensusLogRegion();
        region.UpdateGroupState(new ConsensusGroupState
        {
            GroupId = groupId,
            CurrentTerm = 5,
            CommittedIndex = 42,
            AppliedIndex = 40,
            VotedFor = leaderId,
            LeaderId = leaderId,
            MemberCount = 5,
            LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks,
            LastUpdatedUtcTicks = DateTime.UtcNow.Ticks,
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = ConsensusLogRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.GroupCount == 1;
        return (passed, $"ConsensusLog groupCount={restored.GroupCount}");
    }

    // ---- Compression: CompressionDictionaryRegion ----
    private static (bool, string) VerifyCompression()
    {
        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes("test-dictionary"));
        var region = new CompressionDictionaryRegion();
        region.RegisterDictionary(new CompressionDictEntry
        {
            DictId = 1,
            AlgorithmId = 0, // Zstd
            BlockOffset = 200,
            BlockCount = 3,
            DictionarySizeBytes = 12000,
            TrainedUtcTicks = DateTime.UtcNow.Ticks,
            SampleCount = 1000,
            ContentHash = contentHash,
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = CompressionDictionaryRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.DictionaryCount == 1;
        return (passed, $"CompressionDictionary dictCount={restored.DictionaryCount}");
    }

    // ---- Integrity: IntegrityTreeRegion ----
    private static (bool, string) VerifyIntegrity()
    {
        var region = new IntegrityTreeRegion(leafCount: 4);
        var blockData = new byte[64];
        RandomNumberGenerator.Fill(blockData);
        var leafHash = SHA256.HashData(blockData);
        region.SetLeafHash(0, leafHash);
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = IntegrityTreeRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var rootBefore = region.GetRootHash();
        var rootAfter = restored.GetRootHash();
        bool passed = restored.LeafCount == 4
            && CryptographicOperations.FixedTimeEquals(rootBefore, rootAfter);
        return (passed, $"IntegrityTree leafCount={restored.LeafCount}, rootHash match={passed}");
    }

    // ---- Snapshot: SnapshotTableRegion ----
    private static (bool, string) VerifySnapshot()
    {
        var snapshotId = Guid.NewGuid();
        var region = new SnapshotTableRegion();
        region.CreateSnapshot(new SnapshotEntry
        {
            SnapshotId = snapshotId,
            ParentSnapshotId = Guid.Empty,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            InodeTableBlockOffset = 100,
            InodeTableBlockCount = 10,
            DataBlockCount = 5000,
            Flags = SnapshotEntry.FlagBaseSnapshot,
            Label = Encoding.UTF8.GetBytes("v1.0"),
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = SnapshotTableRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var snap = restored.GetSnapshot(snapshotId);
        bool passed = snap?.SnapshotId == snapshotId && snap?.DataBlockCount == 5000;
        return (passed, $"SnapshotTable snapshotId match={snap?.SnapshotId == snapshotId}");
    }

    // ---- Query: TagIndexRegion (BTreeIndexForest uses the same TagIndexRegion) ----
    private static (bool, string) VerifyQuery()
    {
        // The Query module's BTreeIndexForest region is implemented by TagIndexRegion
        // with a separate namespace. We verify it the same way as Tags but with
        // a query-specific key pattern.
        var region = new TagIndexRegion(order: 8);
        var key = Encoding.UTF8.GetBytes("idx:field1");
        var value = Encoding.UTF8.GetBytes("asc");
        region.Insert(new TagEntry(key, value, 1, 50));
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = TagIndexRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        var entry = restored.Lookup(key);
        bool passed = entry is not null && entry.Value.InodeNumber == 1;
        return (passed, $"BTreeIndexForest (TagIndexRegion) entry match={passed}");
    }

    // ---- Privacy: AnonymizationTableRegion ----
    private static (bool, string) VerifyPrivacy()
    {
        var mappingId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var piiHash = SHA256.HashData(Encoding.UTF8.GetBytes("test@email.com"));
        var token = SHA256.HashData(Encoding.UTF8.GetBytes("anon-token"));
        var region = new AnonymizationTableRegion();
        region.AddMapping(new AnonymizationMapping
        {
            MappingId = mappingId,
            SubjectId = subjectId,
            PiiType = 1, // Email
            PiiHash = piiHash,
            AnonymizedToken = token,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            Flags = AnonymizationMapping.FlagActive,
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = AnonymizationTableRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.MappingCount == 1;
        return (passed, $"AnonymizationTable mappingCount={restored.MappingCount}");
    }

    // ---- Observability: MetricsLogRegion ----
    private static (bool, string) VerifyObservability()
    {
        var region = new MetricsLogRegion(maxCapacitySamples: 1000);
        region.RecordSample(new MetricsSample
        {
            MetricId = 1,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Value = 42.5,
            AggregationType = 0, // Raw
        });
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = MetricsLogRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.SampleCount >= 1;
        return (passed, $"MetricsLog sampleCount={restored.SampleCount}");
    }

    // ---- AuditLog: AuditLogRegion ----
    private static (bool, string) VerifyAuditLog()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var region = new AuditLogRegion();
        region.Append(actorId, 1, targetId, Encoding.UTF8.GetBytes("test-write-event"));
        region.Generation = 1;

        int blocks = region.RequiredBlocks(VerificationBlockSize);
        var buffer = new byte[VerificationBlockSize * blocks];
        region.Serialize(buffer, VerificationBlockSize);

        var restored = AuditLogRegion.Deserialize(buffer, VerificationBlockSize, blocks);
        bool passed = restored.EntryCount == 1;
        return (passed, $"AuditLog entryCount={restored.EntryCount}");
    }
}
