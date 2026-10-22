using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>通过测试宿主执行真实路由/解析/响应链；不要求预先启动独立业务进程。</summary>
// IClassFixture 让同一测试类共享应用宿主；这与 Unity Play Mode 或 Linux 部署验证不同。
public sealed class FoundationEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public FoundationEndpointsTests(WebApplicationFactory<Program> factory)
    {
        // 此 HttpClient 访问测试宿主，无须占用正式监听端口，也不是在伪造端点返回值。
        _client = factory.CreateClient();
    }

    // 存活检查只断言进程层响应，不能由此宣称尚未接入的 MySQL 已就绪。
    [Fact]
    public async Task Liveness_RespondsWithoutClaimingDatabaseReadiness()
    {
        using HttpResponseMessage response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alive", await response.Content.ReadAsStringAsync());
    }

    // 用 ProtoJSON 的 Parser 消费服务信息，避免只验证“字符串看起来像 JSON”。
    [Fact]
    public async Task Info_UsesSharedProtoJsonContract()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/system/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ServiceInfoReply reply = JsonParser.Default.Parse<ServiceInfoReply>(await response.Content.ReadAsStringAsync());
        Assert.Equal("norn.business", reply.Service);
        Assert.Equal(ProtocolCompatibility.CurrentVersion, reply.ProtocolVersion);
    }

    // 中文/Emoji 与最大 uint64 可检出字符编码或数值截断问题。
    [Fact]
    public async Task Ping_RoundTrip_PreservesUnicodeAndLargeSequence()
    {
        ProtocolPingRequest request = NewRequest();
        request.Sequence = ulong.MaxValue;
        request.Message = "你好，Norn 👋";
        using HttpResponseMessage response = await PostAsync(request.ToByteArray());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        ProtocolPingReply reply = ProtocolPingReply.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(request.Sequence, reply.Sequence);
        Assert.Equal(request.Message, reply.Message);
        Assert.Equal(ProtocolCompatibility.CurrentVersion, reply.ProtocolVersion);
    }

    // Theory 的两组输入分别验证缺省版本 0 和不兼容版本 2，合计两个用例。
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public async Task Ping_IncompatibleVersion_IsRejected(uint version)
    {
        ProtocolPingRequest request = NewRequest();
        request.ProtocolVersion = version;
        using HttpResponseMessage response = await PostAsync(request.ToByteArray());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        ProtocolError error = ProtocolError.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(ProtocolErrorCode.VersionMismatch, error.Code);
        Assert.Equal(ProtocolCompatibility.CurrentVersion, error.ExpectedProtocolVersion);
    }

    // 字段声明的长度大于实际数据，必须返回错误而不是把残缺消息当作空字段。
    [Fact]
    public async Task Ping_TruncatedWireData_IsRejected()
    {
        using HttpResponseMessage response = await PostAsync(new byte[] { 0x1A, 0x7F, 0x41 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProtocolError error = ProtocolError.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(ProtocolErrorCode.InvalidPayload, error.Code);
    }

    // 超量输入应先被预算拦下，而不是交给 Parser 消耗更多资源。
    [Fact]
    public async Task Ping_OversizedBody_IsRejectedBeforeParsing()
    {
        using HttpResponseMessage response = await PostAsync(new byte[ProtocolCompatibility.MaxPingPayloadBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Ping_UnknownContentLength_StillEnforcesBodyBudget()
    {
        // 不提供 Content-Length，证明服务端有实际读取上限，不能只相信请求头。
        using UnboundedContent content = new(new byte[ProtocolCompatibility.MaxPingPayloadBytes + 1]);
        using HttpResponseMessage response = await _client.PostAsync("/api/system/ping", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // 100 个汉字少于 256 个字符，但其 UTF-8 字节数超过当前文本预算。
    [Fact]
    public async Task Ping_MessageBudget_CountsUtf8BytesInsteadOfCharacters()
    {
        ProtocolPingRequest request = NewRequest();
        request.Message = new string('汉', 100);
        using HttpResponseMessage response = await PostAsync(request.ToByteArray());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // JSON 文本不能因为内容短就误入二进制 Protobuf 入口。
    [Fact]
    public async Task Ping_UnsupportedContentType_IsRejected()
    {
        using StringContent content = new("{}");
        using HttpResponseMessage response = await _client.PostAsync("/api/system/ping", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // 构造一份合法基线；各测试只更改自己要检验的条件，失败原因更明确。
    private static ProtocolPingRequest NewRequest() => new()
    {
        ProtocolVersion = ProtocolCompatibility.CurrentVersion,
        Sequence = 42,
        Message = "foundation"
    };

    // 二进制请求必须显式指定约定的 Media Type；内容对象在请求完成后释放。
    private Task<HttpResponseMessage> PostAsync(byte[] payload)
    {
        ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return PostAndDisposeAsync(content);
    }

    private async Task<HttpResponseMessage> PostAndDisposeAsync(HttpContent content)
    {
        using (content)
            return await _client.PostAsync("/api/system/ping", content);
    }

    /// <summary>刻意隐藏 Content-Length 的请求体，验证服务端不能只信请求头。</summary>
    private sealed class UnboundedContent : HttpContent
    {
        private readonly byte[] _payload;

        public UnboundedContent(byte[] payload)
        {
            _payload = payload;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        }

        protected override bool TryComputeLength(out long length)
        {
            // 返回 false 才表示未知长度；length=0 不是声称请求体确实为空。
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_payload, 0, _payload.Length);
    }
}
