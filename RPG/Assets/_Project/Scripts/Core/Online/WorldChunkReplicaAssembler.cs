using System;

namespace Game.Core
{
    public enum WorldReplicaAssemblyFailure : byte
    { None, InvalidHeader, WrongIdentity, PendingBusy, StaleVersion, MissingRevision, Capacity, Timeout, ConflictingPage, Checksum, Payload, NoPending, Installing, InstallRejected, InstallerFaulted }

    /// <summary>
    /// 单Chunk固定容量分页收件箱。TryBegin只能由已认证且关联真实请求的协调器调用；Receive绝不按更大ID
    /// 抢占候选，也不推进任何水位。ConnectionEpoch属于构造绑定，传输层还必须先检查实际connectionId。
    /// staging与published独立；缺页、错误Checksum、非法数量、版本缺口均保留完整旧payload。
    /// 完整验证后先调用构造期Installer原子安装实际只读Store，再提交版本/交换数组，上层此后才可ACK。
    /// 不提供Installer只用于独立Core word-Replica测试，正式材料复制必须配置；本类自身不发ACK。
    /// 原子发布限同一主线程，不提供跨线程内存同步。每次接收工作量最多一页复制加一次最大Chunk校验。
    /// </summary>
    public sealed class WorldChunkReplicaAssembler
    {
        private uint[] _published,_staging;
        private readonly bool[] _received;
        private readonly WorldReplicaRevisionState _version;
        private readonly int _pageWords;
        private readonly long _timeoutTicks;
        private readonly IWorldChunkPayloadInstaller _installer;
        private bool _installing;
        private WorldChunkPayloadHeader _pending;
        private int _receivedCount,_pageCount;
        private long _startTick;
        public ulong ConnectionEpoch {get;}
        public WorldChunkKey Key {get;}
        public bool Installed {get;private set;}
        public bool HasPending {get;private set;}
        public bool InstallerConfigured => _installer!=null;
        public bool IsInstallerFaulted {get;private set;}
        public Exception InstallerException {get;private set;}
        public WorldChunkPayloadHeader Header {get;private set;}
        public WorldReplicaAssemblyFailure LastFailure {get;private set;}

