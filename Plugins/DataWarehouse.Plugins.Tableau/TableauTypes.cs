using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Tableau
{
    #region Data Models

    /// <summary>
    /// Represents a Tableau data extract schema.
    /// </summary>
    public sealed class TableauExtractSchema
    {
        /// <summary>
        /// Gets or sets the extract name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the table definition.
        /// </summary>
        public TableauTableDefinition Table { get; set; } = new();

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a Tableau table definition.
    /// </summary>
    public sealed class TableauTableDefinition
    {
        /// <summary>
        /// Gets or sets the table name.
        /// </summary>
        public string Name { get; set; } = "Extract";

        /// <summary>
        /// Gets or sets the columns.
        /// </summary>
        public List<TableauColumnDefinition> Columns { get; set; } = new();

        /// <summary>
        /// Validates the table definition.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException("Table name is required.");
            }

            if (Columns.Count == 0)
            {
                throw new InvalidOperationException("At least one column is required.");
            }

            var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in Columns)
            {
                column.Validate();
                if (!columnNames.Add(column.Name))
                {
                    throw new InvalidOperationException($"Duplicate column name: {column.Name}");
                }
            }
        }
    }

    /// <summary>
    /// Represents a Tableau column definition.
    /// </summary>
    public sealed class TableauColumnDefinition
    {
        /// <summary>
        /// Gets or sets the column name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the column data type.
        /// </summary>
        public TableauDataType DataType { get; set; } = TableauDataType.String;

        /// <summary>
        /// Gets or sets whether the column is nullable.
        /// </summary>
        public bool IsNullable { get; set; } = true;

        /// <summary>
        /// Validates the column definition.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException("Column name is required.");
            }

            if (!IsValidColumnName(Name))
            {
                throw new InvalidOperationException($"Invalid column name: {Name}. Must contain only alphanumeric characters and underscores.");
            }
        }

        private static bool IsValidColumnName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var first = name[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Tableau data types supported by Hyper API.
    /// </summary>
    public enum TableauDataType
    {
        /// <summary>Boolean type.</summary>
        Boolean,
        /// <summary>16-bit integer.</summary>
        SmallInt,
        /// <summary>32-bit integer.</summary>
        Integer,
        /// <summary>64-bit integer.</summary>
        BigInt,
        /// <summary>32-bit floating point.</summary>
        Real,
        /// <summary>64-bit floating point.</summary>
        Double,
        /// <summary>Numeric with precision and scale.</summary>
        Numeric,
        /// <summary>Variable-length string.</summary>
        String,
        /// <summary>Date (no time component).</summary>
        Date,
        /// <summary>Time (no date component).</summary>
        Time,
        /// <summary>Timestamp with timezone.</summary>
        Timestamp,
        /// <summary>Interval (duration).</summary>
        Interval,
        /// <summary>Binary data.</summary>
        Bytes,
        /// <summary>Geographic point.</summary>
        Geography
    }

    /// <summary>
    /// Represents a row of data to be inserted into an extract.
    /// </summary>
    public sealed class TableauRow
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets a value by column name.
        /// </summary>
        public object? this[string columnName]
        {
            get => _values.TryGetValue(columnName, out var value) ? value : null;
            set => _values[columnName] = value;
        }

        /// <summary>
        /// Gets all column values.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Values => _values;

        /// <summary>
        /// Sets a column value.
        /// </summary>
        public void SetValue(string columnName, object? value)
        {
            _values[columnName] = value;
        }

        /// <summary>
        /// Gets a column value.
        /// </summary>
        public T? GetValue<T>(string columnName)
        {
            if (!_values.TryGetValue(columnName, out var value))
            {
                return default;
            }

            if (value is T typedValue)
            {
                return typedValue;
            }

            try
            {
                return (T?)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }
    }

    #endregion

    #region REST API Models

    /// <summary>
    /// Represents a Tableau authentication response.
    /// </summary>
    internal sealed class TableauAuthResponse
    {
        public string Token { get; set; } = string.Empty;
        public string SiteId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a Tableau datasource publish request.
    /// </summary>
    internal sealed class TableauPublishRequest
    {
        public string Name { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public bool Overwrite { get; set; } = true;
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Represents a Tableau datasource publish response.
    /// </summary>
    internal sealed class TableauPublishResponse
    {
        public string DatasourceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    #endregion

    #region Extract Management

    /// <summary>
    /// Manages Tableau extract metadata and lifecycle.
    /// </summary>
    internal sealed class ExtractManager
    {
        private readonly ConcurrentDictionary<string, ExtractMetadata> _extracts = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _tempDirectory;

        public ExtractManager(string tempDirectory)
        {
            _tempDirectory = tempDirectory;
        }

        /// <summary>
        /// Creates a new extract.
        /// </summary>
        public ExtractMetadata CreateExtract(string name, TableauTableDefinition table)
        {
            var extractId = Guid.NewGuid().ToString("N");
            var fileName = $"{name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{extractId}.hyper";
            var filePath = Path.Combine(_tempDirectory, fileName);

            var metadata = new ExtractMetadata
            {
                Id = extractId,
                Name = name,
                FilePath = filePath,
                Table = table,
                CreatedAt = DateTime.UtcNow,
                Status = ExtractStatus.Creating
            };

            _extracts[extractId] = metadata;
            return metadata;
        }

        /// <summary>
        /// Gets extract metadata by ID.
        /// </summary>
        public ExtractMetadata? GetExtract(string extractId)
        {
            return _extracts.TryGetValue(extractId, out var metadata) ? metadata : null;
        }

        /// <summary>
        /// Updates extract status.
        /// </summary>
        public void UpdateStatus(string extractId, ExtractStatus status)
        {
            if (_extracts.TryGetValue(extractId, out var metadata))
            {
                metadata.Status = status;
                metadata.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Deletes an extract.
        /// </summary>
        public bool DeleteExtract(string extractId)
        {
            if (_extracts.TryRemove(extractId, out var metadata))
            {
                try
                {
                    if (File.Exists(metadata.FilePath))
                    {
                        File.Delete(metadata.FilePath);
                    }
                    return true;
                }
                catch
                {
                    // Ignore deletion errors
                }
            }
            return false;
        }

        /// <summary>
        /// Gets all extracts.
        /// </summary>
        public IEnumerable<ExtractMetadata> GetAllExtracts()
        {
            return _extracts.Values.ToList();
        }
    }

    /// <summary>
    /// Metadata for a Tableau extract.
    /// </summary>
    internal sealed class ExtractMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public TableauTableDefinition Table { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public ExtractStatus Status { get; set; }
        public long RowCount { get; set; }
        public long FileSizeBytes { get; set; }
        public string? DatasourceId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Extract status enumeration.
    /// </summary>
    internal enum ExtractStatus
    {
        Creating,
        Ready,
        Publishing,
        Published,
        Failed
    }

    #endregion

    #region Hyper API Simulation

    /// <summary>
    /// Simulates Tableau Hyper API for creating data extracts.
    /// In production, this would use the actual Tableau Hyper API SDK.
    /// </summary>
    internal sealed class HyperApiSimulator
    {
        /// <summary>
        /// Creates a Hyper extract file.
        /// </summary>
        public async Task CreateExtractAsync(
            string filePath,
            TableauTableDefinition table,
            IEnumerable<TableauRow> rows,
            CancellationToken ct = default)
        {
            // Simulate Hyper API extract creation
            // In production, this would use Tableau.HyperAPI NuGet package
            await using var stream = File.Create(filePath);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);

            // Write simulated Hyper format (simplified)
            await writer.WriteLineAsync("# Tableau Hyper Extract (Simulated)");
            await writer.WriteLineAsync($"# Created: {DateTime.UtcNow:O}");
            await writer.WriteLineAsync($"# Table: {table.Name}");
            await writer.WriteLineAsync();

            // Write schema
            await writer.WriteLineAsync("# Schema:");
            foreach (var column in table.Columns)
            {
                await writer.WriteLineAsync($"# {column.Name}: {column.DataType} (nullable={column.IsNullable})");
            }
            await writer.WriteLineAsync();

            // Write data in CSV format (for simulation)
            var columnNames = table.Columns.Select(c => c.Name).ToList();
            await writer.WriteLineAsync(string.Join(",", columnNames));

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var values = columnNames.Select(name =>
                {
                    var value = row[name];
                    if (value == null)
                    {
                        return "NULL";
                    }
                    var str = value.ToString() ?? string.Empty;
                    // Escape commas and quotes
                    if (str.Contains(',') || str.Contains('"'))
                    {
                        return $"\"{str.Replace("\"", "\"\"")}\"";
                    }
                    return str;
                });

                await writer.WriteLineAsync(string.Join(",", values));
            }

            await writer.FlushAsync(ct);
        }

        /// <summary>
        /// Validates an extract file.
        /// </summary>
        public bool ValidateExtract(string filePath)
        {
            return File.Exists(filePath) && new FileInfo(filePath).Length > 0;
        }
    }

    #endregion

    #region REST API Client

    /// <summary>
    /// Tableau REST API client for publishing datasources.
    /// </summary>
    internal sealed class TableauRestClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly TableauConfiguration _config;
        private string? _authToken;
        private string? _siteId;

        public TableauRestClient(TableauConfiguration config)
        {
            _config = config;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = config.VerifySslCertificate
                    ? null
                    : (_, _, _, _) => true
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(config.TableauServerUrl),
                Timeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds)
            };

            if (config.EnableCompression)
            {
                _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new("gzip"));
                _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new("deflate"));
            }
        }

        /// <summary>
        /// Authenticates with Tableau Server.
        /// </summary>
        public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
        {
            try
            {
                var authPayload = new
                {
                    credentials = new
                    {
                        personalAccessTokenName = _config.TokenName,
                        personalAccessTokenSecret = _config.TokenSecret,
                        site = new
                        {
                            contentUrl = _config.SiteId
                        }
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(authPayload),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"/api/{_config.ApiVersion}/auth/signin",
                    content,
                    ct);

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                var authResponse = JsonSerializer.Deserialize<TableauAuthResponse>(responseBody);

                if (authResponse == null)
                {
                    return false;
                }

                _authToken = authResponse.Token;
                _siteId = authResponse.SiteId;

                _httpClient.DefaultRequestHeaders.Add("X-Tableau-Auth", _authToken);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Publishes a datasource to Tableau Server.
        /// </summary>
        public async Task<string?> PublishDatasourceAsync(
            string filePath,
            string name,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_authToken) || string.IsNullOrEmpty(_siteId))
            {
                throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Extract file not found.", filePath);
            }

            try
            {
                using var formData = new MultipartFormDataContent();

                // Add file
                var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, ct));
                fileContent.Headers.ContentType = new("application/octet-stream");
                formData.Add(fileContent, "tableau_datasource", Path.GetFileName(filePath));

                // Add metadata
                var requestPayload = new TableauPublishRequest
                {
                    Name = name,
                    ProjectId = _config.ProjectId,
                    Overwrite = true,
                    Tags = _config.CustomTags
                };

                var requestJson = JsonSerializer.Serialize(new { datasource = requestPayload });
                formData.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request_payload");

                var response = await _httpClient.PostAsync(
                    $"/api/{_config.ApiVersion}/sites/{_siteId}/datasources",
                    formData,
                    ct);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Failed to publish datasource: {response.StatusCode} - {error}");
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                var publishResponse = JsonSerializer.Deserialize<TableauPublishResponse>(responseBody);

                return publishResponse?.DatasourceId;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to publish datasource: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    #endregion
}
