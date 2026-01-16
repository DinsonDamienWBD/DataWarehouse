using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Kernel.Configuration
{
    /// <summary>
    /// Manages configuration hot reload with file watching.
    /// Detects config file changes and notifies subscribers.
    /// </summary>
    public sealed class ConfigurationHotReload : IConfigurationChangeNotifier, IDisposable
    {
        private readonly string _configFilePath;
        private readonly FileSystemWatcher? _watcher;
        private readonly IMessageBus? _messageBus;
        private readonly object _configLock = new();
        private readonly ConcurrentDictionary<string, object?> _currentConfig = new();
        private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);

        private DateTime _lastReloadTime = DateTime.MinValue;
        private CancellationTokenSource? _debounceCts;
        private bool _disposed;

        public event Action<ConfigurationChangeEvent>? OnConfigurationChanged;

        /// <summary>
        /// Current configuration values.
        /// </summary>
        public IReadOnlyDictionary<string, object?> CurrentConfiguration => _currentConfig;

        public ConfigurationHotReload(
            string configFilePath,
            IMessageBus? messageBus = null,
            bool enableFileWatcher = true)
        {
            _configFilePath = configFilePath ?? throw new ArgumentNullException(nameof(configFilePath));
            _messageBus = messageBus;

            // Load initial configuration
            if (File.Exists(_configFilePath))
            {
                LoadConfiguration();
            }

            // Set up file watcher if enabled
            if (enableFileWatcher)
            {
                var directory = Path.GetDirectoryName(_configFilePath);
                var fileName = Path.GetFileName(_configFilePath);

                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    _watcher = new FileSystemWatcher(directory, fileName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    _watcher.Changed += OnFileChanged;
                    _watcher.Created += OnFileChanged;
                }
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce rapid changes
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceDelay, _debounceCts.Token);
                    await ReloadAsync(_debounceCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Debounced - another change came in
                }
            });
        }

        public async Task ReloadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(_configFilePath))
            {
                return;
            }

            // Prevent rapid reloads
            var now = DateTime.UtcNow;
            if (now - _lastReloadTime < TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            _lastReloadTime = now;

            Dictionary<string, object?>? newConfig = null;

            // Read and parse the file with retry
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_configFilePath, ct);
                    newConfig = JsonSerializer.Deserialize<Dictionary<string, object?>>(json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            ReadCommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        });
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    // File might be locked, retry
                    await Task.Delay(100, ct);
                }
                catch (JsonException ex)
                {
                    // Invalid JSON - notify but don't apply
                    OnConfigurationChanged?.Invoke(new ConfigurationChangeEvent
                    {
                        Section = "error",
                        OldValues = new Dictionary<string, object?> { ["error"] = ex.Message },
                        NewValues = new Dictionary<string, object?>(),
                        Timestamp = DateTime.UtcNow
                    });
                    return;
                }
            }

            if (newConfig == null)
            {
                return;
            }

            // Detect changes and notify
            var changes = DetectChanges(newConfig);

            if (changes.Count > 0)
            {
                // Apply changes
                lock (_configLock)
                {
                    foreach (var (key, value) in newConfig)
                    {
                        _currentConfig[key] = value;
                    }

                    // Remove keys that no longer exist
                    var keysToRemove = _currentConfig.Keys
                        .Where(k => !newConfig.ContainsKey(k))
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        _currentConfig.TryRemove(key, out _);
                    }
                }

                // Notify subscribers
                foreach (var (section, change) in changes)
                {
                    var changeEvent = new ConfigurationChangeEvent
                    {
                        Section = section,
                        OldValues = change.OldValues,
                        NewValues = change.NewValues,
                        Timestamp = DateTime.UtcNow
                    };

                    OnConfigurationChanged?.Invoke(changeEvent);

                    // Also publish to message bus
                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(MessageTopics.ConfigChanged, new PluginMessage
                        {
                            Type = "kernel.config.changed",
                            Payload = new Dictionary<string, object>
                            {
                                ["Section"] = section,
                                ["OldValues"] = change.OldValues,
                                ["NewValues"] = change.NewValues,
                                ["Timestamp"] = changeEvent.Timestamp
                            }
                        }, ct);
                    }
                }
            }
        }

        private Dictionary<string, (Dictionary<string, object?> OldValues, Dictionary<string, object?> NewValues)> DetectChanges(
            Dictionary<string, object?> newConfig)
        {
            var changes = new Dictionary<string, (Dictionary<string, object?> OldValues, Dictionary<string, object?> NewValues)>();

            lock (_configLock)
            {
                // Find changed and added keys
                foreach (var (key, newValue) in newConfig)
                {
                    _currentConfig.TryGetValue(key, out var oldValue);

                    if (!ValuesEqual(oldValue, newValue))
                    {
                        var section = GetSection(key);

                        if (!changes.TryGetValue(section, out var sectionChanges))
                        {
                            sectionChanges = (new Dictionary<string, object?>(), new Dictionary<string, object?>());
                            changes[section] = sectionChanges;
                        }

                        sectionChanges.OldValues[key] = oldValue;
                        sectionChanges.NewValues[key] = newValue;
                    }
                }

                // Find removed keys
                foreach (var key in _currentConfig.Keys)
                {
                    if (!newConfig.ContainsKey(key))
                    {
                        var section = GetSection(key);

                        if (!changes.TryGetValue(section, out var sectionChanges))
                        {
                            sectionChanges = (new Dictionary<string, object?>(), new Dictionary<string, object?>());
                            changes[section] = sectionChanges;
                        }

                        sectionChanges.OldValues[key] = _currentConfig[key];
                        sectionChanges.NewValues[key] = null;
                    }
                }
            }

            return changes;
        }

        private static string GetSection(string key)
        {
            // Extract section from key (e.g., "pipeline.stages" -> "pipeline")
            var dotIndex = key.IndexOf('.');
            return dotIndex > 0 ? key[..dotIndex] : "root";
        }

        private static bool ValuesEqual(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            // Handle JsonElement comparison
            if (a is JsonElement jeA && b is JsonElement jeB)
            {
                return jeA.ToString() == jeB.ToString();
            }

            return a.Equals(b);
        }

        private void LoadConfiguration()
        {
            try
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, object?>>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                if (config != null)
                {
                    foreach (var (key, value) in config)
                    {
                        _currentConfig[key] = value;
                    }
                }
            }
            catch
            {
                // Ignore errors during initial load
            }
        }

        /// <summary>
        /// Gets a configuration value.
        /// </summary>
        public T? GetValue<T>(string key, T? defaultValue = default)
        {
            if (!_currentConfig.TryGetValue(key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is T typedValue)
            {
                return typedValue;
            }

            if (value is JsonElement je)
            {
                try
                {
                    return je.Deserialize<T>();
                }
                catch
                {
                    return defaultValue;
                }
            }

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Sets a configuration value and persists to file.
        /// </summary>
        public async Task SetValueAsync(string key, object? value, CancellationToken ct = default)
        {
            lock (_configLock)
            {
                _currentConfig[key] = value;
            }

            await SaveConfigurationAsync(ct);
        }

        private async Task SaveConfigurationAsync(CancellationToken ct)
        {
            Dictionary<string, object?> configToSave;

            lock (_configLock)
            {
                configToSave = _currentConfig.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            var json = JsonSerializer.Serialize(configToSave, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Temporarily disable watcher to avoid triggering ourselves
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
            }

            try
            {
                await File.WriteAllTextAsync(_configFilePath, json, ct);
            }
            finally
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = true;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _watcher?.Dispose();
        }
    }

    /// <summary>
    /// Standard message topics for configuration.
    /// </summary>
    public static partial class MessageTopics
    {
        public const string ConfigChanged = "kernel.config.changed";
    }
}
