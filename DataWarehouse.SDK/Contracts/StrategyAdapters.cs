using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.IO;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Adapts an ICompressionStrategy (standalone algorithm) into an IDataTransformation
    /// (pipeline-compatible plugin). Allows the 50+ compression strategies to be registered
    /// as pipeline stages.
    /// </summary>
    public class CompressionStrategyAdapter : CompressionPluginBase
    {
        private readonly ICompressionStrategy _strategy;

        public CompressionStrategyAdapter(ICompressionStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public override string Id => $"adapter.compression.{_strategy.Characteristics.AlgorithmName.ToLowerInvariant()}";
        public override string Name => _strategy.Characteristics.AlgorithmName;
        public override string Version => "1.0.0";
        protected override string AlgorithmId => _strategy.Characteristics.AlgorithmName;
        protected override bool SupportsStreaming => _strategy.Characteristics.SupportsStreaming;
        protected override bool SupportsParallel => _strategy.Characteristics.SupportsParallelCompression;
        protected override double TypicalRatio => _strategy.Characteristics.TypicalCompressionRatio;

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            var compressed = _strategy.Compress(ms.ToArray());
            lock (StatsLock)
            {
                CompressionCount++;
                TotalBytesCompressed += ms.Length;
            }
            return new MemoryStream(compressed);
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var ms = new MemoryStream();
            stored.CopyTo(ms);
            var decompressed = _strategy.Decompress(ms.ToArray());
            lock (StatsLock)
            {
                DecompressionCount++;
                TotalBytesDecompressed += decompressed.Length;
            }
            return new MemoryStream(decompressed);
        }
    }

    /// <summary>
    /// Adapts an IEncryptionStrategy (standalone cipher) into an IDataTransformation
    /// (pipeline-compatible plugin). Allows the 70+ encryption strategies to be registered
    /// as pipeline stages.
    /// </summary>
    /// <remarks>
    /// The encryption key must be provided in the operation args dictionary under the
    /// "encryptionKey" key (byte[]), or set via <see cref="SetDefaultKey(byte[])"/>.
    /// For production use, prefer the full EncryptionPluginBase with key management.
    /// </remarks>
    public class EncryptionStrategyAdapter : EncryptionPluginBase
    {
        private readonly IEncryptionStrategy _strategy;
        private byte[]? _defaultKey;

        public EncryptionStrategyAdapter(IEncryptionStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public override string Id => $"adapter.encryption.{_strategy.CipherInfo.AlgorithmName.ToLowerInvariant().Replace(" ", "-")}";
        public override string Name => _strategy.CipherInfo.AlgorithmName;
        public override string Version => "1.0.0";
        protected override string AlgorithmId => _strategy.CipherInfo.AlgorithmName;
        protected override int KeySizeBytes => _strategy.CipherInfo.KeySizeBits / 8;
        protected override int IvSizeBytes => _strategy.CipherInfo.IvSizeBytes;
        protected override int TagSizeBytes => _strategy.CipherInfo.TagSizeBytes;

        /// <summary>
        /// Sets a default key to use when no key is provided in operation args.
        /// For testing or scenarios where key management is handled externally.
        /// </summary>
        public void SetDefaultKey(byte[] key)
        {
            _defaultKey = key ?? throw new ArgumentNullException(nameof(key));
        }

        /// <summary>
        /// Resolves the encryption key from operation arguments or the default key.
        /// </summary>
        private byte[] ResolveKey(Dictionary<string, object> args)
        {
            if (args.TryGetValue("encryptionKey", out var keyObj) && keyObj is byte[] key)
                return key;
            if (_defaultKey != null)
                return _defaultKey;
            throw new InvalidOperationException(
                "No encryption key available. Provide 'encryptionKey' in args or call SetDefaultKey().");
        }

        protected override async System.Threading.Tasks.Task<Stream> EncryptCoreAsync(
            Stream input, byte[] key, byte[] iv, IKernelContext context)
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            var encrypted = await _strategy.EncryptAsync(ms.ToArray(), key);
            return new MemoryStream(encrypted);
        }

        protected override async System.Threading.Tasks.Task<(Stream data, byte[]? tag)> DecryptCoreAsync(
            Stream input, byte[] key, byte[]? iv, IKernelContext context)
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            var decrypted = await _strategy.DecryptAsync(ms.ToArray(), key);
            return (new MemoryStream(decrypted), null);
        }
    }
}
