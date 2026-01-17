using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DataWarehouse.SDK.Infrastructure
{
    /// <summary>
    /// Factory interface for creating and managing <see cref="HttpClient"/> instances.
    ///
    /// <para>
    /// This interface addresses the HttpClient anti-pattern where creating individual
    /// HttpClient instances per request leads to socket exhaustion. Implementations
    /// should manage connection pooling and client lifecycle appropriately.
    /// </para>
    ///
    /// <para>
    /// <b>Best Practices:</b>
    /// <list type="bullet">
    ///   <item>Use named clients for different API endpoints with distinct configurations</item>
    ///   <item>Prefer injecting IHttpClientFactory over HttpClient directly</item>
    ///   <item>Do not dispose HttpClient instances returned from the factory</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <example>
    /// Basic usage:
    /// <code>
    /// var factory = new DefaultHttpClientFactory();
    /// var client = factory.CreateClient("google-drive");
    /// var response = await client.GetAsync("https://www.googleapis.com/drive/v3/files");
    /// </code>
    /// </example>
    /// <example>
    /// With custom handler configuration:
    /// <code>
    /// var factory = new DefaultHttpClientFactory();
    /// factory.ConfigureClient("my-api", options => {
    ///     options.BaseAddress = new Uri("https://api.example.com");
    ///     options.Timeout = TimeSpan.FromSeconds(30);
    ///     options.DefaultRequestHeaders.Add("X-Api-Key", "secret");
    /// });
    /// var client = factory.CreateClient("my-api");
    /// </code>
    /// </example>
    public interface IHttpClientFactory
    {
        /// <summary>
        /// Creates or retrieves a named <see cref="HttpClient"/> instance.
        /// </summary>
        /// <param name="name">
        /// The logical name of the client to create. Use descriptive names like
        /// "google-drive", "dropbox-api", or "onedrive" to identify different
        /// API endpoints or configurations.
        /// </param>
        /// <returns>
        /// A configured <see cref="HttpClient"/> instance. The client should not
        /// be disposed by the caller as it may be shared across multiple requests.
        /// </returns>
        /// <remarks>
        /// Implementations should ensure that clients with the same name share
        /// underlying connection pools to prevent socket exhaustion.
        /// </remarks>
        HttpClient CreateClient(string name);

        /// <summary>
        /// Creates or retrieves a default <see cref="HttpClient"/> instance.
        /// </summary>
        /// <returns>
        /// A configured <see cref="HttpClient"/> instance with default settings.
        /// </returns>
        HttpClient CreateClient();

        /// <summary>
        /// Configures options for a named client before it is created.
        /// </summary>
        /// <param name="name">The logical name of the client to configure.</param>
        /// <param name="configure">
        /// An action that configures the <see cref="HttpClientOptions"/> for this client.
        /// </param>
        /// <remarks>
        /// Configuration must be performed before the first call to <see cref="CreateClient(string)"/>
        /// for the specified name. Later configuration changes will be applied to newly created
        /// clients but may not affect existing cached clients.
        /// </remarks>
        void ConfigureClient(string name, Action<HttpClientOptions> configure);

        /// <summary>
        /// Registers a custom <see cref="HttpMessageHandler"/> factory for a named client.
        /// </summary>
        /// <param name="name">The logical name of the client.</param>
        /// <param name="handlerFactory">
        /// A factory function that creates the <see cref="HttpMessageHandler"/> to use.
        /// </param>
        /// <remarks>
        /// Use this method when you need custom handler behavior such as:
        /// <list type="bullet">
        ///   <item>Custom authentication handlers</item>
        ///   <item>Request/response logging</item>
        ///   <item>Retry policies via delegating handlers</item>
        ///   <item>Custom certificate validation</item>
        /// </list>
        /// </remarks>
        void ConfigureHandler(string name, Func<HttpMessageHandler> handlerFactory);
    }

    /// <summary>
    /// Configuration options for an <see cref="HttpClient"/> instance.
    /// </summary>
    public sealed class HttpClientOptions
    {
        /// <summary>
        /// Gets or sets the base address for HTTP requests.
        /// </summary>
        /// <remarks>
        /// When set, relative URIs passed to HttpClient methods will be
        /// resolved against this base address.
        /// </remarks>
        public Uri? BaseAddress { get; set; }

        /// <summary>
        /// Gets or sets the timeout for HTTP requests.
        /// </summary>
        /// <remarks>
        /// Default is 100 seconds to match <see cref="HttpClient"/> default.
        /// Set to <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

        /// <summary>
        /// Gets or sets the maximum response content buffer size in bytes.
        /// </summary>
        /// <remarks>
        /// Default is 2GB (int.MaxValue). Reduce for memory-constrained environments.
        /// </remarks>
        public long MaxResponseContentBufferSize { get; set; } = int.MaxValue;

        /// <summary>
        /// Gets the default request headers to be sent with each request.
        /// </summary>
        public Dictionary<string, string> DefaultRequestHeaders { get; } = new();

        /// <summary>
        /// Gets or sets the default HTTP version for requests.
        /// </summary>
        /// <remarks>
        /// Default is HTTP/1.1. Set to <see cref="HttpVersion.Version20"/> for HTTP/2.
        /// </remarks>
        public Version? DefaultRequestVersion { get; set; }

        /// <summary>
        /// Gets or sets whether to enable automatic decompression of response content.
        /// </summary>
        /// <remarks>
        /// When enabled, the client will automatically request and decompress
        /// GZip and Deflate compressed responses.
        /// </remarks>
        public bool EnableAutomaticDecompression { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of connections per server.
        /// </summary>
        /// <remarks>
        /// Default is 0, which uses the system default (typically 10 for HTTP/1.1).
        /// Increase for high-throughput scenarios.
        /// </remarks>
        public int MaxConnectionsPerServer { get; set; }

        /// <summary>
        /// Gets or sets the connection lifetime for pooled connections.
        /// </summary>
        /// <remarks>
        /// Connections older than this value will be closed and new connections
        /// created. This helps respect DNS changes. Default is 2 minutes.
        /// Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable rotation.
        /// </remarks>
        public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets the idle timeout for pooled connections.
        /// </summary>
        /// <remarks>
        /// Connections that have been idle longer than this value will be closed.
        /// Default is 1 minute.
        /// </remarks>
        public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets whether to use cookies.
        /// </summary>
        /// <remarks>
        /// Default is false for API clients. Set to true if the API requires cookies.
        /// </remarks>
        public bool UseCookies { get; set; }

        /// <summary>
        /// Gets or sets the custom certificate validation callback.
        /// </summary>
        /// <remarks>
        /// <b>Warning:</b> Use with extreme caution. Improper use can create
        /// security vulnerabilities by disabling certificate validation.
        /// </remarks>
        public Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? ServerCertificateCustomValidationCallback { get; set; }
    }

    /// <summary>
    /// Default implementation of <see cref="IHttpClientFactory"/> that provides
    /// thread-safe creation and pooling of <see cref="HttpClient"/> instances.
    ///
    /// <para>
    /// This implementation uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> to
    /// cache HttpClient instances by name, ensuring proper connection pooling and
    /// preventing socket exhaustion issues.
    /// </para>
    ///
    /// <para>
    /// <b>Connection Pooling:</b>
    /// Each named client shares a single <see cref="SocketsHttpHandler"/> which
    /// manages its own connection pool. The handler is configured with:
    /// <list type="bullet">
    ///   <item>Configurable connection lifetime for DNS rotation</item>
    ///   <item>Automatic decompression support</item>
    ///   <item>Configurable connection limits per server</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Thread Safety:</b>
    /// All operations on this factory are thread-safe. Multiple threads can
    /// safely call <see cref="CreateClient(string)"/> with the same name and
    /// will receive the same cached instance.
    /// </para>
    /// </summary>
    /// <example>
    /// Production usage with dependency injection:
    /// <code>
    /// // In your composition root / DI setup
    /// services.AddSingleton&lt;IHttpClientFactory, DefaultHttpClientFactory&gt;();
    ///
    /// // In your service
    /// public class MyCloudService
    /// {
    ///     private readonly IHttpClientFactory _httpClientFactory;
    ///
    ///     public MyCloudService(IHttpClientFactory httpClientFactory)
    ///     {
    ///         _httpClientFactory = httpClientFactory;
    ///     }
    ///
    ///     public async Task&lt;string&gt; GetDataAsync()
    ///     {
    ///         var client = _httpClientFactory.CreateClient("my-api");
    ///         return await client.GetStringAsync("/data");
    ///     }
    /// }
    /// </code>
    /// </example>
    public sealed class DefaultHttpClientFactory : IHttpClientFactory, IDisposable
    {
        /// <summary>
        /// Default client name used when no name is specified.
        /// </summary>
        public const string DefaultClientName = "__default__";

        // Thread-safe storage for client instances keyed by name
        private readonly ConcurrentDictionary<string, Lazy<HttpClient>> _clients = new();

        // Thread-safe storage for client options keyed by name
        private readonly ConcurrentDictionary<string, HttpClientOptions> _options = new();

        // Thread-safe storage for custom handler factories keyed by name
        private readonly ConcurrentDictionary<string, Func<HttpMessageHandler>> _handlerFactories = new();

        // Thread-safe storage for handlers to track for disposal
        private readonly ConcurrentDictionary<string, HttpMessageHandler> _handlers = new();

        // Global default options applied to all clients
        private readonly HttpClientOptions _globalDefaults = new();

        // Lock for updating global defaults
        private readonly object _globalDefaultsLock = new();

        // Disposal tracking
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultHttpClientFactory"/> class.
        /// </summary>
        public DefaultHttpClientFactory()
        {
        }

        /// <summary>
        /// Initializes a new instance with global default options.
        /// </summary>
        /// <param name="configureDefaults">
        /// An action to configure the global default options applied to all clients.
        /// </param>
        public DefaultHttpClientFactory(Action<HttpClientOptions> configureDefaults)
        {
            configureDefaults?.Invoke(_globalDefaults);
        }

        /// <inheritdoc />
        public HttpClient CreateClient(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Client name cannot be null or whitespace.", nameof(name));
            }

            ThrowIfDisposed();

            // Use Lazy<T> to ensure thread-safe initialization
            // Only one thread will create the client even with concurrent calls
            var lazy = _clients.GetOrAdd(name, clientName => new Lazy<HttpClient>(
                () => CreateHttpClientCore(clientName),
                LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }

        /// <inheritdoc />
        public HttpClient CreateClient()
        {
            return CreateClient(DefaultClientName);
        }

        /// <inheritdoc />
        public void ConfigureClient(string name, Action<HttpClientOptions> configure)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Client name cannot be null or whitespace.", nameof(name));
            }

            ArgumentNullException.ThrowIfNull(configure);
            ThrowIfDisposed();

            var options = _options.GetOrAdd(name, _ => new HttpClientOptions());
            configure(options);
        }

        /// <inheritdoc />
        public void ConfigureHandler(string name, Func<HttpMessageHandler> handlerFactory)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Client name cannot be null or whitespace.", nameof(name));
            }

            ArgumentNullException.ThrowIfNull(handlerFactory);
            ThrowIfDisposed();

            _handlerFactories[name] = handlerFactory;
        }

        /// <summary>
        /// Configures global default options applied to all clients.
        /// </summary>
        /// <param name="configure">
        /// An action to configure the global default options.
        /// </param>
        /// <remarks>
        /// Global defaults are applied before client-specific options.
        /// Client-specific options take precedence over global defaults.
        /// </remarks>
        public void ConfigureGlobalDefaults(Action<HttpClientOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            ThrowIfDisposed();

            lock (_globalDefaultsLock)
            {
                configure(_globalDefaults);
            }
        }

        /// <summary>
        /// Removes a cached client, causing the next call to <see cref="CreateClient(string)"/>
        /// to create a new instance.
        /// </summary>
        /// <param name="name">The name of the client to remove.</param>
        /// <returns>True if a client was removed; otherwise, false.</returns>
        /// <remarks>
        /// Use this method to force recreation of a client after configuration changes
        /// or when you need to reset a client's state.
        /// </remarks>
        public bool RemoveClient(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            ThrowIfDisposed();

            var removed = _clients.TryRemove(name, out var lazy);

            // Also dispose the handler if we were tracking it
            if (_handlers.TryRemove(name, out var handler))
            {
                handler.Dispose();
            }

            return removed;
        }

        /// <summary>
        /// Creates the HttpClient instance with proper configuration.
        /// </summary>
        private HttpClient CreateHttpClientCore(string name)
        {
            // Merge global defaults with client-specific options
            var options = GetMergedOptions(name);

            // Create or get handler
            HttpMessageHandler handler;
            if (_handlerFactories.TryGetValue(name, out var factory))
            {
                handler = factory();
            }
            else
            {
                handler = CreateDefaultHandler(options);
            }

            // Track handler for disposal
            _handlers[name] = handler;

            // Create client with the handler
            var client = new HttpClient(handler, disposeHandler: false);

            // Apply options to client
            ApplyOptions(client, options);

            return client;
        }

        /// <summary>
        /// Creates the default SocketsHttpHandler with proper pooling configuration.
        /// </summary>
        private static SocketsHttpHandler CreateDefaultHandler(HttpClientOptions options)
        {
            var handler = new SocketsHttpHandler
            {
                // Connection pooling settings
                PooledConnectionLifetime = options.PooledConnectionLifetime,
                PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout,

                // Cookie handling
                UseCookies = options.UseCookies,

                // Automatic decompression
                AutomaticDecompression = options.EnableAutomaticDecompression
                    ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                    : DecompressionMethods.None,
            };

            // Apply max connections if specified
            if (options.MaxConnectionsPerServer > 0)
            {
                handler.MaxConnectionsPerServer = options.MaxConnectionsPerServer;
            }

            // Apply custom certificate validation if specified
            if (options.ServerCertificateCustomValidationCallback != null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    var request = sender as HttpRequestMessage;
                    var cert2 = cert as X509Certificate2;
                    return options.ServerCertificateCustomValidationCallback(request!, cert2, chain, errors);
                };
            }

            return handler;
        }

        /// <summary>
        /// Applies options to the HttpClient instance.
        /// </summary>
        private static void ApplyOptions(HttpClient client, HttpClientOptions options)
        {
            if (options.BaseAddress != null)
            {
                client.BaseAddress = options.BaseAddress;
            }

            client.Timeout = options.Timeout;
            client.MaxResponseContentBufferSize = options.MaxResponseContentBufferSize;

            if (options.DefaultRequestVersion != null)
            {
                client.DefaultRequestVersion = options.DefaultRequestVersion;
            }

            // Apply default headers
            foreach (var header in options.DefaultRequestHeaders)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        /// <summary>
        /// Merges global defaults with client-specific options.
        /// </summary>
        private HttpClientOptions GetMergedOptions(string name)
        {
            var merged = new HttpClientOptions();

            // Apply global defaults first
            lock (_globalDefaultsLock)
            {
                ApplyOptionsTo(merged, _globalDefaults);
            }

            // Apply client-specific options (override globals)
            if (_options.TryGetValue(name, out var clientOptions))
            {
                ApplyOptionsTo(merged, clientOptions);
            }

            return merged;
        }

        /// <summary>
        /// Copies non-default values from source to target options.
        /// </summary>
        private static void ApplyOptionsTo(HttpClientOptions target, HttpClientOptions source)
        {
            if (source.BaseAddress != null)
            {
                target.BaseAddress = source.BaseAddress;
            }

            if (source.Timeout != TimeSpan.FromSeconds(100))
            {
                target.Timeout = source.Timeout;
            }

            if (source.MaxResponseContentBufferSize != int.MaxValue)
            {
                target.MaxResponseContentBufferSize = source.MaxResponseContentBufferSize;
            }

            if (source.DefaultRequestVersion != null)
            {
                target.DefaultRequestVersion = source.DefaultRequestVersion;
            }

            target.EnableAutomaticDecompression = source.EnableAutomaticDecompression;

            if (source.MaxConnectionsPerServer > 0)
            {
                target.MaxConnectionsPerServer = source.MaxConnectionsPerServer;
            }

            target.PooledConnectionLifetime = source.PooledConnectionLifetime;
            target.PooledConnectionIdleTimeout = source.PooledConnectionIdleTimeout;
            target.UseCookies = source.UseCookies;

            if (source.ServerCertificateCustomValidationCallback != null)
            {
                target.ServerCertificateCustomValidationCallback = source.ServerCertificateCustomValidationCallback;
            }

            foreach (var header in source.DefaultRequestHeaders)
            {
                target.DefaultRequestHeaders[header.Key] = header.Value;
            }
        }

        /// <summary>
        /// Throws if the factory has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DefaultHttpClientFactory));
            }
        }

        /// <summary>
        /// Releases all resources used by the factory.
        /// </summary>
        /// <remarks>
        /// Disposing the factory will dispose all cached handlers. This should
        /// only be done when the application is shutting down.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Dispose all tracked handlers
            foreach (var kvp in _handlers)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch
                {
                    // Ignore disposal errors during shutdown
                }
            }

            _handlers.Clear();
            _clients.Clear();
            _options.Clear();
            _handlerFactories.Clear();
        }
    }

    /// <summary>
    /// Extension methods for <see cref="IHttpClientFactory"/>.
    /// </summary>
    public static class HttpClientFactoryExtensions
    {
        /// <summary>
        /// Creates a client configured for the specified base address.
        /// </summary>
        /// <param name="factory">The HTTP client factory.</param>
        /// <param name="baseAddress">The base address for the client.</param>
        /// <returns>A configured HttpClient instance.</returns>
        /// <remarks>
        /// This method uses the host name as the client name, ensuring connection
        /// pooling is shared for requests to the same host.
        /// </remarks>
        public static HttpClient CreateClient(this IHttpClientFactory factory, Uri baseAddress)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(baseAddress);

            var clientName = baseAddress.Host;
            factory.ConfigureClient(clientName, options =>
            {
                options.BaseAddress = baseAddress;
            });

            return factory.CreateClient(clientName);
        }

        /// <summary>
        /// Creates a client with Bearer token authentication.
        /// </summary>
        /// <param name="factory">The HTTP client factory.</param>
        /// <param name="name">The client name.</param>
        /// <param name="bearerToken">The Bearer token for authentication.</param>
        /// <returns>A configured HttpClient instance.</returns>
        public static HttpClient CreateClientWithBearerToken(
            this IHttpClientFactory factory,
            string name,
            string bearerToken)
        {
            ArgumentNullException.ThrowIfNull(factory);

            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                throw new ArgumentException("Bearer token cannot be null or whitespace.", nameof(bearerToken));
            }

            factory.ConfigureClient(name, options =>
            {
                options.DefaultRequestHeaders["Authorization"] = $"Bearer {bearerToken}";
            });

            return factory.CreateClient(name);
        }

        /// <summary>
        /// Configures a client for a specific cloud provider with appropriate defaults.
        /// </summary>
        /// <param name="factory">The HTTP client factory.</param>
        /// <param name="providerName">
        /// The cloud provider name (e.g., "google-drive", "onedrive", "dropbox", "box").
        /// </param>
        /// <param name="timeout">Optional timeout override. Default is 300 seconds.</param>
        /// <returns>The factory for method chaining.</returns>
        public static IHttpClientFactory ConfigureCloudProvider(
            this IHttpClientFactory factory,
            string providerName,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(factory);

            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider name cannot be null or whitespace.", nameof(providerName));
            }

            factory.ConfigureClient(providerName, options =>
            {
                options.Timeout = timeout ?? TimeSpan.FromSeconds(300);
                options.EnableAutomaticDecompression = true;
                options.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                options.MaxConnectionsPerServer = 20;
            });

            return factory;
        }
    }
}
