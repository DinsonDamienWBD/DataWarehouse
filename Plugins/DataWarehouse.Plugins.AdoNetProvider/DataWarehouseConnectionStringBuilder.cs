using System.Collections;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Provides a simple way to create and manage the contents of connection strings
/// used by the DataWarehouse ADO.NET provider.
/// Thread-safe for reading; modifications should be synchronized by the caller.
/// </summary>
/// <remarks>
/// Supported connection string properties:
/// - Server: The server address (default: localhost)
/// - Port: The server port (default: 5432)
/// - Database: The database name (default: datawarehouse)
/// - User Id: The user name for authentication
/// - Password: The password for authentication
/// - Timeout: Connection timeout in seconds (default: 30)
/// - CommandTimeout: Default command timeout in seconds (default: 30)
/// - PoolSize: Maximum pool size (default: 100)
/// - MinPoolSize: Minimum pool size (default: 0)
/// - Pooling: Enable connection pooling (default: true)
/// - ApplicationName: Application name for identification
/// - SSL Mode: SSL mode (Disable, Prefer, Require)
/// </remarks>
public sealed class DataWarehouseConnectionStringBuilder : DbConnectionStringBuilder
{
    private const string DefaultServer = "localhost";
    private const int DefaultPort = 5432;
    private const string DefaultDatabase = "datawarehouse";
    private const int DefaultTimeout = 30;
    private const int DefaultCommandTimeout = 30;
    private const int DefaultPoolSize = 100;
    private const int DefaultMinPoolSize = 0;
    private const bool DefaultPooling = true;

