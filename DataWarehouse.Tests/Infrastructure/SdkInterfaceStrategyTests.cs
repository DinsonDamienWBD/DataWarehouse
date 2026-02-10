using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Interface;
using HttpMethod = DataWarehouse.SDK.Contracts.Interface.HttpMethod;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK interface strategy infrastructure types:
    /// IInterfaceStrategy, InterfaceProtocol, InterfaceRequest, InterfaceResponse,
    /// InterfaceCapabilities, and HttpMethod.
    /// </summary>
    public class SdkInterfaceStrategyTests
    {
        #region IInterfaceStrategy Interface Tests

        [Fact]
        public void IInterfaceStrategy_DefinesProtocolProperty()
        {
            var prop = typeof(IInterfaceStrategy).GetProperty("Protocol");
            Assert.NotNull(prop);
            Assert.Equal(typeof(InterfaceProtocol), prop!.PropertyType);
        }

        [Fact]
        public void IInterfaceStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IInterfaceStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(InterfaceCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IInterfaceStrategy_DefinesStartAsyncMethod()
        {
            var method = typeof(IInterfaceStrategy).GetMethod("StartAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IInterfaceStrategy_DefinesStopAsyncMethod()
        {
            var method = typeof(IInterfaceStrategy).GetMethod("StopAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IInterfaceStrategy_DefinesHandleRequestAsyncMethod()
        {
            var method = typeof(IInterfaceStrategy).GetMethod("HandleRequestAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<InterfaceResponse>), method!.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(InterfaceRequest), parameters[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        }

        #endregion

        #region InterfaceProtocol Enum Tests

        [Fact]
        public void InterfaceProtocol_Has14Values()
        {
            var values = Enum.GetValues<InterfaceProtocol>();
            Assert.Equal(14, values.Length);
        }

        [Theory]
        [InlineData(InterfaceProtocol.Unknown, 0)]
        [InlineData(InterfaceProtocol.REST, 1)]
        [InlineData(InterfaceProtocol.gRPC, 2)]
        [InlineData(InterfaceProtocol.GraphQL, 3)]
        [InlineData(InterfaceProtocol.WebSocket, 4)]
        [InlineData(InterfaceProtocol.Kafka, 11)]
        [InlineData(InterfaceProtocol.Custom, 99)]
        public void InterfaceProtocol_HasExpectedValues(InterfaceProtocol protocol, int expected)
        {
            Assert.Equal(expected, (int)protocol);
        }

        #endregion

        #region InterfaceRequest Tests

        [Fact]
        public void InterfaceRequest_Construction_SetsRequiredProperties()
        {
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            var query = new Dictionary<string, string> { ["page"] = "1" };
            var body = System.Text.Encoding.UTF8.GetBytes("{\"test\":true}");

            var request = new InterfaceRequest(
                HttpMethod.GET,
                "/api/users",
                headers,
                body,
                query,
                InterfaceProtocol.REST);

            Assert.Equal(HttpMethod.GET, request.Method);
            Assert.Equal("/api/users", request.Path);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal(InterfaceProtocol.REST, request.Protocol);
        }

        [Fact]
        public void InterfaceRequest_ContentType_ReturnsHeaderValue()
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/xml",
                ["Authorization"] = "Bearer token123"
            };

            var request = new InterfaceRequest(
                HttpMethod.POST,
                "/api/data",
                headers,
                ReadOnlyMemory<byte>.Empty,
                new Dictionary<string, string>(),
                InterfaceProtocol.REST);

            Assert.Equal("application/xml", request.ContentType);
            Assert.Equal("Bearer token123", request.Authorization);
        }

        [Fact]
        public void InterfaceRequest_TryGetHeader_ReturnsCorrectly()
        {
            var headers = new Dictionary<string, string> { ["X-Custom"] = "test-value" };
            var request = new InterfaceRequest(
                HttpMethod.GET, "/test", headers,
                ReadOnlyMemory<byte>.Empty,
                new Dictionary<string, string>(),
                InterfaceProtocol.REST);

            Assert.True(request.TryGetHeader("X-Custom", out var value));
            Assert.Equal("test-value", value);
            Assert.False(request.TryGetHeader("Missing-Header", out _));
        }

        [Fact]
        public void InterfaceRequest_TryGetQueryParameter_ReturnsCorrectly()
        {
            var query = new Dictionary<string, string> { ["limit"] = "50" };
            var request = new InterfaceRequest(
                HttpMethod.GET, "/api", new Dictionary<string, string>(),
                ReadOnlyMemory<byte>.Empty,
                query,
                InterfaceProtocol.REST);

            Assert.True(request.TryGetQueryParameter("limit", out var value));
            Assert.Equal("50", value);
            Assert.False(request.TryGetQueryParameter("offset", out _));
        }

        #endregion

        #region InterfaceResponse Tests

        [Fact]
        public void InterfaceResponse_Ok_Returns200()
        {
            var body = System.Text.Encoding.UTF8.GetBytes("{\"data\":[]}");
            var response = InterfaceResponse.Ok(body);

            Assert.Equal(200, response.StatusCode);
            Assert.True(response.IsSuccess);
            Assert.False(response.IsClientError);
            Assert.False(response.IsServerError);
        }

        [Fact]
        public void InterfaceResponse_Created_Returns201WithLocation()
        {
            var body = System.Text.Encoding.UTF8.GetBytes("{\"id\":1}");
            var response = InterfaceResponse.Created(body, "/api/users/1");

            Assert.Equal(201, response.StatusCode);
            Assert.True(response.IsSuccess);
            Assert.True(response.Headers.ContainsKey("Location"));
            Assert.Equal("/api/users/1", response.Headers["Location"]);
        }

        [Fact]
        public void InterfaceResponse_NoContent_Returns204()
        {
            var response = InterfaceResponse.NoContent();

            Assert.Equal(204, response.StatusCode);
            Assert.True(response.IsSuccess);
            Assert.True(response.Body.IsEmpty);
        }

        [Theory]
        [InlineData(400, true, false)]
        [InlineData(401, true, false)]
        [InlineData(403, true, false)]
        [InlineData(404, true, false)]
        [InlineData(500, false, true)]
        public void InterfaceResponse_ErrorStatusCodes_CorrectFlags(int statusCode, bool isClient, bool isServer)
        {
            var response = InterfaceResponse.Error(statusCode, "Error");

            Assert.Equal(statusCode, response.StatusCode);
            Assert.False(response.IsSuccess);
            Assert.Equal(isClient, response.IsClientError);
            Assert.Equal(isServer, response.IsServerError);
        }

        [Fact]
        public void InterfaceResponse_ErrorFactories_CreateCorrectCodes()
        {
            Assert.Equal(400, InterfaceResponse.BadRequest("bad").StatusCode);
            Assert.Equal(401, InterfaceResponse.Unauthorized().StatusCode);
            Assert.Equal(403, InterfaceResponse.Forbidden().StatusCode);
            Assert.Equal(404, InterfaceResponse.NotFound().StatusCode);
            Assert.Equal(500, InterfaceResponse.InternalServerError().StatusCode);
        }

        #endregion

        #region InterfaceCapabilities Tests

        [Fact]
        public void InterfaceCapabilities_CreateRestDefaults_HasCorrectValues()
        {
            var caps = InterfaceCapabilities.CreateRestDefaults();

            Assert.False(caps.SupportsStreaming);
            Assert.True(caps.SupportsAuthentication);
            Assert.Contains("application/json", caps.SupportedContentTypes);
            Assert.Equal(10 * 1024 * 1024, caps.MaxRequestSize);
        }

        [Fact]
        public void InterfaceCapabilities_CreateGrpcDefaults_SupportsStreaming()
        {
            var caps = InterfaceCapabilities.CreateGrpcDefaults();

            Assert.True(caps.SupportsStreaming);
            Assert.True(caps.SupportsBidirectionalStreaming);
            Assert.True(caps.SupportsMultiplexing);
            Assert.True(caps.RequiresTLS);
        }

        [Fact]
        public void InterfaceCapabilities_CreateWebSocketDefaults_SupportsBidirectional()
        {
            var caps = InterfaceCapabilities.CreateWebSocketDefaults();

            Assert.True(caps.SupportsStreaming);
            Assert.True(caps.SupportsBidirectionalStreaming);
            Assert.Null(caps.DefaultTimeout);
        }

        [Fact]
        public void InterfaceCapabilities_CreateGraphQLDefaults_HasCorrectContentTypes()
        {
            var caps = InterfaceCapabilities.CreateGraphQLDefaults();

            Assert.Contains("application/graphql", caps.SupportedContentTypes);
            Assert.True(caps.SupportsStreaming);
        }

        #endregion

        #region HttpMethod Enum Tests

        [Fact]
        public void HttpMethod_Has9Values()
        {
            var values = Enum.GetValues<HttpMethod>();
            Assert.Equal(9, values.Length);
        }

        [Fact]
        public void HttpMethod_ContainsStandardMethods()
        {
            var values = Enum.GetValues<HttpMethod>();
            Assert.Contains(HttpMethod.GET, values);
            Assert.Contains(HttpMethod.POST, values);
            Assert.Contains(HttpMethod.PUT, values);
            Assert.Contains(HttpMethod.DELETE, values);
            Assert.Contains(HttpMethod.PATCH, values);
        }

        #endregion
    }
}
