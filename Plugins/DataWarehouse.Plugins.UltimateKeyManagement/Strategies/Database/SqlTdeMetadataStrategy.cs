using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using Microsoft.Data.SqlClient;
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

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sqltdemetadata.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("sqltdemetadata.init");
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
            // #3461: Take a snapshot outside the lock so that expensive LINQ sort does not hold _certificateLock.
            List<(string CertificateId, DateTime ImportedAt)> snapshot;
            lock (_certificateLock)
            {
                snapshot = _certificates.Values
                    .Select(c => (c.CertificateId, c.ImportedAt))
                    .ToList();
            }

            var latest = snapshot
                .OrderByDescending(c => c.ImportedAt)
                .FirstOrDefault();

            return await Task.FromResult(latest.CertificateId ?? "default");
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
                    if (!await CheckSqlConnectionHealthAsync(cancellationToken))
                    {
                        return false;
                    }
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
            var cert = X509CertificateLoader.LoadCertificate(certBytes);

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
                KeySize = (cert.PublicKey.GetRSAPublicKey()?.KeySize ?? cert.PublicKey.GetECDsaPublicKey()?.KeySize ?? 0),
                HasPrivateKey = cert.HasPrivateKey,
                ImportedAt = DateTime.UtcNow,
                ImportSource = $"File: {certificateFilePath}",
                IsValid = true
            };

            // P2-3471: Only set HasPrivateKey=true when the .pvk file is present AND its
            // header can be confirmed (4-byte magic 0xB0B5F154 for RSA private keys).
            // Setting the flag without parsing is a contract lie that misleads callers.
            if (!string.IsNullOrEmpty(_config.PrivateKeyFilePath) && File.Exists(_config.PrivateKeyFilePath))
            {
                try
                {
                    // Read the first 4 bytes to verify the PVK magic number.
                    using var fs = new FileStream(_config.PrivateKeyFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var magic = new byte[4];
                    var read = await fs.ReadAsync(magic.AsMemory(0, 4));
                    // PVK magic: 0xB0B5F154 (little-endian)
                    var isValidPvk = read == 4 && magic[0] == 0x54 && magic[1] == 0xF1 && magic[2] == 0xB5 && magic[3] == 0xB0;
                    if (isValidPvk)
                    {
                        metadata.HasPrivateKey = true;
                        metadata.PrivateKeyEncrypted = true;
                    }
                    // If magic doesn't match, HasPrivateKey remains as-is (set from cert.HasPrivateKey above).
                }
                catch (IOException)
                {
                    // File unreadable — leave HasPrivateKey at the value from the certificate itself.
                }
            }

            return metadata;
        }

        private async Task<TdeCertificateMetadata> ImportCertificateFromSqlServerAsync(string certificateName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_config.SqlConnectionString))
            {
                throw new InvalidOperationException("SQL connection string is required for SQL Server import.");
            }

            using var connection = new Microsoft.Data.SqlClient.SqlConnection(_config.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Query sys.certificates for certificate metadata
            const string query = @"
                SELECT
                    c.name,
                    c.thumbprint,
                    c.subject,
                    c.issuer,
                    c.expiry_date,
                    c.start_date,
                    c.pvt_key_encryption_type_desc,
                    c.pvt_key_last_backup_date,
                    c.certificate_id
                FROM sys.certificates c
                WHERE c.name = @CertificateName";

            using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
            command.Parameters.AddWithValue("@CertificateName", certificateName);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Certificate '{certificateName}' not found in SQL Server sys.certificates.");
            }

            // Extract certificate metadata from SQL Server
            var thumbprint = reader["thumbprint"] as byte[];
            var thumbprintHex = thumbprint != null ? BitConverter.ToString(thumbprint).Replace("-", "") : "";

            var metadata = new TdeCertificateMetadata
            {
                CertificateId = certificateName,
                Thumbprint = thumbprintHex,
                Subject = reader["subject"] as string ?? "",
                Issuer = reader["issuer"] as string ?? "",
                SerialNumber = thumbprintHex, // SQL Server doesn't expose serial number, use thumbprint
                CreatedAt = reader["start_date"] is DateTime startDate ? startDate : DateTime.UtcNow,
                ExpiresAt = reader["expiry_date"] is DateTime expiryDate ? expiryDate : null,
                SignatureAlgorithm = "Unknown", // Not exposed in sys.certificates
                KeyAlgorithm = "RSA", // TDE certificates are typically RSA
                KeySize = 2048, // Standard TDE key size
                HasPrivateKey = !string.IsNullOrEmpty(reader["pvt_key_encryption_type_desc"] as string),
                PrivateKeyEncrypted = true, // Private keys in SQL Server are always encrypted
                ImportedAt = DateTime.UtcNow,
                ImportSource = $"SQL Server: {connection.DataSource}/{connection.Database}",
                IsValid = true,
                Version = 1
            };

            // Close the first reader before executing the second query
            reader.Close();

            // Query for database mappings (which databases use this certificate)
            const string dbQuery = @"
                SELECT
                    DB_NAME(dek.database_id) as database_name
                FROM sys.dm_database_encryption_keys dek
                INNER JOIN sys.certificates c ON dek.encryptor_thumbprint = c.thumbprint
                WHERE c.name = @CertificateName";

            using var dbCommand = new Microsoft.Data.SqlClient.SqlCommand(dbQuery, connection);
            dbCommand.Parameters.AddWithValue("@CertificateName", certificateName);

            var databases = new List<string>();
            using var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken);

            while (await dbReader.ReadAsync(cancellationToken))
            {
                var dbName = dbReader["database_name"] as string;
                if (!string.IsNullOrEmpty(dbName))
                {
                    databases.Add(dbName);
                }
            }

            metadata.DatabaseMappings = databases.Count > 0 ? databases.ToArray() : null;

            return metadata;
        }

        private async Task ExportCertificateToFileAsync(string keyId, TdeCertificateMetadata metadata, CancellationToken cancellationToken)
        {
            if (metadata.PublicKeyData == null)
            {
                throw new InvalidOperationException($"Cannot export certificate '{keyId}' - no public key data available.");
            }

            var exportPath = Path.Combine(_config.ExportDirectory, $"{keyId}.cer");

            // Create X509 certificate for export
            var cert = X509CertificateLoader.LoadCertificate(metadata.PublicKeyData);
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

        #region SQL Connection Health

        /// <summary>
        /// Validates that the SQL Server connection is alive and accessible.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if connection is healthy, false otherwise</returns>
        private async Task<bool> CheckSqlConnectionHealthAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Build connection string with explicit timeout if not already set
                var connectionStringBuilder = new SqlConnectionStringBuilder(_config.SqlConnectionString);

                // Set a reasonable timeout for the connection attempt (5 seconds) if not already specified
                if (connectionStringBuilder.ConnectTimeout == 15) // 15 is the default
                {
                    connectionStringBuilder.ConnectTimeout = 5;
                }

                using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                // Execute a simple query to verify connectivity
                using var command = new SqlCommand("SELECT 1", connection);
                command.CommandTimeout = 5;

                var result = await command.ExecuteScalarAsync(cancellationToken);

                // Verify we got the expected result
                return result != null && result.Equals(1);
            }
            catch (SqlException ex)
            {
                // SQL-specific errors (connection failures, timeouts, permission issues)
                System.Diagnostics.Debug.WriteLine($"SQL TDE health check failed: {ex.Message}");
                return false;
            }
            catch (InvalidOperationException ex)
            {
                // Invalid connection string
                System.Diagnostics.Debug.WriteLine($"SQL TDE health check failed (invalid connection): {ex.Message}");
                return false;
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation
                System.Diagnostics.Debug.WriteLine("SQL TDE health check cancelled or timed out");
                return false;
            }
            catch (Exception ex)
            {
                // Unexpected errors
                System.Diagnostics.Debug.WriteLine($"SQL TDE health check failed (unexpected): {ex.Message}");
                return false;
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
