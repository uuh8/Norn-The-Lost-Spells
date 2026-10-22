using Game.NetworkProtocol;

namespace Norn.Business.Infrastructure;

/// <summary>可公开的业务错误只带稳定 Code；不把 SQL、连接串、密码或 Token 回显到 Client。</summary>
public sealed class OnlineFailure(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IResult Result() => Results.Json(new OnlineErrorReply { Code = Code }, statusCode: Status);
}
