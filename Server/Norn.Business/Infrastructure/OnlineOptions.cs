namespace Norn.Business.Infrastructure;

/// <summary>线上模块显式开关；N0 基础 HTTP 测试/工具不因未配 MySQL 而被隐式接管。</summary>
public sealed class OnlineOptions
{
    public bool Enabled { get; set; }
    public RoomInstanceOptions Instances { get; set; } = new();
    public bool AutoMigrate { get; set; }
    public bool RoomsEnabled { get; set; } // 独立功能开关；不能把N2-1的预留事务误当成可玩的联机入口。
    public string[] DungeonIds { get; set; } = ["p7", "p8", "raid"]; // 稳定配置键，后续由启动Profile映射到受控Scene。
    public string ManifestPath { get; set; } = "Content/content-manifest.json";
    public string MigrationConnectionString { get; set; } = "";
    public int SessionMinutes { get; set; } = 30;
    public int AuthRequestsPerMinute { get; set; } = 30;
    public string[] StarterContentIds { get; set; } = ["wandtest.wand.a", "wandtest.fire"];
}
