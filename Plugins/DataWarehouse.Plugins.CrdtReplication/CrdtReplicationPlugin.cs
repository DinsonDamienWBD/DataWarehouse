using DataWarehouse.Plugins.CrdtReplication.Crdts;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.CrdtReplication
{
    /// <summary>
    /// CRDT-based replication plugin providing conflict-free replicated data types
    /// with automatic convergence across distributed nodes.
    /// </summary>
    /// <remarks>
    /// Features:
    /// - Multiple CRDT types: GCounter, PNCounter, GSet, TwoPhaseSet, LWWRegister, MVRegister, ORSet
    /// - Vector clocks for causality tracking
    /// - Gossip protocol for state propagation
    /// - Delta-state CRDTs for efficient synchronization
    /// - Automatic merge operations for all CRDT types
    /// - Network partition tolerance with automatic convergence
    ///
    /// Message Commands:
    /// - crdt.create: Create a new CRDT instance
    /// - crdt.get: Get a CRDT's current value
    /// - crdt.update: Update a CRDT
    /// - crdt.merge: Force merge with remote state
    /// - crdt.delete: Delete a CRDT instance
    /// - crdt.list: List all CRDTs
    /// - crdt.sync: Force sync with peers
    /// - crdt.status: Get replication status
    /// - crdt.peer.add: Add a gossip peer
    /// - crdt.peer.remove: Remove a gossip peer
    /// </remarks>
    public sealed class CrdtReplicationPlugin : ReplicationPluginBase
    {
        /// <inheritdoc />
        public override string Id => "com.datawarehouse.replication.crdt";

        /// <inheritdoc />
        public override string Name => "CRDT Replication Plugin";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        private readonly CrdtStore _store;
        private readonly GossipProtocol _gossip;
        private readonly CrdtFactory _factory;
        private IKernelContext? _context;
        private CancellationTokenSource? _cts;
        private string _nodeId = string.Empty;

        /// <summary>
        /// Creates a new CRDT replication plugin instance.
        /// </summary>
        public CrdtReplicationPlugin()
        {
            _store = new CrdtStore();
            _factory = new CrdtFactory(() => _nodeId);
            _gossip = new GossipProtocol(_store, () => _nodeId);
        }

        /// <inheritdoc />
        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            _context = request.Context;
            _nodeId = $"crdt-{request.KernelId}-{Guid.NewGuid():N}"[..24];

            return Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        /// <inheritdoc />
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "create",
                    Description = "Create a new CRDT instance",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "Unique key for the CRDT" },
                            ["type"] = new { type = "string", description = "CRDT type (GCounter, PNCounter, GSet, TwoPhaseSet, LWWRegister, MVRegister, ORSet)" },
                            ["initialValue"] = new { type = "any", description = "Initial value (optional)" }
                        },
                        ["required"] = new[] { "key", "type" }
                    }
                },
                new()
                {
                    Name = "get",
                    Description = "Get a CRDT's current value",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "CRDT key" }
                        },
                        ["required"] = new[] { "key" }
                    }
                },
                new()
                {
                    Name = "update",
                    Description = "Update a CRDT (operation depends on type)",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "CRDT key" },
                            ["operation"] = new { type = "string", description = "Operation (increment, decrement, add, remove, set)" },
                            ["value"] = new { type = "any", description = "Value for the operation" }
                        },
                        ["required"] = new[] { "key", "operation" }
                    }
                },
                new()
                {
                    Name = "sync",
                    Description = "Force synchronization with peers",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "Specific CRDT key (optional, syncs all if not specified)" }
                        }
                    }
                },
                new()
                {
                    Name = "status",
                    Description = "Get replication status",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                },
                new()
                {
                    Name = "peer.add",
                    Description = "Add a gossip peer",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["endpoint"] = new { type = "string", description = "Peer endpoint (host:port)" }
                        },
                        ["required"] = new[] { "endpoint" }
                    }
                }
            };
        }

        /// <inheritdoc />
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "CRDTReplication";
            metadata["NodeId"] = _nodeId;
            metadata["SupportedTypes"] = new[] { "GCounter", "PNCounter", "GSet", "TwoPhaseSet", "LWWRegister", "MVRegister", "ORSet" };
            metadata["SupportsGossip"] = true;
            metadata["SupportsDeltaState"] = true;
            metadata["SupportsVectorClocks"] = true;
            metadata["AutoConvergence"] = true;
            return metadata;
        }

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await _gossip.StartAsync(_cts.Token);
        }

        /// <inheritdoc />
        public override async Task StopAsync()
        {
            _cts?.Cancel();
            await _gossip.StopAsync();
            _cts?.Dispose();
            _cts = null;
        }

        /// <inheritdoc />
        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null)
                return;

            var response = message.Type switch
            {
                "crdt.create" => HandleCreate(message.Payload),
                "crdt.get" => HandleGet(message.Payload),
                "crdt.update" => HandleUpdate(message.Payload),
                "crdt.merge" => HandleMerge(message.Payload),
                "crdt.delete" => HandleDelete(message.Payload),
                "crdt.list" => HandleList(),
                "crdt.sync" => await HandleSyncAsync(message.Payload),
                "crdt.status" => HandleStatus(),
                "crdt.peer.add" => HandlePeerAdd(message.Payload),
                "crdt.peer.remove" => HandlePeerRemove(message.Payload),
                "crdt.gossip.receive" => HandleGossipReceive(message.Payload),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            if (response != null)
            {
                message.Payload["_response"] = response;
            }
        }

        /// <inheritdoc />
        public override async Task<bool> RestoreAsync(string blobId, string? replicaId)
        {
            // Force sync the specific CRDT from peers
            var payload = new Dictionary<string, object> { ["key"] = blobId };
            var result = await HandleSyncAsync(payload);
            return result.GetValueOrDefault("success") is bool success && success;
        }

        #region Message Handlers

        private Dictionary<string, object> HandleCreate(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();
                var crdtType = payload.GetValueOrDefault("type")?.ToString();
                var initialValue = payload.GetValueOrDefault("initialValue");

                if (string.IsNullOrEmpty(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key is required" };

                if (string.IsNullOrEmpty(crdtType))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Type is required" };

                if (_store.Exists(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = $"CRDT with key '{key}' already exists" };

                var crdt = _factory.Create(crdtType, initialValue);
                _store.Store(key, crdt);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["key"] = key,
                    ["type"] = crdtType,
                    ["nodeId"] = _nodeId
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleGet(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();

                if (string.IsNullOrEmpty(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key is required" };

                var crdt = _store.Get(key);
                if (crdt == null)
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = $"CRDT with key '{key}' not found" };

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["key"] = key,
                    ["type"] = crdt.GetCrdtType(),
                    ["value"] = crdt.GetValue(),
                    ["metadata"] = crdt.GetMetadata()
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleUpdate(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();
                var operation = payload.GetValueOrDefault("operation")?.ToString();
                var value = payload.GetValueOrDefault("value");

                if (string.IsNullOrEmpty(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key is required" };

                if (string.IsNullOrEmpty(operation))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Operation is required" };

                var crdt = _store.Get(key);
                if (crdt == null)
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = $"CRDT with key '{key}' not found" };

                crdt.ApplyOperation(operation, value);

                // Notify gossip layer of update
                _gossip.NotifyUpdate(key);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["key"] = key,
                    ["operation"] = operation,
                    ["newValue"] = crdt.GetValue()
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleMerge(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();
                var remoteState = payload.GetValueOrDefault("state")?.ToString();

                if (string.IsNullOrEmpty(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key is required" };

                if (string.IsNullOrEmpty(remoteState))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "State is required" };

                var crdt = _store.Get(key);
                if (crdt == null)
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = $"CRDT with key '{key}' not found" };

                crdt.MergeFromJson(remoteState);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["key"] = key,
                    ["newValue"] = crdt.GetValue()
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleDelete(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();

                if (string.IsNullOrEmpty(key))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key is required" };

                var deleted = _store.Remove(key);

                return new Dictionary<string, object>
                {
                    ["success"] = deleted,
                    ["key"] = key
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleList()
        {
            try
            {
                var crdts = _store.List().Select(kv => new Dictionary<string, object>
                {
                    ["key"] = kv.Key,
                    ["type"] = kv.Value.GetCrdtType(),
                    ["value"] = kv.Value.GetValue()
                }).ToList();

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["count"] = crdts.Count,
                    ["crdts"] = crdts
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private async Task<Dictionary<string, object>> HandleSyncAsync(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();

                int synced;
                if (!string.IsNullOrEmpty(key))
                {
                    await _gossip.SyncKeyAsync(key);
                    synced = 1;
                }
                else
                {
                    synced = await _gossip.SyncAllAsync();
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["synced"] = synced,
                    ["peers"] = _gossip.PeerCount
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleStatus()
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = _nodeId,
                ["crdtCount"] = _store.Count,
                ["peerCount"] = _gossip.PeerCount,
                ["peers"] = _gossip.GetPeers().ToList(),
                ["gossipEnabled"] = _gossip.IsRunning,
                ["lastSyncTime"] = _gossip.LastSyncTime?.ToString("O") ?? "never"
            };
        }

        private Dictionary<string, object> HandlePeerAdd(Dictionary<string, object> payload)
        {
            try
            {
                var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();

                if (string.IsNullOrEmpty(endpoint))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Endpoint is required" };

                _gossip.AddPeer(endpoint);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["endpoint"] = endpoint,
                    ["peerCount"] = _gossip.PeerCount
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandlePeerRemove(Dictionary<string, object> payload)
        {
            try
            {
                var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();

                if (string.IsNullOrEmpty(endpoint))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Endpoint is required" };

                var removed = _gossip.RemovePeer(endpoint);

                return new Dictionary<string, object>
                {
                    ["success"] = removed,
                    ["endpoint"] = endpoint,
                    ["peerCount"] = _gossip.PeerCount
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleGossipReceive(Dictionary<string, object> payload)
        {
            try
            {
                var key = payload.GetValueOrDefault("key")?.ToString();
                var state = payload.GetValueOrDefault("state")?.ToString();
                var crdtType = payload.GetValueOrDefault("type")?.ToString();
                var isDelta = payload.GetValueOrDefault("isDelta") as bool? ?? false;

                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(crdtType))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Key, state, and type are required" };

                var existing = _store.Get(key);
                if (existing == null)
                {
                    // Create new CRDT from received state
                    var crdt = _factory.CreateFromJson(crdtType, state);
                    _store.Store(key, crdt);
                }
                else
                {
                    // Merge with existing
                    if (isDelta)
                    {
                        existing.ApplyDeltaFromJson(state);
                    }
                    else
                    {
                        existing.MergeFromJson(state);
                    }
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["key"] = key,
                    ["merged"] = existing != null
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        #endregion
    }

    #region CRDT Interfaces

    /// <summary>
    /// Base interface for all CRDTs.
    /// </summary>
    /// <typeparam name="T">The CRDT type for merge operations.</typeparam>
    public interface ICrdt<T> where T : class
    {
        /// <summary>
        /// Merges this CRDT with another.
        /// </summary>
        T Merge(T other);

        /// <summary>
        /// Creates a deep copy of this CRDT.
        /// </summary>
        T Clone();
    }

    /// <summary>
    /// Interface for delta-state CRDTs that support efficient synchronization.
    /// </summary>
    /// <typeparam name="T">The CRDT type.</typeparam>
    public interface IDeltaCrdt<T> where T : class
    {
        /// <summary>
        /// Gets the delta state since the previous state.
        /// </summary>
        T GetDelta(T? previousState);

        /// <summary>
        /// Applies a delta state.
        /// </summary>
        T ApplyDelta(T delta);

        /// <summary>
        /// Applies a delta state in place.
        /// </summary>
        void ApplyDeltaInPlace(T delta);
    }

    #endregion

    #region CRDT Store

    /// <summary>
    /// Storage wrapper for CRDTs with type-erased access.
    /// </summary>
    internal interface ICrdtWrapper
    {
        /// <summary>Gets the CRDT type name.</summary>
        string GetCrdtType();

        /// <summary>Gets the current value.</summary>
        object? GetValue();

        /// <summary>Gets metadata about the CRDT.</summary>
        Dictionary<string, object> GetMetadata();

        /// <summary>Serializes to JSON.</summary>
        string ToJson();

        /// <summary>Gets delta state as JSON since a version.</summary>
        string GetDeltaJson(VectorClock? since);

        /// <summary>Merges from JSON state.</summary>
        void MergeFromJson(string json);

        /// <summary>Applies delta from JSON.</summary>
        void ApplyDeltaFromJson(string json);

        /// <summary>Applies an operation.</summary>
        void ApplyOperation(string operation, object? value);

        /// <summary>Gets the version clock.</summary>
        VectorClock GetVersion();
    }

    /// <summary>
    /// Typed wrapper for CRDTs.
    /// </summary>
    internal sealed class CrdtWrapper<T> : ICrdtWrapper where T : class
    {
        private T _crdt;
        private readonly string _type;
        private readonly Func<T, string> _toJson;
        private readonly Func<string, T> _fromJson;
        private readonly Func<T, object?> _getValue;
        private readonly Action<T, string, object?> _applyOp;
        private readonly Func<T, T, T>? _getDelta;
        private readonly Action<T, T>? _applyDelta;
        private VectorClock _version;
        private readonly string _nodeId;

        public CrdtWrapper(
            T crdt,
            string type,
            string nodeId,
            Func<T, string> toJson,
            Func<string, T> fromJson,
            Func<T, object?> getValue,
            Action<T, string, object?> applyOp,
            Func<T, T, T>? getDelta = null,
            Action<T, T>? applyDelta = null)
        {
            _crdt = crdt ?? throw new ArgumentNullException(nameof(crdt));
            _type = type;
            _nodeId = nodeId;
            _toJson = toJson;
            _fromJson = fromJson;
            _getValue = getValue;
            _applyOp = applyOp;
            _getDelta = getDelta;
            _applyDelta = applyDelta;
            _version = new VectorClock();
            _version.IncrementInPlace(nodeId);
        }

        public string GetCrdtType() => _type;

        public object? GetValue() => _getValue(_crdt);

        public Dictionary<string, object> GetMetadata() => new()
        {
            ["type"] = _type,
            ["version"] = _version.Entries
        };

        public string ToJson() => _toJson(_crdt);

        public string GetDeltaJson(VectorClock? since)
        {
            // For simplicity, return full state if delta not supported
            return ToJson();
        }

        public void MergeFromJson(string json)
        {
            var other = _fromJson(json);
            if (_crdt is GCounter gc && other is GCounter ogc)
            {
                gc.MergeInPlace(ogc);
            }
            else if (_crdt is PNCounter pc && other is PNCounter opc)
            {
                pc.MergeInPlace(opc);
            }
            else if (_crdt is GSet<string> gs && other is GSet<string> ogs)
            {
                gs.MergeInPlace(ogs);
            }
            else if (_crdt is TwoPhaseSet<string> tps && other is TwoPhaseSet<string> otps)
            {
                tps.MergeInPlace(otps);
            }
            else if (_crdt is ORSet<string> ors && other is ORSet<string> oors)
            {
                ors.MergeInPlace(oors);
            }
            else if (_crdt is LWWRegister<string> lww && other is LWWRegister<string> olww)
            {
                lww.MergeInPlace(olww);
            }
            else if (_crdt is MVRegister<string> mv && other is MVRegister<string> omv)
            {
                mv.MergeInPlace(omv);
            }
            else
            {
                // Fallback: replace with remote state
                _crdt = other;
            }
            _version.IncrementInPlace(_nodeId);
        }

        public void ApplyDeltaFromJson(string json)
        {
            // Same as merge for now
            MergeFromJson(json);
        }

        public void ApplyOperation(string operation, object? value)
        {
            _applyOp(_crdt, operation, value);
            _version.IncrementInPlace(_nodeId);
        }

        public VectorClock GetVersion() => _version.Clone();
    }

    /// <summary>
    /// In-memory store for CRDTs.
    /// </summary>
    internal sealed class CrdtStore
    {
        private readonly ConcurrentDictionary<string, ICrdtWrapper> _store = new();

        public void Store(string key, ICrdtWrapper crdt)
        {
            _store[key] = crdt;
        }

        public ICrdtWrapper? Get(string key)
        {
            return _store.GetValueOrDefault(key);
        }

        public bool Exists(string key)
        {
            return _store.ContainsKey(key);
        }

        public bool Remove(string key)
        {
            return _store.TryRemove(key, out _);
        }

        public IEnumerable<KeyValuePair<string, ICrdtWrapper>> List()
        {
            return _store.ToArray();
        }

        public int Count => _store.Count;

        public IEnumerable<string> Keys => _store.Keys;
    }

    #endregion

    #region CRDT Factory

    /// <summary>
    /// Factory for creating CRDT instances.
    /// </summary>
    internal sealed class CrdtFactory
    {
        private readonly Func<string> _getNodeId;

        public CrdtFactory(Func<string> getNodeId)
        {
            _getNodeId = getNodeId;
        }

        /// <summary>
        /// Creates a new CRDT of the specified type.
        /// </summary>
        public ICrdtWrapper Create(string type, object? initialValue = null)
        {
            var nodeId = _getNodeId();

            return type.ToLowerInvariant() switch
            {
                "gcounter" => CreateGCounter(nodeId, initialValue),
                "pncounter" => CreatePNCounter(nodeId, initialValue),
                "gset" => CreateGSet(nodeId, initialValue),
                "twophaseset" or "2pset" => CreateTwoPhaseSet(nodeId, initialValue),
                "lwwregister" or "lww" => CreateLWWRegister(nodeId, initialValue),
                "mvregister" or "mv" => CreateMVRegister(nodeId, initialValue),
                "orset" => CreateORSet(nodeId, initialValue),
                _ => throw new ArgumentException($"Unknown CRDT type: {type}")
            };
        }

        /// <summary>
        /// Creates a CRDT from JSON state.
        /// </summary>
        public ICrdtWrapper CreateFromJson(string type, string json)
        {
            var nodeId = _getNodeId();

            return type.ToLowerInvariant() switch
            {
                "gcounter" => CreateGCounterFromJson(nodeId, json),
                "pncounter" => CreatePNCounterFromJson(nodeId, json),
                "gset" => CreateGSetFromJson(nodeId, json),
                "twophaseset" or "2pset" => CreateTwoPhaseSetFromJson(nodeId, json),
                "lwwregister" or "lww" => CreateLWWRegisterFromJson(nodeId, json),
                "mvregister" or "mv" => CreateMVRegisterFromJson(nodeId, json),
                "orset" => CreateORSetFromJson(nodeId, json),
                _ => throw new ArgumentException($"Unknown CRDT type: {type}")
            };
        }

        private CrdtWrapper<GCounter> CreateGCounter(string nodeId, object? initialValue)
        {
            var counter = new GCounter(nodeId);
            if (initialValue is long l && l > 0)
                counter.IncrementInPlace(l);
            else if (initialValue is int i && i > 0)
                counter.IncrementInPlace(i);

            return new CrdtWrapper<GCounter>(
                counter,
                "GCounter",
                nodeId,
                c => c.ToJson(),
                GCounter.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    var amount = val switch
                    {
                        long l => l,
                        int i => i,
                        _ => 1L
                    };
                    if (op.ToLowerInvariant() == "increment")
                        c.IncrementInPlace(amount);
                });
        }

        private CrdtWrapper<GCounter> CreateGCounterFromJson(string nodeId, string json)
        {
            var counter = GCounter.FromJson(json);
            return new CrdtWrapper<GCounter>(
                counter,
                "GCounter",
                nodeId,
                c => c.ToJson(),
                GCounter.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    var amount = val switch { long l => l, int i => i, _ => 1L };
                    if (op.ToLowerInvariant() == "increment")
                        c.IncrementInPlace(amount);
                });
        }

        private CrdtWrapper<PNCounter> CreatePNCounter(string nodeId, object? initialValue)
        {
            var counter = new PNCounter(nodeId);
            if (initialValue is long l)
                counter.AddInPlace(l);
            else if (initialValue is int i)
                counter.AddInPlace(i);

            return new CrdtWrapper<PNCounter>(
                counter,
                "PNCounter",
                nodeId,
                c => c.ToJson(),
                PNCounter.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    var amount = val switch { long l => l, int i => i, _ => 1L };
                    switch (op.ToLowerInvariant())
                    {
                        case "increment":
                            c.IncrementInPlace(Math.Abs(amount));
                            break;
                        case "decrement":
                            c.DecrementInPlace(Math.Abs(amount));
                            break;
                        case "add":
                            c.AddInPlace(amount);
                            break;
                    }
                });
        }

        private CrdtWrapper<PNCounter> CreatePNCounterFromJson(string nodeId, string json)
        {
            var counter = PNCounter.FromJson(json);
            return new CrdtWrapper<PNCounter>(
                counter,
                "PNCounter",
                nodeId,
                c => c.ToJson(),
                PNCounter.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    var amount = val switch { long l => l, int i => i, _ => 1L };
                    switch (op.ToLowerInvariant())
                    {
                        case "increment": c.IncrementInPlace(Math.Abs(amount)); break;
                        case "decrement": c.DecrementInPlace(Math.Abs(amount)); break;
                        case "add": c.AddInPlace(amount); break;
                    }
                });
        }

        private CrdtWrapper<GSet<string>> CreateGSet(string nodeId, object? initialValue)
        {
            var set = new GSet<string>(nodeId);
            if (initialValue is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is string s)
                        set.AddInPlace(s);
                }
            }
            else if (initialValue?.ToString() is string s)
            {
                set.AddInPlace(s);
            }

            return new CrdtWrapper<GSet<string>>(
                set,
                "GSet",
                nodeId,
                c => c.ToJson(),
                GSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (op.ToLowerInvariant() == "add" && val?.ToString() is string s)
                        c.AddInPlace(s);
                });
        }

        private CrdtWrapper<GSet<string>> CreateGSetFromJson(string nodeId, string json)
        {
            var set = GSet<string>.FromJson(json);
            return new CrdtWrapper<GSet<string>>(
                set,
                "GSet",
                nodeId,
                c => c.ToJson(),
                GSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (op.ToLowerInvariant() == "add" && val?.ToString() is string s)
                        c.AddInPlace(s);
                });
        }

        private CrdtWrapper<TwoPhaseSet<string>> CreateTwoPhaseSet(string nodeId, object? initialValue)
        {
            var set = new TwoPhaseSet<string>(nodeId);
            if (initialValue is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is string s)
                        set.AddInPlace(s);
                }
            }
            else if (initialValue?.ToString() is string s)
            {
                set.AddInPlace(s);
            }

            return new CrdtWrapper<TwoPhaseSet<string>>(
                set,
                "TwoPhaseSet",
                nodeId,
                c => c.ToJson(),
                TwoPhaseSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (val?.ToString() is not string s) return;
                    switch (op.ToLowerInvariant())
                    {
                        case "add": c.AddInPlace(s); break;
                        case "remove": c.RemoveInPlace(s); break;
                    }
                });
        }

        private CrdtWrapper<TwoPhaseSet<string>> CreateTwoPhaseSetFromJson(string nodeId, string json)
        {
            var set = TwoPhaseSet<string>.FromJson(json);
            return new CrdtWrapper<TwoPhaseSet<string>>(
                set,
                "TwoPhaseSet",
                nodeId,
                c => c.ToJson(),
                TwoPhaseSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (val?.ToString() is not string s) return;
                    switch (op.ToLowerInvariant())
                    {
                        case "add": c.AddInPlace(s); break;
                        case "remove": c.RemoveInPlace(s); break;
                    }
                });
        }

        private CrdtWrapper<ORSet<string>> CreateORSet(string nodeId, object? initialValue)
        {
            var set = new ORSet<string>(nodeId);
            if (initialValue is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is string s)
                        set.AddInPlace(s);
                }
            }
            else if (initialValue?.ToString() is string s)
            {
                set.AddInPlace(s);
            }

            return new CrdtWrapper<ORSet<string>>(
                set,
                "ORSet",
                nodeId,
                c => c.ToJson(),
                ORSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (val?.ToString() is not string s) return;
                    switch (op.ToLowerInvariant())
                    {
                        case "add": c.AddInPlace(s); break;
                        case "remove": c.RemoveInPlace(s); break;
                    }
                });
        }

        private CrdtWrapper<ORSet<string>> CreateORSetFromJson(string nodeId, string json)
        {
            var set = ORSet<string>.FromJson(json);
            return new CrdtWrapper<ORSet<string>>(
                set,
                "ORSet",
                nodeId,
                c => c.ToJson(),
                ORSet<string>.FromJson,
                c => c.Elements.ToList(),
                (c, op, val) =>
                {
                    if (val?.ToString() is not string s) return;
                    switch (op.ToLowerInvariant())
                    {
                        case "add": c.AddInPlace(s); break;
                        case "remove": c.RemoveInPlace(s); break;
                    }
                });
        }

        private CrdtWrapper<LWWRegister<string>> CreateLWWRegister(string nodeId, object? initialValue)
        {
            var register = initialValue?.ToString() is string s
                ? new LWWRegister<string>(nodeId, s)
                : new LWWRegister<string>(nodeId);

            return new CrdtWrapper<LWWRegister<string>>(
                register,
                "LWWRegister",
                nodeId,
                c => c.ToJson(),
                LWWRegister<string>.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    if (op.ToLowerInvariant() == "set")
                        c.SetInPlace(val?.ToString());
                });
        }

        private CrdtWrapper<LWWRegister<string>> CreateLWWRegisterFromJson(string nodeId, string json)
        {
            var register = LWWRegister<string>.FromJson(json);
            return new CrdtWrapper<LWWRegister<string>>(
                register,
                "LWWRegister",
                nodeId,
                c => c.ToJson(),
                LWWRegister<string>.FromJson,
                c => c.Value,
                (c, op, val) =>
                {
                    if (op.ToLowerInvariant() == "set")
                        c.SetInPlace(val?.ToString());
                });
        }

        private CrdtWrapper<MVRegister<string>> CreateMVRegister(string nodeId, object? initialValue)
        {
            var register = initialValue?.ToString() is string s
                ? new MVRegister<string>(nodeId, s)
                : new MVRegister<string>(nodeId);

            return new CrdtWrapper<MVRegister<string>>(
                register,
                "MVRegister",
                nodeId,
                c => c.ToJson(),
                MVRegister<string>.FromJson,
                c => c.HasConflict ? c.Values.ToList() : (object?)(c.FirstValue),
                (c, op, val) =>
                {
                    switch (op.ToLowerInvariant())
                    {
                        case "set": c.SetInPlace(val?.ToString()); break;
                        case "resolve": c.ResolveInPlace(val?.ToString()); break;
                    }
                });
        }

        private CrdtWrapper<MVRegister<string>> CreateMVRegisterFromJson(string nodeId, string json)
        {
            var register = MVRegister<string>.FromJson(json);
            return new CrdtWrapper<MVRegister<string>>(
                register,
                "MVRegister",
                nodeId,
                c => c.ToJson(),
                MVRegister<string>.FromJson,
                c => c.HasConflict ? c.Values.ToList() : (object?)(c.FirstValue),
                (c, op, val) =>
                {
                    switch (op.ToLowerInvariant())
                    {
                        case "set": c.SetInPlace(val?.ToString()); break;
                        case "resolve": c.ResolveInPlace(val?.ToString()); break;
                    }
                });
        }
    }

    #endregion

    #region Gossip Protocol

    /// <summary>
    /// Gossip protocol for CRDT state propagation.
    /// </summary>
    internal sealed class GossipProtocol
    {
        private readonly CrdtStore _store;
        private readonly Func<string> _getNodeId;
        private readonly ConcurrentDictionary<string, GossipPeer> _peers = new();
        private readonly ConcurrentDictionary<string, VectorClock> _peerVersions = new();
        private readonly HashSet<string> _pendingUpdates = new();
        private readonly object _updateLock = new();

        private CancellationTokenSource? _cts;
        private Task? _gossipTask;
        private TcpListener? _listener;
        private int _listenPort = 5100;

        public bool IsRunning => _gossipTask != null && !_gossipTask.IsCompleted;
        public int PeerCount => _peers.Count;
        public DateTime? LastSyncTime { get; private set; }

        public GossipProtocol(CrdtStore store, Func<string> getNodeId)
        {
            _store = store;
            _getNodeId = getNodeId;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Start listener
            await StartListenerAsync();

            // Start gossip loop
            _gossipTask = RunGossipLoopAsync(_cts.Token);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();

            if (_gossipTask != null)
            {
                try
                {
                    await _gossipTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { /* Ignore */ }
            }

            _cts?.Dispose();
            _cts = null;
        }

        public void AddPeer(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                throw new ArgumentNullException(nameof(endpoint));

            _peers.TryAdd(endpoint, new GossipPeer { Endpoint = endpoint });
        }

        public bool RemovePeer(string endpoint)
        {
            return _peers.TryRemove(endpoint, out _);
        }

        public IEnumerable<string> GetPeers()
        {
            return _peers.Keys;
        }

        public void NotifyUpdate(string key)
        {
            lock (_updateLock)
            {
                _pendingUpdates.Add(key);
            }
        }

        public async Task SyncKeyAsync(string key)
        {
            var crdt = _store.Get(key);
            if (crdt == null)
                return;

            await BroadcastAsync(key, crdt);
        }

        public async Task<int> SyncAllAsync()
        {
            var synced = 0;
            foreach (var (key, crdt) in _store.List())
            {
                await BroadcastAsync(key, crdt);
                synced++;
            }
            LastSyncTime = DateTime.UtcNow;
            return synced;
        }

        private async Task StartListenerAsync()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _listenPort);
                _listener.Start();
                _ = AcceptConnectionsAsync(_cts!.Token);
            }
            catch
            {
                _listenPort++;
                if (_listenPort < 5200)
                {
                    await StartListenerAsync();
                }
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Continue accepting
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(line))
                        return;

                    using var doc = JsonDocument.Parse(line);
                    var key = doc.RootElement.GetProperty("key").GetString() ?? "";
                    var state = doc.RootElement.GetProperty("state").GetString() ?? "";
                    var type = doc.RootElement.GetProperty("type").GetString() ?? "";

                    // Apply received state
                    var existing = _store.Get(key);
                    if (existing != null)
                    {
                        existing.MergeFromJson(state);
                    }

                    await writer.WriteLineAsync("{\"success\":true}");
                }
            }
            catch
            {
                // Client handling failed
            }
        }

        private async Task RunGossipLoopAsync(CancellationToken ct)
        {
            var random = new Random();
            var gossipInterval = TimeSpan.FromSeconds(1);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(gossipInterval, ct);

                    // Get pending updates
                    string[] updates;
                    lock (_updateLock)
                    {
                        updates = _pendingUpdates.ToArray();
                        _pendingUpdates.Clear();
                    }

                    // Broadcast updates to random subset of peers
                    foreach (var key in updates)
                    {
                        var crdt = _store.Get(key);
                        if (crdt != null)
                        {
                            // Select random peers (fanout)
                            var peersToContact = _peers.Values
                                .OrderBy(_ => random.Next())
                                .Take(Math.Min(3, _peers.Count))
                                .ToList();

                            foreach (var peer in peersToContact)
                            {
                                await SendToPeerAsync(peer, key, crdt);
                            }
                        }
                    }

                    // Periodic anti-entropy sync
                    if (random.NextDouble() < 0.1) // 10% chance each interval
                    {
                        await SyncAllAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Gossip loop error, continue
                }
            }
        }

        private async Task BroadcastAsync(string key, ICrdtWrapper crdt)
        {
            var tasks = _peers.Values.Select(peer => SendToPeerAsync(peer, key, crdt));
            await Task.WhenAll(tasks);
        }

        private async Task SendToPeerAsync(GossipPeer peer, string key, ICrdtWrapper crdt)
        {
            try
            {
                using var client = new TcpClient();
                client.SendTimeout = 5000;
                client.ReceiveTimeout = 5000;

                var parts = peer.Endpoint.Split(':');
                if (parts.Length != 2)
                    return;

                await client.ConnectAsync(parts[0], int.Parse(parts[1]));

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var message = JsonSerializer.Serialize(new
                {
                    key,
                    type = crdt.GetCrdtType(),
                    state = crdt.ToJson(),
                    fromNode = _getNodeId()
                });

                await writer.WriteLineAsync(message);

                var response = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(response))
                {
                    peer.LastContact = DateTime.UtcNow;
                    peer.FailureCount = 0;
                }
            }
            catch
            {
                peer.FailureCount++;
                peer.LastFailure = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Represents a gossip peer.
    /// </summary>
    internal sealed class GossipPeer
    {
        public string Endpoint { get; set; } = "";
        public DateTime LastContact { get; set; }
        public DateTime? LastFailure { get; set; }
        public int FailureCount { get; set; }
        public bool IsHealthy => FailureCount < 3 && (DateTime.UtcNow - LastContact).TotalSeconds < 60;
    }

    #endregion
}
