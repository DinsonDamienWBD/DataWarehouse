// <copyright file="ConnectionStringParser.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.OdbcDriver.Protocol;

/// <summary>
/// Parses and builds ODBC connection strings according to the ODBC specification.
/// Supports both DSN-based and DSN-less connection strings.
/// Thread-safe: All methods are thread-safe.
/// </summary>
public static class ConnectionStringParser
{
    /// <summary>
    /// Common connection string keywords.
    /// </summary>
    public static class Keywords
    {
        /// <summary>Data Source Name.</summary>
        public const string Dsn = "DSN";

        /// <summary>Driver name.</summary>
        public const string Driver = "DRIVER";

        /// <summary>Server/host name.</summary>
        public const string Server = "SERVER";

        /// <summary>Alternative server keyword.</summary>
        public const string Host = "HOST";

        /// <summary>Database name.</summary>
        public const string Database = "DATABASE";

        /// <summary>Alternative database keyword.</summary>
        public const string Db = "DB";

        /// <summary>User ID.</summary>
        public const string Uid = "UID";

        /// <summary>Alternative user keyword.</summary>
        public const string User = "USER";

        /// <summary>Password.</summary>
        public const string Pwd = "PWD";

        /// <summary>Alternative password keyword.</summary>
        public const string Password = "PASSWORD";

        /// <summary>Port number.</summary>
        public const string Port = "PORT";

        /// <summary>Connection timeout.</summary>
        public const string Timeout = "TIMEOUT";

        /// <summary>Alternative timeout keyword.</summary>
        public const string ConnectTimeout = "CONNECT_TIMEOUT";

        /// <summary>Trusted connection flag.</summary>
        public const string TrustedConnection = "TRUSTED_CONNECTION";

        /// <summary>Application name.</summary>
        public const string App = "APP";

        /// <summary>Alternative application name keyword.</summary>
        public const string ApplicationName = "APPLICATION_NAME";

        /// <summary>Encryption flag.</summary>
        public const string Encrypt = "ENCRYPT";

        /// <summary>SSL mode.</summary>
        public const string SslMode = "SSLMODE";

        /// <summary>Charset/encoding.</summary>
        public const string Charset = "CHARSET";

        /// <summary>File DSN path.</summary>
        public const string FileDsn = "FILEDSN";

        /// <summary>Save file DSN flag.</summary>
        public const string SaveFile = "SAVEFILE";
    }

    /// <summary>
    /// Parses an ODBC connection string into a dictionary of key-value pairs.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    /// <returns>A dictionary of connection string attributes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when connectionString is null.</exception>
    public static Dictionary<string, string> Parse(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return result;
        }

        // Connection string format: key1=value1;key2=value2;...
        // Values can be quoted with {} or '' to include special characters
        var regex = new Regex(
            @"(?<key>[^=;]+)\s*=\s*(?:(?:\{(?<braceValue>[^}]*)\})|(?:'(?<quoteValue>[^']*)')|(?<value>[^;]*))",
            RegexOptions.IgnoreCase);

        var matches = regex.Matches(connectionString);

        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value.Trim().ToUpperInvariant();

            // Get value from appropriate capture group
            string value;
            if (match.Groups["braceValue"].Success)
            {
                value = match.Groups["braceValue"].Value;
            }
            else if (match.Groups["quoteValue"].Success)
            {
                value = match.Groups["quoteValue"].Value;
            }
            else
            {
                value = match.Groups["value"].Value.Trim();
            }

