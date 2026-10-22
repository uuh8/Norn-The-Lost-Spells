using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Norn.Business.Bootstrap;

/// <summary>基础 HTTP 适配层：将传输输入变成共享消息，不持有玩家或副本权威状态。</summary>
public static class FoundationEndpoints
{
    /// <summary>扩展方法集中注册基础路由，让 Program 保持为装配入口。</summary>
    public static WebApplication MapFoundationEndpoints(this WebApplication app)
    {
        // Liveness 仅证明进程能响应；尚未接入 MySQL，不能冒充数据库 readiness。
        app.MapGet("/health/live", () => Results.Text("alive"));
        // 版本信息在启动时创建，此后只读；避免每个 GET 都重建消息。
        ServiceInfoReply info = new()
        {
            Service = "norn.business",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            ProtocolVersion = ProtocolCompatibility.CurrentVersion
        };
        // 使用 ProtoJSON 自己的字段/数值映射，与普通 JSON 序列化器的约定不能混淆。
        app.MapGet("/api/system/info", () => Results.Content(JsonFormatter.Default.Format(info), "application/json"));
        // 明确选择返回 IResult 的委托，避免被绑定为忽略返回值的 RequestDelegate。
        app.MapPost("/api/system/ping", (Func<HttpContext, Task<IResult>>)HandlePingAsync);
        return app;
    }

    /// <summary>先限制输入，再解析结构，最后验证业务约束；不能把能解码当成可信。</summary>
    private static async Task<IResult> HandlePingAsync(HttpContext context)
    {
        // Content-Type 选择这条入口支持的编码；HTTP 路径已经约定了具体消息类型。
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? contentType) ||
            !string.Equals(contentType.MediaType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
        {
            return Error(ProtocolErrorCode.UnsupportedContentType, "Expected application/x-protobuf.", StatusCodes.Status415UnsupportedMediaType);
        }
        // 请求头只能快速拒绝已知超限，未知长度仍必须在读取循环中施加真实上限。
        if (context.Request.ContentLength > ProtocolCompatibility.MaxPingPayloadBytes)
            return TooLarge();

        int maximum = ProtocolCompatibility.MaxPingPayloadBytes;
        // 多读一个字节用于判定超限；即使客户端没有 Content-Length，也不会无限扩容。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maximum + 1);
        try
        {
            int count = 0;
            while (count <= maximum)
            {
                // 异步等待输入不占住线程；客户端断开时用 RequestAborted 取消无用读取。
                int received = await context.Request.Body.ReadAsync(buffer.AsMemory(count, maximum + 1 - count), context.RequestAborted);
                if (received == 0)
                    break;
                count += received;
                if (count > maximum)
                    return TooLarge();
            }
            if (count == 0)
                return Error(ProtocolErrorCode.InvalidPayload, "Empty payload.", StatusCodes.Status400BadRequest);

            ProtocolPingRequest request;
            try
            {
                // 池返回的数组可能比申请的大；只解析已接收的 count 字节，忽略尾部空间。
                request = ProtocolPingRequest.Parser.ParseFrom(buffer, 0, count);
            }
            catch (InvalidProtocolBufferException)
            {
                return Error(ProtocolErrorCode.InvalidPayload, "Invalid protobuf payload.", StatusCodes.Status400BadRequest);
            }
            // Parser 只保证二进制结构可读，不会自动拒绝缺省版本 0 或其他应用版本。
            if (!ProtocolCompatibility.IsSupported(request.ProtocolVersion))
                return Error(ProtocolErrorCode.VersionMismatch, "Unsupported protocol version.", StatusCodes.Status409Conflict);

            // Unicode 字符数不等于网络字节数；这项预算针对实际 UTF-8 数据。
            if (Encoding.UTF8.GetByteCount(request.Message) > ProtocolCompatibility.MaxPingMessageBytes)
                return Error(ProtocolErrorCode.InvalidPayload, "Message exceeds UTF-8 byte budget.", StatusCodes.Status400BadRequest);

            // 探测只回显已验证的输入；序号用于关联请求，不授予玩家身份或线上资产。
            return new ProtobufHttpResult(new ProtocolPingReply
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                Sequence = request.Sequence,
                Message = request.Message
            }, StatusCodes.Status200OK);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // 宿主先触发请求体限制时，也尽量使用本入口约定的错误类型返回。
            return TooLarge();
        }
        finally
        {
            // 所有提前 return/异常路径都必须归还池化数组；清理旧请求字节避免跨请求残留。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>统一超限结果，保证声明长度和实际读取量两条路径的语义一致。</summary>
    private static IResult TooLarge() => Error(ProtocolErrorCode.PayloadTooLarge, "Payload exceeds byte budget.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>错误也使用共享 Schema；HTTP 状态、稳定错误码与诊断文本分别承担职责。</summary>
    private static IResult Error(ProtocolErrorCode code, string message, int statusCode) => new ProtobufHttpResult(new ProtocolError
    {
        Code = code,
        Message = message,
        ExpectedProtocolVersion = ProtocolCompatibility.CurrentVersion
    }, statusCode);
}
