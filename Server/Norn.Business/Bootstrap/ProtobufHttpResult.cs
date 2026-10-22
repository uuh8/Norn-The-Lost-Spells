using Google.Protobuf;

namespace Norn.Business.Bootstrap;

/// <summary>把消息编码与 HTTP 状态关联，避免错误响应意外变成 200 或被当作 JSON。</summary>
public sealed class ProtobufHttpResult : IResult
{
    // 生成的请求、回复和错误类都实现 IMessage，使适配层无需为每种消息重复编码逻辑。
    private readonly IMessage _message;
    private readonly int _statusCode;

    /// <summary>保留消息与 HTTP 结果；创建结果对象时尚未向客户端写入字节。</summary>
    public ProtobufHttpResult(IMessage message, int statusCode)
    {
        _message = message;
        _statusCode = statusCode;
    }

    /// <summary>由 ASP.NET Core 调用，将共享消息转换为响应体并带上正确的传输元数据。</summary>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // 本入口属于离散业务请求，允许消息 / byte[] 分配；不是未来零 GC 的实时 Codec。
        byte[] payload = _message.ToByteArray();
        httpContext.Response.StatusCode = _statusCode;
        httpContext.Response.ContentType = "application/x-protobuf";
        // Content-Length 的单位是编码后字节数，不是消息字段数或文本字符数。
        httpContext.Response.ContentLength = payload.Length;
        // 请求已经断开时取消写入；本类不负责重连、消息重发或请求幂等。
        await httpContext.Response.Body.WriteAsync(payload.AsMemory(), httpContext.RequestAborted);
    }
}