            // Normalize common keywords
            key = NormalizeKeyword(key);

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Builds an ODBC connection string from a dictionary of attributes.
    /// </summary>
    /// <param name="attributes">The connection string attributes.</param>
    /// <returns>The formatted connection string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when attributes is null.</exception>
    public static string Build(Dictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (attributes.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        foreach (var kvp in attributes)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            var key = kvp.Key;
            var value = kvp.Value;

            // Quote value if it contains special characters
            if (NeedsQuoting(value))
            {
                if (value.Contains('}'))
                {
                    // Use single quotes if value contains braces
                    value = $"'{value}'";
                }
                else
                {
                    // Use braces for quoting
                    value = $"{{{value}}}";
                }
            }

            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets a value from parsed connection string attributes with fallback keywords.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="keywords">The keywords to try (in order of preference).</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The attribute value or default.</returns>
    public static string GetValue(
        Dictionary<string, string> attributes,
        string[] keywords,
        string defaultValue = "")
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keywords);

        foreach (var keyword in keywords)
        {
            if (attributes.TryGetValue(keyword, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets the server/host from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The server name or default.</returns>
    public static string GetServer(Dictionary<string, string> attributes, string defaultValue = "localhost")
    {
        return GetValue(attributes, new[] { Keywords.Server, Keywords.Host }, defaultValue);
    }

    /// <summary>
    /// Gets the database name from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The database name or default.</returns>
    public static string GetDatabase(Dictionary<string, string> attributes, string defaultValue = "")
    {
        return GetValue(attributes, new[] { Keywords.Database, Keywords.Db }, defaultValue);
    }

    /// <summary>
    /// Gets the user ID from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The user ID or default.</returns>
    public static string GetUserId(Dictionary<string, string> attributes, string defaultValue = "")
    {
        return GetValue(attributes, new[] { Keywords.Uid, Keywords.User }, defaultValue);
    }

    /// <summary>
    /// Gets the password from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The password or default.</returns>
    public static string GetPassword(Dictionary<string, string> attributes, string defaultValue = "")
    {
        return GetValue(attributes, new[] { Keywords.Pwd, Keywords.Password }, defaultValue);
    }

    /// <summary>
    /// Gets the port from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The port number or default.</returns>
    public static int GetPort(Dictionary<string, string> attributes, int defaultValue = 0)
    {
        var portStr = GetValue(attributes, new[] { Keywords.Port }, "");
        return int.TryParse(portStr, out var port) ? port : defaultValue;
    }

    /// <summary>
    /// Gets the connection timeout from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="defaultValue">The default value if not found.</param>
    /// <returns>The timeout in seconds or default.</returns>
    public static int GetTimeout(Dictionary<string, string> attributes, int defaultValue = 30)
    {
        var timeoutStr = GetValue(attributes, new[] { Keywords.Timeout, Keywords.ConnectTimeout }, "");
        return int.TryParse(timeoutStr, out var timeout) ? timeout : defaultValue;
    }

    /// <summary>
    /// Gets the DSN from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <returns>The DSN or empty string.</returns>
    public static string GetDsn(Dictionary<string, string> attributes)
    {
        return GetValue(attributes, new[] { Keywords.Dsn }, "");
    }

    /// <summary>
    /// Gets the driver name from connection string attributes.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <returns>The driver name or empty string.</returns>
    public static string GetDriver(Dictionary<string, string> attributes)
    {
        return GetValue(attributes, new[] { Keywords.Driver }, "");
    }

    /// <summary>
    /// Validates that required connection string attributes are present.
    /// </summary>
    /// <param name="attributes">The parsed attributes.</param>
    /// <param name="missingAttributes">List of missing required attributes.</param>
    /// <returns>True if all required attributes are present.</returns>
    public static bool ValidateRequired(
        Dictionary<string, string> attributes,
        out List<string> missingAttributes)
    {
        missingAttributes = new List<string>();

        // Must have either DSN or Driver+Server
        var hasDsn = !string.IsNullOrEmpty(GetDsn(attributes));
        var hasDriver = !string.IsNullOrEmpty(GetDriver(attributes));
        var hasServer = !string.IsNullOrEmpty(GetServer(attributes, ""));

        if (!hasDsn && !hasDriver)
        {
            missingAttributes.Add("DSN or DRIVER");
        }

        if (!hasDsn && !hasServer)
        {
            missingAttributes.Add("SERVER");
        }

        return missingAttributes.Count == 0;
    }

    /// <summary>
    /// Creates a connection string builder for fluent construction.
    /// </summary>
    /// <returns>A new connection string builder.</returns>
    public static ConnectionStringBuilder CreateBuilder()
    {
        return new ConnectionStringBuilder();
    }

    /// <summary>
    /// Normalizes a connection string keyword to its canonical form.
    /// </summary>
    private static string NormalizeKeyword(string keyword)
    {
        return keyword.ToUpperInvariant() switch
        {
            "HOST" => Keywords.Server,
            "USER" or "USERNAME" => Keywords.Uid,
            "PASSWORD" => Keywords.Pwd,
            "DB" or "CATALOG" or "INITIAL CATALOG" => Keywords.Database,
            "CONNECT_TIMEOUT" or "CONNECTION_TIMEOUT" or "CONNECTIONTIMEOUT" => Keywords.Timeout,
            "APPLICATION_NAME" or "APP NAME" or "APPLICATIONNAME" => Keywords.App,
            "SSLMODE" or "SSL MODE" or "SSL" => Keywords.SslMode,
            _ => keyword.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Determines if a value needs quoting in a connection string.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        return value.Contains(';') ||
               value.Contains('=') ||
               value.Contains(' ') ||
               value.Contains('{') ||
               value.Contains('}') ||
               value.Contains('\'');
    }
}

/// <summary>
/// Fluent builder for ODBC connection strings.
/// </summary>
public sealed class ConnectionStringBuilder
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the data source name (DSN).
    /// </summary>
    /// <param name="dsn">The DSN value.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithDsn(string dsn)
    {
        _attributes[ConnectionStringParser.Keywords.Dsn] = dsn;
        return this;
    }

    /// <summary>
    /// Sets the driver name.
    /// </summary>
    /// <param name="driver">The driver name.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithDriver(string driver)
    {
        _attributes[ConnectionStringParser.Keywords.Driver] = driver;
        return this;
    }

    /// <summary>
    /// Sets the server/host name.
    /// </summary>
    /// <param name="server">The server name.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithServer(string server)
    {
        _attributes[ConnectionStringParser.Keywords.Server] = server;
        return this;
    }

    /// <summary>
    /// Sets the database name.
    /// </summary>
    /// <param name="database">The database name.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithDatabase(string database)
    {
        _attributes[ConnectionStringParser.Keywords.Database] = database;
        return this;
    }

    /// <summary>
    /// Sets the user ID.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithUserId(string userId)
    {
        _attributes[ConnectionStringParser.Keywords.Uid] = userId;
        return this;
    }

    /// <summary>
    /// Sets the password.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithPassword(string password)
    {
        _attributes[ConnectionStringParser.Keywords.Pwd] = password;
        return this;
    }

    /// <summary>
    /// Sets the port number.
    /// </summary>
    /// <param name="port">The port number.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithPort(int port)
    {
        _attributes[ConnectionStringParser.Keywords.Port] = port.ToString();
        return this;
    }

    /// <summary>
    /// Sets the connection timeout.
    /// </summary>
    /// <param name="timeoutSeconds">The timeout in seconds.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithTimeout(int timeoutSeconds)
    {
        _attributes[ConnectionStringParser.Keywords.Timeout] = timeoutSeconds.ToString();
        return this;
    }

    /// <summary>
    /// Sets a custom attribute.
    /// </summary>
    /// <param name="key">The attribute key.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>This builder for chaining.</returns>
    public ConnectionStringBuilder WithAttribute(string key, string value)
    {
        _attributes[key] = value;
        return this;
    }

    /// <summary>
    /// Builds the connection string.
    /// </summary>
    /// <returns>The formatted connection string.</returns>
    public string Build()
    {
        return ConnectionStringParser.Build(_attributes);
    }

    /// <summary>
    /// Gets the current attributes dictionary.
    /// </summary>
    /// <returns>A copy of the attributes dictionary.</returns>
    public Dictionary<string, string> GetAttributes()
    {
        return new Dictionary<string, string>(_attributes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts this builder to its string representation.
    /// </summary>
    /// <returns>The connection string.</returns>
    public override string ToString()
    {
        return Build();
    }
}
