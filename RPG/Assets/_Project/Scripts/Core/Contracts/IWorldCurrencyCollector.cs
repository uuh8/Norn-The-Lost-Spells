using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 原金币Trigger只报告碰触Actor，不携带自报金额或写钱包权限。
    /// DS组合层从真实Actor认证/Run及唯一Coin事实裁决；单机仍沿原Session端口。
    /// </summary>
    public interface IWorldCurrencyCollector
    {
        bool TryCollectWorldCurrency(ulong coinId, Transform actor);
    }
}
