namespace Norn.Business.Infrastructure;

/// <summary>可信部署配置；Client不能指定Exe、工作目录、端口或控制面地址。本阶段只验证本机Windows。</summary>
public sealed class RoomInstanceOptions
{
    public bool Enabled { get; set; }
    public string BuildPath { get; set; } = "";
    public string WorkRoot { get; set; } = ".artifacts/instances";
    public string BusinessBaseUrl { get; set; } = "http://127.0.0.1:5080/";
    public int FirstPort { get; set; } = 17800;
    public int LastPort { get; set; } = 17831;
    public int MaximumInstances { get; set; } = 4;
    public int ReadyTimeoutSeconds { get; set; } = 45;
    public int LeaseSeconds { get; set; } = 120;
    public int HeartbeatSeconds { get; set; } = 15;
    public int FreezeAfterSeconds { get; set; } = 60;
    public int IdleShutdownSeconds { get; set; } = 180;
    public void Validate()
    {
        if (FirstPort < 1024 || LastPort > 65535 || LastPort < FirstPort || LastPort - FirstPort > 128 || MaximumInstances is < 1 or > 32 ||
            ReadyTimeoutSeconds != 45 || LeaseSeconds != 120 || HeartbeatSeconds != 15 || FreezeAfterSeconds != 60 || IdleShutdownSeconds is < 90 or > 600 ||
            !Uri.TryCreate(BusinessBaseUrl, UriKind.Absolute, out var url) || !url.IsLoopback || url.AbsolutePath != "/" || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) || !string.IsNullOrEmpty(url.UserInfo) || url.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("实例配置越界；本阶段只验证45/120/15/60秒与loopback控制面。");
    }
}
