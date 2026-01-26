# DataWarehouse.Shared

Shared capability-aware library for DataWarehouse CLI and GUI applications.

## Overview

DataWarehouse.Shared provides a unified library for both CLI and GUI applications to:
- Connect to DataWarehouse instances (local or remote)
- Query instance capabilities
- Execute commands that adapt to available features
- Manage connection profiles

## Components

### CapabilityManager
Manages instance capabilities and provides methods to query available features.

```csharp
var capabilityManager = new CapabilityManager();

// Check if a feature is available
if (capabilityManager.HasFeature("encryption"))
{
    // Show encryption UI/commands
}

// Check if a plugin is loaded
if (capabilityManager.HasPlugin("DataWarehouse.Plugins.AesEncryption"))
{
    // Enable AES-specific features
}

// Get all available command categories
var commands = capabilityManager.GetAvailableCommands();
```

### CommandRegistry
Registry of all available commands with their requirements.

```csharp
// Get all commands available for current capabilities
var capabilities = new InstanceCapabilities
{
    SupportsEncryption = true,
    SupportsCompression = true
};
var availableCommands = CommandRegistry.GetAvailableCommandsForCapabilities(capabilities);

// Get commands grouped by category
var commandsByCategory = CommandRegistry.GetCommandsByCategory(capabilities);

// Check if specific command is available
bool canEncrypt = CommandRegistry.IsCommandAvailable("encryption.enable", capabilities);
```

### InstanceManager
Manages connections to DataWarehouse instances.

```csharp
var instanceManager = new InstanceManager();

// Connect to local instance
var target = new ConnectionTarget
{
    Name = "Local Instance",
    Type = ConnectionType.Local,
    Address = "localhost",
    Port = 9000
};

await instanceManager.ConnectAsync(target);

// Execute command
var result = await instanceManager.ExecuteAsync("storage.list", new Dictionary<string, object>());

// Save connection profile
var profile = new ConnectionProfile
{
    Name = "My Local Instance",
    Target = target,
    IsDefault = true
};
instanceManager.SaveProfile(profile);

// Load saved profiles
var profiles = instanceManager.LoadProfiles();
```

### MessageBridge
Bridge for sending messages to connected instances.

```csharp
var bridge = new MessageBridge();

// Connect
await bridge.ConnectAsync(target);

// Send message and wait for response
var message = new Message
{
    Type = MessageType.Request,
    Command = "storage.get",
    Data = new Dictionary<string, object> { ["id"] = "12345" }
};
var response = await bridge.SendAsync(message);

// Send one-way message (fire and forget)
await bridge.SendOneWayAsync(new Message
{
    Type = MessageType.Event,
    Command = "system.notify",
    Data = new Dictionary<string, object> { ["event"] = "user_action" }
});
```

## Usage Pattern in CLI

```csharp
// In CLI Program.cs
var instanceManager = new InstanceManager();

// Connect to instance
var target = new ConnectionTarget
{
    Type = ConnectionType.Local,
    Address = "localhost",
    Port = 9000
};

if (await instanceManager.ConnectAsync(target))
{
    // Get capabilities
    var capabilities = instanceManager.Capabilities;

    // Build command tree based on capabilities
    var availableCommands = CommandRegistry.GetAvailableCommandsForCapabilities(capabilities);

    // Only show commands that are supported
    foreach (var cmd in availableCommands)
    {
        Console.WriteLine($"{cmd.Category}.{cmd.Name}: {cmd.Description}");
    }

    // Execute command
    var result = await instanceManager.ExecuteAsync("storage.list", null);
}
```

## Usage Pattern in GUI

```csharp
// In GUI ViewModel
public class MainViewModel
{
    private readonly InstanceManager _instanceManager;

    public MainViewModel()
    {
        _instanceManager = new InstanceManager();
        _instanceManager.CapabilitiesChanged += OnCapabilitiesChanged;
    }

    private void OnCapabilitiesChanged(object? sender, InstanceCapabilities caps)
    {
        // Update UI to show/hide features based on capabilities
        EncryptionEnabled = caps.SupportsEncryption;
        CompressionEnabled = caps.SupportsCompression;
        // etc.
    }

    public async Task ConnectToInstanceAsync(string address, int port)
    {
        var target = new ConnectionTarget
        {
            Type = ConnectionType.Remote,
            Address = address,
            Port = port
        };

        await _instanceManager.ConnectAsync(target);
    }
}
```

## Connection Types

- **Local**: Connect to instance on localhost via TCP
- **Remote**: Connect to instance on remote host via TCP
- **InProcess**: Direct in-memory communication (for embedded scenarios)

## Command Definitions

Commands are defined with their requirements:
- **IsCore**: Always available (e.g., storage.list, system.info)
- **RequiredFeatures**: Features needed (e.g., "encryption", "compression")
- **RequiredPlugins**: Specific plugins needed

The CommandRegistry automatically filters commands based on current instance capabilities.

## Connection Profiles

Connection profiles are saved in:
- Windows: `%APPDATA%\DataWarehouse\Profiles\`
- Linux/Mac: `~/.config/DataWarehouse/Profiles/`

Each profile contains:
- Connection target information
- Creation and last connected timestamps
- Default profile flag

## Events

### CapabilityManager
- `CapabilitiesChanged`: Fired when capabilities are updated (e.g., when switching instances)

### InstanceManager
- `ConnectionChanged`: Fired when connection target changes
- `ConnectionStatusChanged`: Fired when connection status changes (connected/disconnected)

## Thread Safety

All components are designed to be used from a single thread. For multi-threaded scenarios, use appropriate synchronization.

## Error Handling

Methods return null or false on failure. Check return values before proceeding.

```csharp
var result = await instanceManager.ExecuteAsync("command", params);
if (result == null)
{
    Console.WriteLine("Command execution failed");
}
else if (result.Type == MessageType.Error)
{
    Console.WriteLine($"Error: {result.Error}");
}
```
