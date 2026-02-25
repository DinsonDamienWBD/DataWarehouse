using System.Xml;
using System.Xml.Serialization;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Serializes and deserializes DataWarehouseConfiguration to/from XML.
/// Supports bidirectional persistence: LoadFromFile at startup, SaveToFile after changes.
/// The XML files are COMPLETE living system state files containing EVERY setting.
/// </summary>
public static class ConfigurationSerializer
{
    private static readonly XmlSerializer XmlSerializerInstance = new(typeof(DataWarehouseConfiguration));

    private static readonly XmlSerializerNamespaces EmptyNamespaces = CreateEmptyNamespaces();

    private static XmlSerializerNamespaces CreateEmptyNamespaces()
    {
        var ns = new XmlSerializerNamespaces();
        ns.Add(string.Empty, string.Empty);
        return ns;
    }

    /// <summary>Serialize configuration to XML string.</summary>
    public static string ToXml(DataWarehouseConfiguration config)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8
        };

        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);
        XmlSerializerInstance.Serialize(xmlWriter, config, EmptyNamespaces);
        return stringWriter.ToString();
    }

    /// <summary>Deserialize configuration from XML string.</summary>
    public static DataWarehouseConfiguration? FromXml(string xml)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, readerSettings);
        return XmlSerializerInstance.Deserialize(xmlReader) as DataWarehouseConfiguration;
    }

    /// <summary>
    /// Load configuration from XML file.
    /// If the file does not exist, creates a default standard configuration and writes it.
    /// </summary>
    public static DataWarehouseConfiguration LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            // Create default config if missing
            var defaultConfig = ConfigurationPresets.CreateStandard();
            SaveToFile(defaultConfig, filePath);
            return defaultConfig;
        }

        var xml = File.ReadAllText(filePath);
        return FromXml(xml) ?? ConfigurationPresets.CreateStandard();
    }

    /// <summary>
    /// Save configuration to XML file (BIDIRECTIONAL write-back).
    /// Called after every runtime configuration change to persist the live state.
    /// </summary>
    public static void SaveToFile(DataWarehouseConfiguration config, string filePath)
    {
        var xml = ToXml(config);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(filePath, xml);
    }

    /// <summary>
    /// Load preset from embedded resource.
    /// Falls back to programmatic preset creation if resource not found.
    /// </summary>
    public static DataWarehouseConfiguration LoadPreset(string presetName)
    {
        var assembly = typeof(ConfigurationSerializer).Assembly;
        var resourceName = $"DataWarehouse.SDK.Primitives.Configuration.Presets.{presetName}.xml";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return ConfigurationPresets.CreateByName(presetName);

        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();
        return FromXml(xml) ?? ConfigurationPresets.CreateByName(presetName);
    }
}
