namespace Game.ElementField
{
    /// <summary>
    /// 网络液体表现的只读播放时钟。相机或地形造成重新Capture时，必须继续同一权威窗口，
    /// 不能重新从Alpha=0播放；本接口不提供数量写入、Solver推进或Gameplay权限。
    /// 单机不实现此接口，继续使用原有本地Transport History时钟。
    /// </summary>
    public interface ILiquidPresentationPlaybackSource
    {
        uint PresentationRevision { get; }
        uint PresentationInvalidationRevision { get; }
        ulong PresentationWindowId { get; }
        // 跨Chunk中间安装期间，仅候选覆盖的row提供旧端点；其余row继续显示最新数量。
        bool PresentationHolding { get; }
        bool TryGetPresentationAlpha(ulong capturedWindowId, out float alpha);
    }
}
