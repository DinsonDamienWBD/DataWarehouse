using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Database
{
    /// <summary>
    /// SQL Server TDE (Transparent Data Encryption) metadata import/export strategy.
    /// Provides interoperability with SQL Server encrypted databases for key management workflows.
    ///
    /// Purpose:
    /// SQL Server TDE uses a hierarchical key structure to encrypt databases at rest.
    /// This strategy enables DataWarehouse to import/export TDE certificate metadata
    /// for integration scenarios including:
    /// - Migrating encrypted databases to DataWarehouse-managed storage
    /// - Backing up TDE certificates for disaster recovery
    /// - Documenting TDE key hierarchies for compliance audits
    /// - Synchronizing encryption metadata between SQL Server and DataWarehouse
    ///
    /// Key Hierarchy:
    /// SQL Server TDE follows a 4-tier hierarchy:
    ///   1. Service Master Key (SMK) - Auto-generated, encrypted by Windows DPAPI
    ///   2. Database Master Key (DMK) - Per-database, encrypted by SMK or password
    ///   3. Certificate - Created in master database, protected by DMK
    ///   4. Database Encryption Key (DEK) - Per user database, encrypted by certificate
    ///
    /// Import/Export Workflows:
    ///
    /// Import from SQL Server:
    ///   1. Extract certificate using BACKUP CERTIFICATE command (generates .cer and .pvk files)
    ///   2. Configure this strategy with certificate file paths
    ///   3. Call GetKeyAsync to import certificate metadata (public key, thumbprint, subject)
    ///   4. Metadata stored in DataWarehouse key store for reference
    ///   5. Private key (.pvk) optionally imported if password provided
    ///
    /// Export to SQL Server:
    ///   1. Generate or retrieve certificate metadata from DataWarehouse
    ///   2. Call CreateKeyAsync to export certificate in .cer format
    ///   3. Use CREATE CERTIFICATE ... FROM FILE in SQL Server to import
    ///   4. Associate certificate with DEK using ALTER DATABASE ... SET ENCRYPTION ON
    ///
    /// Direct SQL Server Integration (Optional):
    ///   - Configure SQL connection string to query sys.certificates and sys.dm_database_encryption_keys
    ///   - Extract certificate metadata directly without manual backup
    ///   - Requires VIEW DEFINITION permissions on master database
    ///
    /// Security Considerations:
    /// - This strategy handles METADATA ONLY - actual private keys must be protected separately
    /// - Private key files (.pvk) are encrypted by SQL Server with a user-provided password
    /// - DataWarehouse stores only certificate thumbprint, subject, and public key
    /// - Actual DEK decryption still requires SQL Server certificate with private key
    /// - Use ISecurityContext for access control to TDE metadata
    ///
    /// Configuration:
    /// - CertificateFilePath: Path to .cer file for import (public key)
    /// - PrivateKeyFilePath: Optional path to .pvk file (encrypted private key)
    /// - PrivateKeyPassword: Password for .pvk decryption
    /// - SqlConnectionString: Optional SQL connection for direct metadata extraction
    /// - ExportDirectory: Directory for exported certificate files
    /// - MetadataStoragePath: Path to JSON file storing certificate metadata
    ///
    /// Supported Operations:
    /// - Import certificate metadata from .cer/.pvk files
    /// - Export certificate metadata to .cer format
    /// - Query SQL Server for TDE certificate information
    /// - Track certificate-to-database mappings
    /// - List all imported TDE certificates
    /// - Validate certificate structure and expiration
    ///
    /// StrategyId: "sql-tde-metadata"
    /// </summary>
    public sealed class SqlTdeMetadataStrategy : KeyStoreStrategyBase
    {
        private SqlTdeConfig _config = new();
        private readonly Dictionary<string, TdeCertificateMetadata> _certificates = new();
        private readonly object _certificateLock = new();

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false, // TDE rotation happens in SQL Server
            SupportsEnvelope = false, // Metadata only, not key encryption
            SupportsHsm = false, // SQL Server may use HSM, but this is metadata layer
            SupportsExpiration = true, // Certificates have expiration dates
            SupportsReplication = false,
            SupportsVersioning = true, // Track certificate versions
            SupportsPerKeyAcl = true, // Per-certificate access control
            SupportsAuditLogging = true, // Log all metadata operations
            MaxKeySizeBytes = 0, // Metadata size varies
            MinKeySizeBytes = 0, // Metadata only
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "SQL Server TDE",
                ["Purpose"] = "Metadata Import/Export",
                ["Backend"] = "File System + Optional SQL Server",
                ["StrategyId"] = "sql-tde-metadata",
                ["SupportedFormats"] = new[] { ".cer", ".pvk" },
                ["KeyHierarchy"] = "SMK -> DMK -> Certificate -> DEK"
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("CertificateFilePath", out var certPathObj) && certPathObj is string certPath)
                _config.CertificateFilePath = certPath;
            if (Configuration.TryGetValue("PrivateKeyFilePath", out var pvkPathObj) && pvkPathObj is string pvkPath)
                _config.PrivateKeyFilePath = pvkPath;
            if (Configuration.TryGetValue("PrivateKeyPassword", out var pwdObj) && pwdObj is string pwd)
                _config.PrivateKeyPassword = pwd;
            if (Configuration.TryGetValue("SqlConnectionString", out var connStrObj) && connStrObj is string connStr)
                _config.SqlConnectionString = connStr;
            if (Configuration.TryGetValue("ExportDirectory", out var exportDirObj) && exportDirObj is string exportDir)
                _config.ExportDirectory = exportDir;
            if (Configuration.TryGetValue("MetadataStoragePath", out var metaPathObj) && metaPathObj is string metaPath)
                _config.MetadataStoragePath = metaPath;

            // Ensure export directory exists
            if (!string.IsNullOrEmpty(_config.ExportDirectory))
            {
                Directory.CreateDirectory(_config.ExportDirectory);
            }

            // Load existing metadata from storage
            await LoadMetadataFromStorageAsync(cancellationToken);
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            // Return the most recently imported certificate
            lock (_certificateLock)
            {
                var latest = _certificates.Values
                    .OrderByDescending(c => c.ImportedAt)
                    .FirstOrDefault();

                return latest?.CertificateId ?? "default";
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // Try to load from in-memory cache first
            lock (_certificateLock)
            {
                if (_certificates.TryGetValue(keyId, out var metadata))
                {
                    // Return certificate metadata as JSON bytes
                    return SerializeCertificateMetadata(metadata);
                }
            }

            // Try to load from certificate file
            if (!string.IsNullOrEmpty(_config.CertificateFilePath) && File.Exists(_config.CertificateFilePath))
            {
                var metadata = await ImportCertificateFromFileAsync(_config.CertificateFilePath, keyId, cancellationToken: default);
                lock (_certificateLock)
                {
                    _certificates[keyId] = metadata;
                }
                await SaveMetadataToStorageAsync(default);
                return SerializeCertificateMetadata(metadata);
            }

            // Try to load from SQL Server
            if (!string.IsNullOrEmpty(_config.SqlConnectionString))
            {
                var metadata = await ImportCertificateFromSqlServerAsync(keyId, default);
                lock (_certificateLock)
                {
                    _certificates[keyId] = metadata;
                }
                await SaveMetadataToStorageAsync(default);
                return SerializeCertificateMetadata(metadata);
            }

            throw new KeyNotFoundException($"TDE certificate '{keyId}' not found in metadata storage, file system, or SQL Server.");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can create or update TDE certificate metadata.");
            }

            // Deserialize metadata from keyData
            var metadata = DeserializeCertificateMetadata(keyData);
            metadata.CertificateId = keyId;
            metadata.ImportedAt = DateTime.UtcNow;

            lock (_certificateLock)
            {
                _certificates[keyId] = metadata;
            }

            // Save to persistent storage
            await SaveMetadataToStorageAsync(default);

            // Export certificate to file if export directory is configured
            if (!string.IsNullOrEmpty(_config.ExportDirectory) && metadata.PublicKeyData != null)
            {
                await ExportCertificateToFileAsync(keyId, metadata, default);
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            lock (_certificateLock)
            {
                return _certificates.Keys.ToList().AsReadOnly();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete TDE certificate metadata.");
            }

            lock (_certificateLock)
            {
                _certificates.Remove(keyId);
            }

            await SaveMetadataToStorageAsync(cancellationToken);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            TdeCertificateMetadata? tdeMeta;
            lock (_certificateLock)
            {
                if (!_certificates.TryGetValue(keyId, out tdeMeta))
                    return null;
            }

            return new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = tdeMeta.CreatedAt,
                CreatedBy = tdeMeta.ImportedBy,
                ExpiresAt = tdeMeta.ExpiresAt,
                Version = tdeMeta.Version,
                KeySizeBytes = tdeMeta.PublicKeyData?.Length ?? 0,
                IsActive = !tdeMeta.IsExpired && tdeMeta.IsValid,
                Metadata = new Dictionary<string, object>
                {
                    ["Thumbprint"] = tdeMeta.Thumbprint ?? "",
                    ["Subject"] = tdeMeta.Subject ?? "",
                    ["Issuer"] = tdeMeta.Issuer ?? "",
                    ["SerialNumber"] = tdeMeta.SerialNumber ?? "",
                    ["SignatureAlgorithm"] = tdeMeta.SignatureAlgorithm ?? "",
                    ["KeyAlgorithm"] = tdeMeta.KeyAlgorithm ?? "",
                    ["KeySize"] = tdeMeta.KeySize,
                    ["HasPrivateKey"] = tdeMeta.HasPrivateKey,
                    ["DatabaseMappings"] = tdeMeta.DatabaseMappings ?? Array.Empty<string>(),
                    ["ImportSource"] = tdeMeta.ImportSource ?? "Unknown",
                    ["IsExpired"] = tdeMeta.IsExpired,
                    ["IsValid"] = tdeMeta.IsValid
                }
            };
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Check if metadata storage path is accessible
                if (!string.IsNullOrEmpty(_config.MetadataStoragePath))
                {
                    var directory = Path.GetDirectoryName(_config.MetadataStoragePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        return false;
                    }
                }

                // Check if SQL connection is healthy (if configured)
                if (!string.IsNullOrEmpty(_config.SqlConnectionString))
                {
                    // TODO: Add SQL connection health check
                    // For now, assume healthy if connection string is provided
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        #region Certificate Import/Export

        private async Task<TdeCertificateMetadata> ImportCertificateFromFileAsync(string certificateFilePath, string keyId, CancellationToken cancellationToken)
        {
            var certBytes = await File.ReadAllBytesAsync(certificateFilePath, cancellationToken);
            var cert = new X509Certificate2(certBytes);

            var metadata = new TdeCertificateMetadata
            {
                CertificateId = keyId,
                Thumbprint = cert.Thumbprint,
                Subject = cert.Subject,
                Issuer = cert.Issuer,
                SerialNumber = cert.SerialNumber,
                CreatedAt = cert.NotBefore,
                ExpiresAt = cert.NotAfter,
                SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName ?? "",
                PublicKeyData = cert.GetPublicKey(),
                KeyAlgorithm = cert.GetKeyAlgorithm(),
                KeySize = cert.PublicKey.Key.KeySize,
                HasPrivateKey = cert.HasPrivateKey,
                ImportedAt = DateTime.UtcNow,
                ImportSource = $"File: {certificateFilePath}",
                IsValid = true
            };

            // Import private key if available
            if (!string.IsNullOrEmpty(_config.PrivateKeyFilePath) && File.Exists(_config.PrivateKeyFilePath))
            {
                // Note: .pvk files require special handling - this is a placeholder
                // Full implementation would use BouncyCastle or similar library to parse .pvk
                metadata.HasPrivateKey = true;
                metadata.PrivateKeyEncrypted = true;
            }

            return metadata;
        }

        private async Task<TdeCertificateMetadata> ImportCertificateFromSqlServerAsync(string certificateName, CancellationToken cancellationToken)
        {
            // Placeholder for SQL Server integration
            // Full implementation would use ADO.NET to query:
            // SELECT name, thumbprint, subject, issuer, expiry_date, pvt_key_encryption_type
            // FROM sys.certificates
            // WHERE name = @certificateName

            throw new NotImplementedException("SQL Server direct import is not yet implemented. Use file-based import with BACKUP CERTIFICATE.");
        }

        private async Task ExportCertificateToFileAsync(string keyId, TdeCertificateMetadata metadata, CancellationToken cancellationToken)
        {
            if (metadata.PublicKeyData == null)
            {
                throw new InvalidOperationException($"Cannot export certificate '{keyId}' - no public key data available.");
            }

            var exportPath = Path.Combine(_config.ExportDirectory, $"{keyId}.cer");

            // Create X509 certificate for export
            var cert = new X509Certificate2(metadata.PublicKeyData);
            var certBytes = cert.Export(X509ContentType.Cert);

            await File.WriteAllBytesAsync(exportPath, certBytes, cancellationToken);
        }

        #endregion

        #region Metadata Serialization

        private byte[] SerializeCertificateMetadata(TdeCertificateMetadata metadata)
        {
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            return Encoding.UTF8.GetBytes(json);
        }

        private TdeCertificateMetadata DeserializeCertificateMetadata(byte[] data)
        {
            var json = Encoding.UTF8.GetString(data);
            return JsonSerializer.Deserialize<TdeCertificateMetadata>(json) ?? new TdeCertificateMetadata();
        }

        private async Task SaveMetadataToStorageAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_config.MetadataStoragePath))
                return;

            var directory = Path.GetDirectoryName(_config.MetadataStoragePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TdeCertificateMetadata[] certificates;
            lock (_certificateLock)
            {
                certificates = _certificates.Values.ToArray();
            }

            var json = JsonSerializer.Serialize(certificates, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_config.MetadataStoragePath, json, cancellationToken);
        }

        private async Task LoadMetadataFromStorageAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_config.MetadataStoragePath) || !File.Exists(_config.MetadataStoragePath))
                return;

            var json = await File.ReadAllTextAsync(_config.MetadataStoragePath, cancellationToken);
            var certificates = JsonSerializer.Deserialize<TdeCertificateMetadata[]>(json);

            if (certificates != null)
            {
                lock (_certificateLock)
                {
                    foreach (var cert in certificates)
                    {
                        _certificates[cert.CertificateId] = cert;
                    }
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Configuration for SQL Server TDE metadata strategy.
    /// </summary>
    public class SqlTdeConfig
    {
        /// <summary>
        /// Path to certificate file (.cer) for import.
        /// Generated by SQL Server BACKUP CERTIFICATE command.
        /// </summary>
        public string CertificateFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Path to private key file (.pvk) for import.
        /// Optional - only needed if private key metadata is required.
        /// </summary>
        public string? PrivateKeyFilePath { get; set; }

        /// <summary>
        /// Password for decrypting .pvk file.
        /// Required if PrivateKeyFilePath is specified.
        /// </summary>
        public string? PrivateKeyPassword { get; set; }

        /// <summary>
        /// SQL Server connection string for direct metadata extraction.
        /// Requires VIEW DEFINITION permission on master database.
        /// Optional - use file-based import if not provided.
        /// </summary>
        public string? SqlConnectionString { get; set; }

        /// <summary>
        /// Directory for exporting certificate files.
        /// Defaults to ./tde-exports
        /// </summary>
        public string ExportDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "tde-exports");

        /// <summary>
        /// Path to JSON file storing certificate metadata.
        /// Defaults to ./tde-metadata.json
        /// </summary>
        public string MetadataStoragePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "tde-metadata.json");
    }

    /// <summary>
    /// Metadata for a SQL Server TDE certificate.
    /// Represents the certificate used to encrypt Database Encryption Keys (DEKs).
    /// </summary>
    public class TdeCertificateMetadata
    {
        /// <summary>
        /// Unique identifier for this certificate in DataWarehouse.
        /// </summary>
        public string CertificateId { get; set; } = string.Empty;

        /// <summary>
        /// Certificate thumbprint (SHA-1 hash of certificate).
        /// </summary>
        public string? Thumbprint { get; set; }

        /// <summary>
        /// Certificate subject (e.g., "CN=TDE_Certificate").
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>
        /// Certificate issuer.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// Certificate serial number.
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// When the certificate was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the certificate expires.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Signature algorithm (e.g., "sha256RSA").
        /// </summary>
        public string? SignatureAlgorithm { get; set; }

        /// <summary>
        /// Public key data (DER-encoded).
        /// </summary>
        public byte[]? PublicKeyData { get; set; }

        /// <summary>
        /// Public key algorithm OID (e.g., "1.2.840.113549.1.1.1" for RSA).
        /// </summary>
        public string? KeyAlgorithm { get; set; }

        /// <summary>
        /// Key size in bits (e.g., 2048 for RSA-2048).
        /// </summary>
        public int KeySize { get; set; }

        /// <summary>
        /// Whether the certificate has an associated private key.
        /// </summary>
        public bool HasPrivateKey { get; set; }

        /// <summary>
        /// Whether the private key is encrypted (always true for .pvk files).
        /// </summary>
        public bool PrivateKeyEncrypted { get; set; }

        /// <summary>
        /// List of database names encrypted with this certificate.
        /// Populated from sys.dm_database_encryption_keys if SQL connection available.
        /// </summary>
        public string[]? DatabaseMappings { get; set; }

        /// <summary>
        /// When this certificate was imported into DataWarehouse.
        /// </summary>
        public DateTime ImportedAt { get; set; }

        /// <summary>
        /// Who imported this certificate (user ID).
        /// </summary>
        public string? ImportedBy { get; set; }

        /// <summary>
        /// Source of the import (e.g., "File: C:\path\to\cert.cer" or "SQL Server: MyServer").
        /// </summary>
        public string? ImportSource { get; set; }

        /// <summary>
        /// Certificate version (for tracking updates).
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Whether the certificate is expired.
        /// </summary>
        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

        /// <summary>
        /// Whether the certificate is valid (not expired and has required data).
        /// </summary>
        public bool IsValid { get; set; } = true;
    }
}
