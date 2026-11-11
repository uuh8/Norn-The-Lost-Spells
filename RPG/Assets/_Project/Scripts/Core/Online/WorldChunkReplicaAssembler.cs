using System;

namespace Game.Core
{
    public enum WorldReplicaAssemblyFailure : byte
    { None, InvalidHeader, WrongIdentity, PendingBusy, StaleVersion, MissingRevision, Capacity, Timeout, ConflictingPage, Checksum, Payload, NoPending, Installing, InstallRejected, InstallerFaulted }

    /// <summary>
    /// 单Chunk固定容量分页收件箱。TryBegin只能由已认证且关联真实请求的协调器调用；Receive绝不按更大ID
    /// 抢占候选，也不推进任何水位。可信连接Owner可切换ConnectionEpoch，传输层仍须先审实际connectionId/Epoch。
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
        public ulong ConnectionEpoch {get;private set;}
        public WorldChunkKey Key {get;private set;}
        // Installed是该槽曾提交过版本的永久事实，包含权威删除tombstone；退出Interest不能清零，
        // 否则TryBindUnusedKey会把有历史的槽当成空槽，遗忘旧Generation/Revision并接受迟到包。
        public bool Installed {get;private set;}
        /// <summary>
        /// 当前发布数组是否允许读取。与永久版本事实分离：退出Interest后Header仍用于拒绝旧包，
        /// 但旧数量不能继续成为表现数据；只有完整校验和实际Installer成功后才能重新开放读取。
        /// </summary>
        public bool PayloadAvailable {get;private set;}
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
        {if(!PayloadAvailable || index<0 || index>=Header.WordCount)throw new ArgumentOutOfRangeException(nameof(index));return _published[index];}
        public bool TryCopyInstalled(uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;if(!PayloadAvailable || destination==null || destination.Length<Header.WordCount)return false;
            Array.Copy(_published,destination,Header.WordCount);header=Header;return true;
        }

        /// <summary>
        /// 由可信协调器处理当前订阅退出：取消未完成候选并撤销payload读取资格，重复退出幂等。
        /// Key、Installed、Header与版本对象永久保留；数组无需清空，读取守卫阻止旧数量再发布，
        /// 下一份合法Baseline会重新填满staging并经过实际Installer，不能用旧Header直接恢复可见性。
        /// 本类不持有订阅代次，也不直接调用InterestSink；协调器须先核验订阅，并串行释放实际占据。
        /// Installer期间拒绝以保护候选原子发布，Faulted期间拒绝以防退出/重入绕过故障锁存。
        /// </summary>
        public bool TryReleaseInterest()
        {
            if(IsInstallerFaulted)return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);
            if(_installing)return Reject(WorldReplicaAssemblyFailure.Installing);
            CancelPending();PayloadAvailable=false;return Accept();
        }

        /// <summary>
        /// 将预分配但尚未使用的收件槽绑定到原生Chunk地址，不重新分配分页缓冲。
        /// 已收候选不能换Key，否则旧页会被解释成另一块；一旦安装过数据（包括删除tombstone），
        /// 版本记录就永久属于该Chunk，不能靠池复用遗忘Generation/Revision后接受旧包。
        /// WorldEpoch始终由构造固定；这里只允许未建立任何版本事实的槽修改Key。
        /// </summary>
        public bool TryBindUnusedKey(WorldChunkKey key)
        {
            if(IsInstallerFaulted)return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);
            if(_installing)return Reject(WorldReplicaAssemblyFailure.Installing);
            if(Installed)return Reject(WorldReplicaAssemblyFailure.WrongIdentity);
            if(HasPending)return Reject(WorldReplicaAssemblyFailure.PendingBusy);
            if((byte)key.Domain>(byte)WorldMaterialDomain.Gas)return Reject(WorldReplicaAssemblyFailure.WrongIdentity);
            Key=key;return Accept();
        }

        /// <summary>
        /// 由可信连接Owner在认证连接交接时调用，不授予认证、材料写权或新的世界生命期。
        /// 更高代次只取消旧候选，已安装Payload、Header和版本栅栏全部保留；同代次重复配置幂等，
        /// 不能丢半包或重启TTL。零/倒退代次拒绝，不使用可能回绕的加法推断新旧。
        /// Installer回调与Faulted期间不得换身份，避免破坏原子安装或用重连绕过故障锁存。
        /// 页接口本身不含连接代次，调用方仍须在每页进入前验证真实connectionId/ConnectionEpoch。
        /// </summary>
        public bool ResetConnectionEpoch(ulong connectionEpoch)
        {
            if(IsInstallerFaulted)return Reject(WorldReplicaAssemblyFailure.InstallerFaulted);
            if(_installing)return Reject(WorldReplicaAssemblyFailure.Installing);
            if(connectionEpoch==0 || connectionEpoch<ConnectionEpoch)return Reject(WorldReplicaAssemblyFailure.WrongIdentity);
            if(connectionEpoch==ConnectionEpoch)return Accept();
            CancelPending();ConnectionEpoch=connectionEpoch;return Accept();
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
            // 已退出的Header只代表历史版本，不代表实际Store仍有该材料；新订阅首份必须Baseline。
            // 仅切换ConnectionEpoch不会关闭PayloadAvailable，因此保留既有重连的已安装世界合同。
            if(!PayloadAvailable && h.BaseRevision!=0)return Reject(WorldReplicaAssemblyFailure.MissingRevision);
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
            uint[] old=_published;_published=_staging;_staging=old;Header=_pending;Installed=true;PayloadAvailable=true;CancelPending();return Accept();
        }
        public void CancelPending(){if(_installing)return;HasPending=false;_receivedCount=0;_pageCount=0;}
        public bool ExpirePending(long nowTick)
        {if(_installing || !HasPending)return false;if(nowTick<_startTick || nowTick-_startTick>=_timeoutTicks){CancelPending();LastFailure=WorldReplicaAssemblyFailure.Timeout;return true;}return false;}
        private bool Accept(){LastFailure=WorldReplicaAssemblyFailure.None;return true;}
        private bool Reject(WorldReplicaAssemblyFailure f){LastFailure=f;return false;}
    }
}