        public WorldChunkReplicaAssembler(ulong worldEpoch,WorldChunkKey key,ulong connectionEpoch,int maxWords,int maxPages,int pageWords,long timeoutTicks,IWorldChunkPayloadInstaller installer=null)
        {
            if(connectionEpoch==0 || maxWords<=0 || maxPages<=0 || pageWords<=0 || timeoutTicks<=0)throw new ArgumentOutOfRangeException(nameof(maxWords));
            _version=new WorldReplicaRevisionState(worldEpoch);Key=key;ConnectionEpoch=connectionEpoch;
            _published=new uint[maxWords];_staging=new uint[maxWords];_received=new bool[maxPages];_pageWords=pageWords;_timeoutTicks=timeoutTicks;
            _installer=installer;
        }
        public uint GetWord(int index)
        {if(!Installed || index<0 || index>=Header.WordCount)throw new ArgumentOutOfRangeException(nameof(index));return _published[index];}
        public bool TryCopyInstalled(uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;if(!Installed || destination==null || destination.Length<Header.WordCount)return false;
            Array.Copy(_published,destination,Header.WordCount);header=Header;return true;
        }
        public bool TryBegin(WorldChunkPayloadHeader header,long nowTick)
        {
            if(IsInstallerFaulted)return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);
            if(_installing)return Reject(WorldReplicaAssemblyFailure.Installing);
            if(nowTick<0 || header.WorldEpoch!=_version.WorldEpoch || header.Key!=Key)return Reject(WorldReplicaAssemblyFailure.WrongIdentity);
            if(header.SnapshotId==0 || header.Generation==0 || header.Revision==0 || header.ServerTick<0 || header.WordCount<0
                || (!header.IsResident && header.WordCount!=0) || (header.IsResident && header.WordCount<11)
                || (header.BaseRevision!=0 && header.Revision<=header.BaseRevision))return Reject(WorldReplicaAssemblyFailure.InvalidHeader);
            int pages=header.WordCount==0?1:1+(header.WordCount-1)/_pageWords;
            if(header.WordCount>_staging.Length || pages>_received.Length)return Reject(WorldReplicaAssemblyFailure.Capacity);
            if(HasPending)
            {
                // 同一合法Begin重发幂等，但绝不延长TTL；任意新ID需先显式取消旧请求。
                if(_pending.Equals(header))return Accept();
                return Reject(WorldReplicaAssemblyFailure.PendingBusy);
            }
            if(!VersionAllowed(header))return false;
            for(int i=0;i<pages;i++)_received[i]=false;
            _pending=header;_pageCount=pages;_receivedCount=0;_startTick=nowTick;HasPending=true;return Accept();
        }
        private bool VersionAllowed(WorldChunkPayloadHeader h)
        {
            if(!Installed)return h.BaseRevision==0 || Reject(WorldReplicaAssemblyFailure.MissingRevision);
            if(h.Generation<Header.Generation || (h.Generation==Header.Generation && h.Revision<Header.Revision))return Reject(WorldReplicaAssemblyFailure.StaleVersion);
            if(h.BaseRevision==0)
            {
                if(!Header.IsResident && h.IsResident && h.Generation==Header.Generation)return Reject(WorldReplicaAssemblyFailure.MissingRevision);
                // 相同版本必须相同内容；SnapshotId不是“越大越可信”的版本水位。
                if(h.Generation==Header.Generation && h.Revision==Header.Revision && (h.Checksum!=Header.Checksum || h.WordCount!=Header.WordCount || h.IsResident!=Header.IsResident))return Reject(WorldReplicaAssemblyFailure.Payload);
                return true;
            }
            if(h.Generation!=Header.Generation || h.BaseRevision!=Header.Revision || h.Revision<=Header.Revision)return Reject(WorldReplicaAssemblyFailure.MissingRevision);
            // 删除是本代最后一个Delta；重建必须新Generation Baseline，不能用Delta复活。
            if(!Header.IsResident)return Reject(WorldReplicaAssemblyFailure.MissingRevision);
            return true;
        }
        public bool TryReceivePage(WorldChunkPayloadHeader header,int pageIndex,uint[] words,int count,long nowTick)
        {
            if(IsInstallerFaulted)return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);
            if(_installing)return Reject(WorldReplicaAssemblyFailure.Installing);
            if(!HasPending)return Reject(WorldReplicaAssemblyFailure.NoPending);
            if(!_pending.Equals(header))return Reject(WorldReplicaAssemblyFailure.WrongIdentity);
            if(nowTick<_startTick || nowTick-_startTick>=_timeoutTicks){CancelPending();return Reject(WorldReplicaAssemblyFailure.Timeout);}
            if(pageIndex<0 || pageIndex>=_pageCount || words==null || count<0 || count>words.Length)return Reject(WorldReplicaAssemblyFailure.InvalidHeader);
            int offset=pageIndex*_pageWords,expected=Math.Min(_pageWords,_pending.WordCount-offset);
            if(count!=expected)return Reject(WorldReplicaAssemblyFailure.InvalidHeader);
            if(_received[pageIndex])
            {
                for(int i=0;i<count;i++)if(_staging[offset+i]!=words[i])return Reject(WorldReplicaAssemblyFailure.ConflictingPage);
                return Accept();
            }
            Array.Copy(words,0,_staging,offset,count);_received[pageIndex]=true;_receivedCount++;
            if(_receivedCount!=_pageCount)return Accept();
            if(WorldChunkPayloadCodec.ComputeChecksum(_staging,_pending.WordCount)!=_pending.Checksum){CancelPending();return Reject(WorldReplicaAssemblyFailure.Checksum);}
            if(_pending.IsResident && !WorldChunkPayloadCodec.Validate(_staging,_pending.WordCount,Key)){CancelPending();return Reject(WorldReplicaAssemblyFailure.Payload);}
            if(_installer!=null)
            {
                // 实际Replica先预检并原子安装。重入栅栏保证外部调用期间不能取消/替换候选，
                // 因而成功返回后版本仍满足Begin时的检查，后续只剩不会失败的内存发布。
                bool installed=false;_installing=true;
                try { installed=_installer.TryInstall(_pending,_staging,_pending.WordCount); }
                catch(Exception exception) { IsInstallerFaulted=true;InstallerException=exception; }
                finally { _installing=false; }
                if(IsInstallerFaulted){CancelPending();return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);}
                if(!installed){CancelPending();return Reject(WorldReplicaAssemblyFailure.InstallRejected);}
            }
            // 前面不修改版本；这里已经完整验证。单线程内Version提交后只执行不会失败的数组/值交换。
            bool accepted=_pending.BaseRevision==0
                ?_version.TryAcceptBaselineVersion(_pending.WorldEpoch,_pending.Generation,_pending.Revision)
                :_version.TryAcceptDeltaVersion(_pending.WorldEpoch,_pending.Generation,_pending.BaseRevision,_pending.Revision);
            if(!accepted){CancelPending();return Reject(WorldReplicaAssemblyFailure.StaleVersion);}
            uint[] old=_published;_published=_staging;_staging=old;Header=_pending;Installed=true;CancelPending();return Accept();
        }
        public void CancelPending(){if(_installing)return;HasPending=false;_receivedCount=0;_pageCount=0;}
        public bool ExpirePending(long nowTick)
        {if(_installing || !HasPending)return false;if(nowTick<_startTick || nowTick-_startTick>=_timeoutTicks){CancelPending();LastFailure=WorldReplicaAssemblyFailure.Timeout;return true;}return false;}
        private bool Accept(){LastFailure=WorldReplicaAssemblyFailure.None;return true;}
        private bool Reject(WorldReplicaAssemblyFailure f){LastFailure=f;return false;}
    }
}
