using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.Plugins.UltimateInterface;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.RPC;

/// <summary>
/// XML-RPC interface strategy implementing the XML-RPC specification.
/// </summary>
/// <remarks>
/// Implements XML-RPC protocol with:
/// - Request format: &lt;methodCall&gt;&lt;methodName&gt;...&lt;params&gt;
/// - Response format: &lt;methodResponse&gt;&lt;params&gt; or &lt;fault&gt;
/// - Supports all XML-RPC types: int, double, boolean, string, dateTime.iso8601, base64, struct, array
/// - Fault responses: &lt;fault&gt;&lt;value&gt;&lt;struct&gt; with faultCode and faultString
/// </remarks>
internal sealed class XmlRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public override string StrategyId => "xml-rpc";
    public string DisplayName => "XML-RPC";
    public string SemanticDescription => "XML-RPC interface with support for all XML-RPC data types and fault handling.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["xml-rpc", "rpc", "xml", "legacy"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.XmlRpc;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "text/xml", "application/xml" },
        MaxRequestSize: 5 * 1024 * 1024,
        MaxResponseSize: 10 * 1024 * 1024,
        SupportsBidirectionalStreaming: false,
        SupportsMultiplexing: false,
        DefaultTimeout: TimeSpan.FromSeconds(30),
        RequiresTLS: false
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Validate content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;
        if (!contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return CreateXmlRpcFault(1, "Content-Type must be text/xml or application/xml");
        }

        if (request.Body.Length == 0)
        {
            return CreateXmlRpcFault(2, "Request body is empty");
        }

        try
        {
            var bodyText = Encoding.UTF8.GetString(request.Body.Span);
            var doc = XDocument.Parse(bodyText);

            // Validate root element
            if (doc.Root?.Name.LocalName != "methodCall")
            {
                return CreateXmlRpcFault(3, "Root element must be <methodCall>");
            }

            // Extract method name
            var methodNameElement = doc.Root.Element("methodName");
            if (methodNameElement == null || string.IsNullOrWhiteSpace(methodNameElement.Value))
            {
                return CreateXmlRpcFault(4, "<methodName> element is required");
            }

            var methodName = methodNameElement.Value;

            // Extract params (optional)
            var paramsElement = doc.Root.Element("params");
            var parameters = new List<object>();

            if (paramsElement != null)
            {
                foreach (var param in paramsElement.Elements("param"))
                {
                    var valueElement = param.Element("value");
                    if (valueElement != null)
                    {
                        parameters.Add(ParseXmlRpcValue(valueElement));
                    }
                }
            }

            // Route via message bus
            if (MessageBus != null)
            {
                try
                {
                    var topic = $"interface.xml-rpc.{methodName}";
                    var message = new PluginMessage
                    {
                        Type = topic,
                        SourcePluginId = "UltimateInterface",
                        Payload = new Dictionary<string, object>
                        {
                            ["method"] = methodName,
                            ["parameters"] = parameters
                        }
                    };

                    await MessageBus.PublishAsync(topic, message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return CreateXmlRpcFault(100, $"Internal error: {ex.Message}");
                }
            }

            // Create success response
            var responseXml = CreateXmlRpcResponse(new
            {
                method = methodName,
                status = "executed",
                paramCount = parameters.Count
            });

            var responseBody = Encoding.UTF8.GetBytes(responseXml);
            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>
                {
                    ["content-type"] = "text/xml"
                },
                Body: responseBody
            );
        }
        catch (XmlException ex)
        {
            return CreateXmlRpcFault(5, $"XML parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CreateXmlRpcFault(100, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses an XML-RPC value element into a CLR object.
    /// Supports: int, i4, double, boolean, string, dateTime.iso8601, base64, struct, array.
    /// </summary>
    private static object ParseXmlRpcValue(XElement valueElement)
    {
        // If value has no type element, it's a string
        if (!valueElement.HasElements)
        {
            return valueElement.Value;
        }

        var typeElement = valueElement.Elements().First();
        var typeName = typeElement.Name.LocalName;

        return typeName switch
        {
            "int" or "i4" => int.Parse(typeElement.Value, CultureInfo.InvariantCulture),
            "double" => double.Parse(typeElement.Value, CultureInfo.InvariantCulture),
            "boolean" => typeElement.Value == "1" || typeElement.Value.Equals("true", StringComparison.OrdinalIgnoreCase),
            "string" => typeElement.Value,
            "dateTime.iso8601" => DateTime.Parse(typeElement.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "base64" => Convert.FromBase64String(typeElement.Value),
            "struct" => ParseXmlRpcStruct(typeElement),
            "array" => ParseXmlRpcArray(typeElement),
            _ => typeElement.Value
        };
    }

    /// <summary>
    /// Parses an XML-RPC struct element into a dictionary.
    /// </summary>
    private static Dictionary<string, object> ParseXmlRpcStruct(XElement structElement)
    {
        var result = new Dictionary<string, object>();

        foreach (var member in structElement.Elements("member"))
        {
            var name = member.Element("name")?.Value ?? string.Empty;
            var value = member.Element("value");
            if (value != null)
            {
                result[name] = ParseXmlRpcValue(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses an XML-RPC array element into a list.
    /// </summary>
    private static List<object> ParseXmlRpcArray(XElement arrayElement)
    {
        var result = new List<object>();
        var data = arrayElement.Element("data");

        if (data != null)
        {
            foreach (var value in data.Elements("value"))
            {
                result.Add(ParseXmlRpcValue(value));
            }
        }

        return result;
    }

    /// <summary>
    /// Creates an XML-RPC success response.
    /// </summary>
    private static string CreateXmlRpcResponse(object result)
    {
        var xml = new XDocument(
            new XElement("methodResponse",
                new XElement("params",
                    new XElement("param",
                        new XElement("value",
                            new XElement("struct",
                                new XElement("member",
                                    new XElement("name", "method"),
                                    new XElement("value",
                                        new XElement("string", result.GetType().GetProperty("method")?.GetValue(result)?.ToString() ?? "unknown")
                                    )
                                ),
                                new XElement("member",
                                    new XElement("name", "status"),
                                    new XElement("value",
                                        new XElement("string", "executed")
                                    )
                                )
                            )
                        )
                    )
                )
            )
        );

        return xml.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Creates an XML-RPC fault response.
    /// </summary>
    /// <param name="faultCode">Fault code (integer)</param>
    /// <param name="faultString">Fault message</param>
    private static SdkInterface.InterfaceResponse CreateXmlRpcFault(int faultCode, string faultString)
    {
        var faultXml = new XDocument(
            new XElement("methodResponse",
                new XElement("fault",
                    new XElement("value",
                        new XElement("struct",
                            new XElement("member",
                                new XElement("name", "faultCode"),
                                new XElement("value",
                                    new XElement("int", faultCode.ToString(CultureInfo.InvariantCulture))
                                )
                            ),
                            new XElement("member",
                                new XElement("name", "faultString"),
                                new XElement("value",
                                    new XElement("string", faultString)
                                )
                            )
                        )
                    )
                )
            )
        );

        var body = Encoding.UTF8.GetBytes(faultXml.ToString(SaveOptions.DisableFormatting));
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = "text/xml"
            },
            Body: body
        );
    }
}