    private static readonly Dictionary<string, string> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Server", "Server" },
        { "Host", "Server" },
        { "Data Source", "Server" },
        { "Port", "Port" },
        { "Database", "Database" },
        { "Initial Catalog", "Database" },
        { "User Id", "User Id" },
        { "Username", "User Id" },
        { "User", "User Id" },
        { "Password", "Password" },
        { "Pwd", "Password" },
        { "Timeout", "Timeout" },
        { "Connect Timeout", "Timeout" },
        { "Connection Timeout", "Timeout" },
        { "Command Timeout", "CommandTimeout" },
        { "CommandTimeout", "CommandTimeout" },
        { "Pool Size", "PoolSize" },
        { "Max Pool Size", "PoolSize" },
        { "PoolSize", "PoolSize" },
        { "Min Pool Size", "MinPoolSize" },
        { "MinPoolSize", "MinPoolSize" },
        { "Pooling", "Pooling" },
        { "Application Name", "ApplicationName" },
        { "ApplicationName", "ApplicationName" },
        { "SSL Mode", "SslMode" },
        { "SslMode", "SslMode" }
    };

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnectionStringBuilder class.
    /// </summary>
    public DataWarehouseConnectionStringBuilder()
    {
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnectionStringBuilder class
    /// using the supplied connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    /// <exception cref="ArgumentException">Thrown when the connection string is invalid.</exception>
    public DataWarehouseConnectionStringBuilder(string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            ConnectionString = connectionString;
        }
    }

    /// <summary>
    /// Gets or sets the server address.
    /// </summary>
    [DisplayName("Server")]
    [Description("The server address to connect to.")]
    [RefreshProperties(RefreshProperties.All)]
    public string Server
    {
        get => GetString("Server", DefaultServer);
        set => this["Server"] = value ?? DefaultServer;
    }

    /// <summary>
    /// Gets or sets the server port.
    /// </summary>
    [DisplayName("Port")]
    [Description("The server port to connect to.")]
    [RefreshProperties(RefreshProperties.All)]
    public int Port
    {
        get => GetInt32("Port", DefaultPort);
        set
        {
            if (value < 1 || value > 65535)
                throw new ArgumentOutOfRangeException(nameof(value), "Port must be between 1 and 65535.");
            this["Port"] = value;
        }
    }

    /// <summary>
    /// Gets or sets the database name.
    /// </summary>
    [DisplayName("Database")]
    [Description("The database name to connect to.")]
    [RefreshProperties(RefreshProperties.All)]
    public string Database
    {
        get => GetString("Database", DefaultDatabase);
        set => this["Database"] = value ?? DefaultDatabase;
    }

    /// <summary>
    /// Gets or sets the user ID for authentication.
    /// </summary>
    [DisplayName("User Id")]
    [Description("The user name for authentication.")]
    [RefreshProperties(RefreshProperties.All)]
    public string? UserId
    {
        get => GetString("User Id", null);
        set => this["User Id"] = value!;
    }

    /// <summary>
    /// Gets or sets the password for authentication.
    /// </summary>
    [DisplayName("Password")]
    [Description("The password for authentication.")]
    [PasswordPropertyText(true)]
    [RefreshProperties(RefreshProperties.All)]
    public string? Password
    {
        get => GetString("Password", null);
        set => this["Password"] = value!;
    }

    /// <summary>
    /// Gets or sets the connection timeout in seconds.
    /// </summary>
    [DisplayName("Timeout")]
    [Description("The connection timeout in seconds.")]
    [RefreshProperties(RefreshProperties.All)]
    public int Timeout
    {
        get => GetInt32("Timeout", DefaultTimeout);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be non-negative.");
            this["Timeout"] = value;
        }
    }

    /// <summary>
    /// Gets or sets the default command timeout in seconds.
    /// </summary>
    [DisplayName("Command Timeout")]
    [Description("The default command timeout in seconds.")]
    [RefreshProperties(RefreshProperties.All)]
    public int CommandTimeout
    {
        get => GetInt32("CommandTimeout", DefaultCommandTimeout);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "CommandTimeout must be non-negative.");
            this["CommandTimeout"] = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum connection pool size.
    /// </summary>
    [DisplayName("Pool Size")]
    [Description("The maximum number of connections in the pool.")]
    [RefreshProperties(RefreshProperties.All)]
    public int PoolSize
    {
        get => GetInt32("PoolSize", DefaultPoolSize);
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), "PoolSize must be at least 1.");
            this["PoolSize"] = value;
        }
    }

    /// <summary>
    /// Gets or sets the minimum connection pool size.
    /// </summary>
    [DisplayName("Min Pool Size")]
    [Description("The minimum number of connections in the pool.")]
    [RefreshProperties(RefreshProperties.All)]
    public int MinPoolSize
    {
        get => GetInt32("MinPoolSize", DefaultMinPoolSize);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MinPoolSize must be non-negative.");
            this["MinPoolSize"] = value;
        }
    }

    /// <summary>
    /// Gets or sets whether connection pooling is enabled.
    /// </summary>
    [DisplayName("Pooling")]
    [Description("Whether connection pooling is enabled.")]
    [RefreshProperties(RefreshProperties.All)]
    public bool Pooling
    {
        get => GetBoolean("Pooling", DefaultPooling);
        set => this["Pooling"] = value;
    }

    /// <summary>
    /// Gets or sets the application name.
    /// </summary>
    [DisplayName("Application Name")]
    [Description("The application name for identification.")]
    [RefreshProperties(RefreshProperties.All)]
    public string? ApplicationName
    {
        get => GetString("ApplicationName", null);
        set => this["ApplicationName"] = value!;
    }

    /// <summary>
    /// Gets or sets the SSL mode.
    /// </summary>
    [DisplayName("SSL Mode")]
    [Description("The SSL mode (Disable, Prefer, Require).")]
    [RefreshProperties(RefreshProperties.All)]
    public SslMode SslMode
    {
        get
        {
            var value = GetString("SslMode", null);
            if (string.IsNullOrEmpty(value))
                return SslMode.Prefer;
            return Enum.TryParse<SslMode>(value, true, out var mode) ? mode : SslMode.Prefer;
        }
        set => this["SslMode"] = value.ToString();
    }

    /// <summary>
    /// Gets or sets the value associated with the specified key.
    /// </summary>
    /// <param name="keyword">The key of the item.</param>
    /// <returns>The value associated with the specified key.</returns>
    public override object this[string keyword]
    {
        get
        {
            var normalizedKey = NormalizeKeyword(keyword);
            return base.TryGetValue(normalizedKey, out var value) ? value : string.Empty;
        }
#pragma warning disable CS8765
        set
#pragma warning restore CS8765
        {
            var normalizedKey = NormalizeKeyword(keyword);
            if (value == null)
                base.Remove(normalizedKey);
            else
                base[normalizedKey] = value;
        }
    }

    /// <summary>
    /// Determines whether the connection string builder contains a specific key.
    /// </summary>
    /// <param name="keyword">The key to locate.</param>
    /// <returns>true if the key is found; otherwise, false.</returns>
    public override bool ContainsKey(string keyword)
    {
        var normalizedKey = NormalizeKeyword(keyword);
        return base.ContainsKey(normalizedKey);
    }

    /// <summary>
    /// Removes the entry with the specified key.
    /// </summary>
    /// <param name="keyword">The key of the entry to remove.</param>
    /// <returns>true if the key was found and removed; otherwise, false.</returns>
    public override bool Remove(string keyword)
    {
        var normalizedKey = NormalizeKeyword(keyword);
        return base.Remove(normalizedKey);
    }

    /// <summary>
    /// Attempts to get the value associated with the specified key.
    /// </summary>
    /// <param name="keyword">The key to locate.</param>
    /// <param name="value">When this method returns, contains the value associated with the specified key.</param>
    /// <returns>true if the key is found; otherwise, false.</returns>
    public override bool TryGetValue(string keyword, [NotNullWhen(true)] out object? value)
    {
        var normalizedKey = NormalizeKeyword(keyword);
        return base.TryGetValue(normalizedKey, out value);
    }

    /// <summary>
    /// Gets the keys collection.
    /// </summary>
    public override ICollection Keys => base.Keys;

    /// <summary>
    /// Clears the connection string builder.
    /// </summary>
    public override void Clear()
    {
        base.Clear();
    }

    /// <summary>
    /// Retrieves a value indicating whether the specified key exists.
    /// </summary>
    /// <param name="keyword">The key to find.</param>
    /// <returns>true if the key exists; otherwise, false.</returns>
    public override bool ShouldSerialize(string keyword)
    {
        var normalizedKey = NormalizeKeyword(keyword);
        return base.ContainsKey(normalizedKey);
    }

    /// <summary>
    /// Creates a copy of this connection string builder with the password masked.
    /// </summary>
    /// <returns>A string representation with password masked.</returns>
    public string ToSanitizedString()
    {
        var builder = new DataWarehouseConnectionStringBuilder(ConnectionString);
        if (builder.ContainsKey("Password"))
        {
            builder["Password"] = "****";
        }
        return builder.ConnectionString;
    }

    private static string NormalizeKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            throw new ArgumentException("Keyword cannot be null or empty.", nameof(keyword));

        return _keywords.TryGetValue(keyword, out var normalized) ? normalized : keyword;
    }

    private string GetString(string keyword, string? defaultValue)
    {
        return TryGetValue(keyword, out var value) ? value?.ToString() ?? defaultValue ?? string.Empty : defaultValue ?? string.Empty;
    }

    private int GetInt32(string keyword, int defaultValue)
    {
        if (!TryGetValue(keyword, out var value))
            return defaultValue;

        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private bool GetBoolean(string keyword, bool defaultValue)
    {
        if (!TryGetValue(keyword, out var value))
            return defaultValue;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when s.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            string s when s.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            string s when s == "1" => true,
            string s when s == "0" => false,
            _ => defaultValue
        };
    }
}

/// <summary>
/// SSL mode options for the DataWarehouse connection.
/// </summary>
public enum SslMode
{
    /// <summary>
    /// SSL is disabled.
    /// </summary>
    Disable = 0,

    /// <summary>
    /// SSL is preferred but not required.
    /// </summary>
    Prefer = 1,

    /// <summary>
    /// SSL is required.
    /// </summary>
    Require = 2
}
