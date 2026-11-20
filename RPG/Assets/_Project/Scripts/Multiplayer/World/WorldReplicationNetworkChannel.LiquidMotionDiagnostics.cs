using System;
using System.IO;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationNetworkChannel
    {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
        private string _liquidProbeDirectory;
        private double _liquidProbeNext,_liquidProbeStart;
        private bool _liquidProbeStarted,_liquidProbeFinished;
        private MotionNetworkReport _liquidProbeBefore;
        [Serializable] private sealed class MotionNetworkReport
        {
            public string Mode,Utc;public ulong WorldEpoch;public int ProcessId;
            public double Seconds,ChargedBytesPerSecond;
            public long Captures,CaptureRejected,GroupsRejected,Pages,EncodedBytes,ChargedBytes,Published,Rejected,TimedOut;
            public long QuantityAcknowledged,QuantityRejected,QuantityPages;
        }
        private MotionNetworkReport ReadMotionNetworkReport()=>new MotionNetworkReport {
            Mode=_coordinator.IsAuthority?"Authority":"Replica",WorldEpoch=_coordinator.WorldEpoch,
            Captures=_motion.Captures,CaptureRejected=_motion.CaptureRejected,GroupsRejected=_motion.GroupsRejected,
            Pages=_motion.PagesSent,EncodedBytes=_motion.EncodedBytesSent,ChargedBytes=_motion.ChargedBytesSent,
            Published=_motion.Published,Rejected=_motion.Rejected,TimedOut=_motion.TimedOut,
            QuantityAcknowledged=_coordinator.Acknowledged,QuantityRejected=_coordinator.Rejected,QuantityPages=_coordinator.PagesSent };
#endif
        /// <summary>仅显式Native诊断安装。正常游戏没有新增文件轮询、采样数组或每帧日志。</summary>
        private void ConfigureLiquidMotionDiagnostics()
        {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
            string path=Environment.GetEnvironmentVariable("NORN_LIQUID_MOTION_PROBE");
            _liquidProbeDirectory=!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)?Path.GetFullPath(path):null;
            _liquidProbeStarted=_liquidProbeFinished=false;_liquidProbeBefore=null;
#endif
        }
        private void PollLiquidMotionDiagnostics()
        {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
            if(_liquidProbeDirectory==null || _liquidProbeFinished || _motion==null || _coordinator==null)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(now<_liquidProbeNext)return;_liquidProbeNext=now+.25;
            try
            {
                if(!_liquidProbeStarted)
                {
                    if(!File.Exists(Path.Combine(_liquidProbeDirectory,"start.request")))return;
                    _liquidProbeStarted=true;_liquidProbeStart=now;_liquidProbeBefore=ReadMotionNetworkReport();return;
                }
                if(now-_liquidProbeStart<10)return;
                _liquidProbeFinished=true;var report=ReadMotionNetworkReport();var before=_liquidProbeBefore;
                report.Seconds=now-_liquidProbeStart;report.Utc=DateTime.UtcNow.ToString("O");
                report.Captures-=before.Captures;report.CaptureRejected-=before.CaptureRejected;report.GroupsRejected-=before.GroupsRejected;
                report.Pages-=before.Pages;report.EncodedBytes-=before.EncodedBytes;report.ChargedBytes-=before.ChargedBytes;
                report.Published-=before.Published;report.Rejected-=before.Rejected;report.TimedOut-=before.TimedOut;
                report.QuantityAcknowledged-=before.QuantityAcknowledged;report.QuantityRejected-=before.QuantityRejected;report.QuantityPages-=before.QuantityPages;
                report.ChargedBytesPerSecond=report.ChargedBytes/report.Seconds;
                using(var process=System.Diagnostics.Process.GetCurrentProcess())report.ProcessId=process.Id;
                Directory.CreateDirectory(_liquidProbeDirectory);
                string file=Path.Combine(_liquidProbeDirectory,"liquid-network-"+report.ProcessId+"-"+report.WorldEpoch+".json");
                using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                using var writer=new StreamWriter(stream);writer.Write(JsonUtility.ToJson(report,true));
            }
            catch(Exception error){_liquidProbeFinished=true;Game.Core.GameLog.Warn("显式液体网络诊断失败："+error.Message,"LiquidMotionProbe");}
#endif
        }
    }
}
