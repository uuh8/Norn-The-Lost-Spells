using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    public interface IGridLiquidFieldSamples
    {
        Vector3 Origin { get; }
        float CellSize { get; }
        int Capacity { get; }
        int MaterialCount { get; }
        int GroupCount { get; }
        float Interval { get; }
        int GetGroup(int materialSlot);
        LiquidCellPresentationSample Read(Vector3Int cell, int materialSlot, bool previous);
        Vector4 Terrain(Vector3Int cell);
        bool Supported(Vector3Int cell, bool previous);
    }
    public interface IGridLiquidCrownSamples { float CrownStrength { get; } float Crown(Vector3Int cell,bool previous); }
    public interface IGridLiquidShapeSamples { float ShapeScale(Vector3Int cell,bool previous); bool SolidSupported(Vector3Int cell,bool previous); }

    /// <summary>GPU连续场的CPU参考。坐标均为World Space，数量只读，输出正值表示液体内部。</summary>
    public sealed class GridLiquidFieldSampler
    {
        private readonly IGridLiquidFieldSamples _source;
        private readonly GridLiquidFieldFrame _snapshot;
#if UNITY_EDITOR
        // 同输入性能对照只切换候选格遍历，不撤掉圆冠/Stable等原有显示语义。
        private bool _validationFullScan;
        internal void SetValidationFullScan(bool full)=>_validationFullScan=full;
#endif
        private readonly float _crownStrength;
        private readonly float _cartoonVolumeScale;
        private readonly IGridLiquidShapeSamples _shapeSource;
        private readonly float[] _amounts = new float[8], _planeAmounts=new float[64],_joinedAmounts=new float[8];
        private readonly GroundPlane[] _planes=new GroundPlane[8];
        private int _planeCount;
        private struct GroundPlane
        {
            public float Coverage,Floor,Real,Proxy,RimScale;
            public Vector2 Gradient,FloorGradient,RealGradient,ProxyGradient,Slope;
            public bool Ground,Solid;
            public void Add(in GroundPlane other)
            {Coverage+=other.Coverage;Floor+=other.Floor;Real+=other.Real;Proxy+=other.Proxy;RimScale+=other.RimScale;Gradient+=other.Gradient;
                FloorGradient+=other.FloorGradient;RealGradient+=other.RealGradient;ProxyGradient+=other.ProxyGradient;Slope+=other.Slope;}
        }
        public const float CoverageThreshold = .15f, KernelRadius = 1.45f;
        public float LastSwellWeight {get;private set;}
        public GridLiquidFieldSampler(IGridLiquidFieldSamples source,int subdivision=2) { _source = source;_snapshot=source as GridLiquidFieldFrame;_crownStrength=source is IGridLiquidCrownSamples crown?crown.CrownStrength:.15f;_shapeSource=_crownStrength>.15f?source as IGridLiquidShapeSamples:null;_cartoonVolumeScale=_snapshot?.CartoonVolumeScale??1; }
        // 校准档保留近似真实体积；美术档对少量水使用四次根，形成有明显侧壁的圆润液团。
        // 空格仍为空，满格/深湖回到真实水位。显示增厚不改变GMU，避免填槽/Gameplay被视觉反写。
        public static float MapHeight(float fill,float crownStrength=.15f)
        {
            if(fill<=0 || fill>=1)return Mathf.Max(0,fill);
            float calibrated=fill+Mathf.Min(.15f,Mathf.Clamp01(crownStrength))*Mathf.Min(fill,1-fill);
            float art=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.15f,.8f,crownStrength));
            return Mathf.Lerp(calibrated,Mathf.Pow(fill,.25f),art);
        }
        public static float SmoothMaximum(float a, float b)
        { float h = Mathf.Max(.08f - Mathf.Abs(a - b), 0f) / .08f; return Mathf.Max(a, b) + h * h * h * (.08f / 6f); }
        private static float Kernel(float dx, float dz, out Vector2 gradient)
        {
            float w = Mathf.Max(0f, 1f - (dx * dx + dz * dz) / (KernelRadius * KernelRadius));
            gradient = new Vector2(dx, dz) * (-6f * w * w / (KernelRadius * KernelRadius));
            return w * w * w;
        }
        public float Evaluate(Vector3 position,float alpha)=>EvaluateBlended(position,alpha,false,false);

        // 自由液体也进入统一等值面，形成连续的液舌和液帘，不再按格绘制圆球。
        public float EvaluateGround(Vector3 position,float alpha)=>EvaluateBlended(position,alpha,true,false);
        public float EvaluateGroundProxy(Vector3 position,float alpha)=>EvaluateBlended(position,alpha,false,true);
        private float EvaluateBlended(Vector3 position,float alpha,bool ground,bool proxy)
        {
            alpha=Mathf.Clamp01(alpha);
            // CPU回退重建完整Current快照(alpha=1)。Lerp的两个参数都会先求值，
            // 直接返回端点可省去完全不参与结果的Previous宽核，同时保持中间相位插值。
            if(alpha<=0)return EvaluatePhase(position,true,alpha,ground,proxy);
            if(alpha>=1)return EvaluatePhase(position,false,alpha,ground,proxy);
            return Mathf.Lerp(EvaluatePhase(position,true,alpha,ground,proxy),EvaluatePhase(position,false,alpha,ground,proxy),alpha);
        }

        private int AddPlane(in GroundPlane row)
        {
            // 整数Y行只负责寻址。同一真实支撑平面跨行合并；不同平台/台阶不合并。
            float floor=row.Floor/row.Coverage;Vector2 slope=row.Slope/row.Coverage;
            for(int i=0;i<_planeCount;i++)
            {ref var plane=ref _planes[i];if(plane.Ground==row.Ground && plane.Solid==row.Solid && Mathf.Abs(plane.Floor/plane.Coverage-floor)<.1f
                && (plane.Slope/plane.Coverage-slope).sqrMagnitude<.04f){plane.Add(row);return i;}}
            if(_planeCount<_planes.Length){_planes[_planeCount]=row;return _planeCount++;}
            // 极端多平台在固定工作容量内合并最近的面，保留全部数量权重，不扩容热路径数组。
            int closest=0;float distance=float.PositiveInfinity;
            for(int i=0;i<_planeCount;i++){float d=Mathf.Abs(_planes[i].Floor/_planes[i].Coverage-floor);if(d<distance){closest=i;distance=d;}}
            _planes[closest].Add(row);return closest;
        }
        private float GatherPlanes(Vector3 position,float alpha,int singlePhase,bool colors,bool air)
        {
            _snapshot?.PrepareQuery(position);_planeCount=0;
            if(colors)System.Array.Clear(_planeAmounts,0,_planeAmounts.Length);
            Vector3 at=(position-_source.Origin)/_source.CellSize;
            int cx=Mathf.FloorToInt(at.x),cy=Mathf.FloorToInt(at.y),cz=Mathf.FloorToInt(at.z);float free=-.65f;
            // Frame只列出有数量的邻格；普通参考源仍扫描完整邻域。候选顺序完全相同，
            // Previous-only尾水也保留，避免优化改变插值和接触面的累加顺序。
            bool candidates=_snapshot!=null;
#if UNITY_EDITOR
            candidates&=!_validationFullScan;
#endif
            int neighbors=candidates?_snapshot.QueryCellCount:175;
            for(int neighbor=0;neighbor<neighbors;neighbor++)
            {
                var cell=candidates?_snapshot.QueryCell(neighbor):new Vector3Int(cx-2+neighbor%5,cy-3+neighbor/25,cz-2+neighbor/5%5);
                int x=cell.x,y=cell.y,z=cell.z;
                for(int phase=0;phase<2;phase++)
                {
                    if(singlePhase>=0 && phase!=singlePhase)continue;
                    bool previous=phase==0;float mix=singlePhase>=0?1:previous?1-alpha:alpha;if(mix<=0)continue;
                    if(candidates && !_snapshot.QueryHasPhase(neighbor,previous))continue;
                    float q=0;Vector3 velocity=default,offset=default;
                    if(_snapshot!=null)
                    {var row=_snapshot.ReadRowMotion(cell,previous);q=row.Quantity;velocity=row.VelocityWorld;offset=row.OffsetCells;}
                    else
                    {
                        for(int m=0;m<_source.MaterialCount;m++){var sample=_source.Read(cell,m,previous);if(sample.Quantity==0)continue;q+=sample.Quantity;velocity+=sample.VelocityWorld*sample.Quantity;offset+=sample.OffsetCells*sample.Quantity;}
                        if(q>0){velocity/=q;offset/=q;}
                    }
                    if(q<=0)continue;
                    Vector3 warp=Vector3.ClampMagnitude(velocity*(_source.Interval*(previous?alpha:alpha-1))/_source.CellSize,3);
                    Vector3 center=new Vector3(x+.5f,y+.5f,z+.5f)+offset+warp;
                    bool supported=_source.Supported(cell,previous);
                    // 自由液体也用连续的圆肩体积场，形成液舌/液帘；不按格画圆形水珠。
                    if(!supported && !colors && !air)continue;
                    float scale=_shapeSource?.ShapeScale(cell,previous)??1;
                    float dx=(at.x-center.x)/scale,dz=(at.z-center.z)/scale,k=Kernel(dx,dz,out Vector2 g)*mix;if(k<=0)continue;g*=mix/scale;
                    Vector4 terrain=_source.Terrain(cell);Vector2 slope=supported?new Vector2(-terrain.x,-terrain.z)/Mathf.Max(.25f,terrain.y):Vector2.zero;
                    // 支撑平面固定在Collider上。Kernel覆盖随运动前推，但落水History的
                    // 竖直速度/Offset不能把已支撑的真实地板一起搬动，否则子帧会露出地面窄缝。
                    float floor=supported?(terrain.w-_source.Origin.y)/_source.CellSize+slope.x*(at.x-x-.5f)+slope.y*(at.z-z-.5f):center.y-.5f;
                    float fill=q/_source.Capacity,crown=_source is IGridLiquidCrownSamples crownSource?crownSource.Crown(cell,previous):1;
                    // r2的Cube沿坡面还有X/Z高差。代理厚度覆盖完整采样Cube，避免斜薄片漏采。
                    if(_crownStrength>.15f)crown=Mathf.Lerp(1,crown,.2f);
                    // 收窄圆肩后离散体积损失降低；最大补偿15%，与真实闭合Mesh测量校准。
                    float reconstructionGain=1+.15f*Mathf.Clamp01((1-scale)/.6f);
                    bool uniform=_snapshot!=null && _snapshot.UniformHeight(cell,previous);
                    float volumeScale=_snapshot!=null && _snapshot.StableInput(cell,previous)?1:_cartoonVolumeScale;
                    float real=_shapeSource!=null?(uniform?offset.y:fill/(scale*scale)*reconstructionGain*volumeScale):fill+(MapHeight(fill,_crownStrength)-fill)*crown;
                    // 校准档的自由表面已经上下收圆；再加15%的顶冠会重复补偿体积。
                    if(!supported && _crownStrength<=.15f)real=fill;
                    // 空中外壳必须以数量加权的真实中心对称生成。固定减半格会把少量液体
                    // 压在每个Y行的底部，形成多层薄片；这里与GPU/Bounds使用同一半厚度。
                    if(!supported)floor=center.y-real*.5f;
                    if(uniform)
                    {
                        // 尾格的1 GMU不能和富液格提供相同轮廓覆盖，否则转移的一瞬间会新增整块面积。
                        // 宏观Dome会把同量中心抬高；尾格衰减必须容许这种重新分配，
                        // 不能把鼓起的富液格误当作1 GMU的干尾，否则会在中央重新削出薄坑。
                        float meanFill=real*scale*scale/(reconstructionGain*volumeScale*2.25f);
                        float wet=Mathf.SmoothStep(0,1,Mathf.Clamp01(fill/Mathf.Max(.001f,meanFill*.75f)));k*=wet;g*=wet;
                        if(k<=0)continue;
                    }
                    float proxy=Mathf.Max((_shapeSource!=null?.8f:.5f)*(1+Mathf.Abs(slope.x)+Mathf.Abs(slope.y)),real);
                    // 陡坡圆肩的局部厚度远小于中心厚度；一个Cube跨顶/底时Surface Nets
                    // 的单Vertex会把薄斜层折成条片。坡面保留厚代理，再压回同一显示高度。
                    // 宽Dome的角肩在r2下会让同一个Cube同时穿顶/底，造成非流形折边。
                    // 平地使用至少1.6格的采样代理，再按同一real压回；增加采样厚度不增加显示液量。
                    if(_shapeSource!=null && supported && slope.sqrMagnitude<.0625f)proxy=Mathf.Max(1.6f,real);
                    if(!supported)proxy=Mathf.Max(1.6f,real);
                    // 先按真实平面归组再累加；同一Y行也可能同时包含坡面和台阶，不能先平均它们。
                    var contribution=new GroundPlane{Ground=supported,Solid=_source is IGridLiquidShapeSamples ground?ground.SolidSupported(cell,previous):supported,Coverage=k,Floor=k*floor,Real=k*real,Proxy=k*proxy,RimScale=k*scale,Slope=k*slope,
                        Gradient=g,FloorGradient=g*floor+k*slope,RealGradient=g*real,ProxyGradient=g*proxy};
                    int plane=AddPlane(contribution);
                    // 颜色只读数量，不需再次解码速度/Offset；各材料身份与Density组仍分别保留。
                    if(colors)for(int m=0;m<_source.MaterialCount;m++)_planeAmounts[plane*8+m]+=k*(_snapshot!=null?_snapshot.ReadQuantity(cell,m,previous):_source.Read(cell,m,previous).Quantity);
                }
            }
            MergeContactPlanes(at.y,colors);
            // 相接地面共用采样代理厚度。坡面代理若单独更高，Union会先造一堵竖墙，
            // 随后不同压缩比例再把它折成坡脚碎片；统一代理后才按各自真实液厚压回。
            float commonProxy=0,coverage=0;Vector2 commonGradient=default,coverageGradient=default;
            for(int i=0;i<_planeCount;i++)if(_planes[i].Solid)
            {ref var plane=ref _planes[i];commonProxy+=plane.Proxy;coverage+=plane.Coverage;commonGradient+=plane.ProxyGradient;coverageGradient+=plane.Gradient;}
            if(coverage>0){commonProxy/=coverage;commonGradient=(commonGradient-commonProxy*coverageGradient)/coverage;}
            for(int i=0;i<_planeCount;i++)if(_planes[i].Solid)
            {ref var plane=ref _planes[i];plane.Proxy=commonProxy*plane.Coverage;plane.ProxyGradient=commonGradient*plane.Coverage+commonProxy*plane.Gradient;}
            return free;
        }
        private void MergeContactPlanes(float height,bool colors)
        {
            int anchor=-1;float best=float.PositiveInfinity;
            for(int i=0;i<_planeCount;i++)if(_planes[i].Solid)
            {float d=Mathf.Abs(height-_planes[i].Floor/_planes[i].Coverage);if(d<best){anchor=i;best=d;}}
            if(anchor<0)return;GroundPlane basis=_planes[anchor],joined=default;int kept=0;
            float floor=float.NegativeInfinity;Vector2 floorGradient=default;
            if(colors)System.Array.Clear(_joinedAmounts,0,8);
            for(int i=0;i<_planeCount;i++)
            {
                GroundPlane row=_planes[i];
                if(row.Solid && Compatible(in basis,in row))
                {
                    float f=row.Floor/row.Coverage;if(f>floor){floor=f;floorGradient=(row.FloorGradient-f*row.Gradient)/row.Coverage;}
                    joined.Add(row);if(colors)for(int m=0;m<8;m++)_joinedAmounts[m]+=_planeAmounts[i*8+m];
                }
                else{_planes[kept]=row;if(colors)System.Array.Copy(_planeAmounts,i*8,_planeAmounts,kept*8,8);kept++;}
            }
            // 相接坡面和平地是一张共同外壳。先合并Coverage再生成圆肩，不能先生成
            // 两张相交的厚代理壳再压缩；独立楼层仍由Compatible保留为独立面。
            joined.Ground=joined.Solid=true;joined.Floor=floor*joined.Coverage;
            joined.FloorGradient=floor*joined.Gradient+floorGradient*joined.Coverage;joined.Slope=floorGradient*joined.Coverage;
            _planes[kept]=joined;if(colors)System.Array.Copy(_joinedAmounts,0,_planeAmounts,kept*8,8);_planeCount=kept+1;
        }
        private float EvaluatePhase(Vector3 p,bool previous,float alpha,bool groundOnly=false,bool proxy=false)
        {
            float result=GatherPlanes(p,alpha,previous?0:1,false,!groundOnly),height=(p.y-_source.Origin.y)/_source.CellSize;
            float contact=float.NegativeInfinity;
            for(int i=0;i<_planeCount;i++)if(_planes[i].Solid)contact=Mathf.Max(contact,_planes[i].Floor/_planes[i].Coverage);
            for(int i=0;i<_planeCount;i++)
            {
                ref var plane=ref _planes[i];float floor=plane.Floor/plane.Coverage,top=(proxy?plane.Proxy:plane.Real)/plane.Coverage;
                if(!plane.Ground)floor+=(plane.Real/plane.Coverage-top)*.5f;
                // 在三角化前裁掉接触地板下的代理内部；事后压平交叉三角会留下坡脚折片。
                if(plane.Solid)floor=Mathf.Max(floor,contact);
                float edge=(plane.Coverage-CoverageThreshold)/Mathf.Max(.001f,plane.Gradient.magnitude);
                // 圆肩宽度跟随整个液团的横向形变，避免收拢后仍按旧宽度削掉大部分顶面。
                // 卡通档把圆肩集中到外侧0.7格：旧1.2格会削掉主体的大半截面，形成薄裙边。
                // 校准档保留既有体积曲线，Shader法线也必须使用同一宽度。
                float rimWidth=(_crownStrength>.15f?.7f:1.2f)*plane.RimScale/plane.Coverage;
                float rim=Mathf.Clamp01(edge/rimWidth);rim=Mathf.Sqrt(Mathf.Max(0,rim*(2-rim)));
                // 接地底面贴Collider，自由液舌则上下都收圆；只有顶面收圆会留下切平的薄盖片。
                float vertical=plane.Ground?Mathf.Min(height-floor,floor+top*rim-height):top*.5f*rim-Mathf.Abs(height-floor-top*.5f);
                result=SmoothMaximum(result,Mathf.Min(edge,vertical));
            }
            return result;
        }
        public void CompressGroundProxy(ref Vector3 position,ref Vector3 normal,float alpha)
        {
            LastSwellWeight=0;
            GatherPlanes(position,alpha,-1,false,true);Vector3 p=(position-_source.Origin)/_source.CellSize;
            int chosen=-1;float best=float.PositiveInfinity;
            for(int i=0;i<_planeCount;i++)
            {ref var plane=ref _planes[i];float floor=plane.Floor/plane.Coverage,proxy=plane.Proxy/plane.Coverage;
                if(!plane.Ground)floor+=(plane.Real/plane.Coverage-proxy)*.5f;
                float distance=Mathf.Max(floor-p.y,Mathf.Max(0,p.y-floor-proxy));if(distance<best){chosen=i;best=distance;}}
            if(chosen<0)return;
            ref var selected=ref _planes[chosen];float virtualHeight=selected.Proxy/selected.Coverage;
            // 斜面Cube的插值误差也包含水平采样高差，固定0.15格会漏掉需要约束的底部顶点。
            if(best>Mathf.Max(.15f,virtualHeight))return;
            float weight=0,mapped=0,derivativeY=0,weightY=0,contactFloor=float.NegativeInfinity;
            Vector2 mappedGradient=default,weightGradient=default,contactGradient=default;
            int contributors=0;
            for(int i=0;i<_planeCount;i++)
            {
                ref var plane=ref _planes[i];if(!Compatible(in selected,in plane))continue;contributors++;
                float f=plane.Floor/plane.Coverage,real=plane.Real/plane.Coverage,proxy=plane.Proxy/plane.Coverage;
                float ratio=Mathf.Clamp(real/proxy,.00001f,1),center=f+real*.5f;
                float samplingFloor=plane.Ground?f:center-proxy*.5f,above=Mathf.Max(0,p.y-samplingFloor);
                float value=plane.Ground?f+above*ratio:center+(p.y-center)*ratio;
                Vector2 fg=(plane.FloorGradient-f*plane.Gradient)/plane.Coverage;
                Vector2 rg=(plane.RealGradient-real*plane.Gradient)/plane.Coverage,pg=(plane.ProxyGradient-proxy*plane.Gradient)/plane.Coverage;
                Vector2 ratioGradient=(rg*proxy-real*pg)/(proxy*proxy);
                Vector2 centerGradient=fg+rg*.5f,samplingGradient=plane.Ground?fg:centerGradient-pg*.5f;
                float distance=Mathf.Max(samplingFloor-p.y,Mathf.Max(0,p.y-samplingFloor-proxy)),denominator=1+16*distance*distance;
                Vector2 dg=p.y<samplingFloor?samplingGradient:p.y>samplingFloor+proxy?-samplingGradient-pg:Vector2.zero;float dy=p.y<samplingFloor?-1:p.y>samplingFloor+proxy?1:0;
                float w=plane.Coverage/denominator,wy=-plane.Coverage*32*distance*dy/(denominator*denominator);
                Vector2 wg=plane.Gradient/denominator-plane.Coverage*32*distance*dg/(denominator*denominator);
                Vector2 vg=plane.Ground?(p.y>f?(1-ratio)*fg+above*ratioGradient:fg):(1-ratio)*centerGradient+(p.y-center)*ratioGradient;
                weight+=w;mapped+=w*value;weightGradient+=wg;mappedGradient+=w*vg+wg*value;
                derivativeY+=w*(plane.Ground && p.y<=f?0:ratio)+wy*value;weightY+=wy;
                if(plane.Solid && f>contactFloor){contactFloor=f;contactGradient=fg;}
            }
            float height=mapped/weight,scaleY=Mathf.Max(.00001f,(derivativeY-height*weightY)/weight);
            Vector2 slope=(mappedGradient-height*weightGradient)/weight;
            for(int i=0;i<_planeCount;i++)
            {ref var barrier=ref _planes[i];if(!barrier.Solid)continue;float f=barrier.Floor/barrier.Coverage;
                if(f>height && f-height<1.5f && f>contactFloor){contactFloor=f;contactGradient=(barrier.FloorGradient-f*barrier.Gradient)/barrier.Coverage;}}
            // 连续权重混合Jacobian；两个接触平面的最高地板构成连续的坡脚边界。
            // 不对Coverage设硬阈值，否则邻平面进入Kernel时又会跳动；独立平台由Compatible排除。
            if(height<contactFloor){height=contactFloor;slope=contactGradient;scaleY=1;}
            normal=new Vector3(normal.x-normal.y*slope.x/scaleY,normal.y/scaleY,normal.z-normal.y*slope.y/scaleY).normalized;
            p.y=height+.0002f;
            if(float.IsNegativeInfinity(contactFloor))contactFloor=selected.Floor/selected.Coverage;
            // 接地圆肩和底面不能被Vertex起伏抬开；12cm内平滑衰减，真正离地的顶面才起伏。
            LastSwellWeight=Mathf.SmoothStep(0,1,Mathf.Clamp01((p.y-contactFloor)*_source.CellSize/.12f));
            float selectedFloor=selected.Floor/selected.Coverage,selectedReal=selected.Real/selected.Coverage;
            Vector2 floorGradient=(selected.FloorGradient-selectedFloor*selected.Gradient)/selected.Coverage;
            Vector2 realGradient=(selected.RealGradient-selectedReal*selected.Gradient)/selected.Coverage;
            if(_crownStrength>.15f && contributors==1 && floorGradient.sqrMagnitude<.0625f)
            {
                // 平坦液团采用连续Kernel解析坡度，避免节点法线把宽高光切成方块。
                // 陡坡保留前面的Jacobian法线；平坦近似不能把隐藏底壳误判为坡面顶面。
                float rimWidth=.7f*selected.RimScale/selected.Coverage;
                float edge=(selected.Coverage-CoverageThreshold)/Mathf.Max(.001f,selected.Gradient.magnitude),u=Mathf.Clamp01(edge/rimWidth);
                float rim=Mathf.Sqrt(Mathf.Max(0,u*(2-u)));
                // 顶面由场梯度的朝向判定，不能按一半高度硬切；低侧壁也可能朝上。
                // 保留其他顶点的Jacobian法线，避免底面法线沿Triangle插值成一排暗色锯齿。
                if(normal.y>0)
                {Vector2 rimGradient=u>0 && u<1?selected.Gradient.normalized*((1-u)/Mathf.Max(.001f,rimWidth*rim)):Vector2.zero;
                    Vector2 topGradient=selected.Ground?floorGradient+realGradient*rim+selectedReal*rimGradient:floorGradient+realGradient*.5f*(1+rim)+selectedReal*.5f*rimGradient;normal=new Vector3(-topGradient.x,1,-topGradient.y).normalized;}
            }
            if(_crownStrength>.15f && selected.Solid)
            {
                // 封闭底壳若比Collider仅高0.1mm，坡脚Rasterization会把底部Triangle露成薄齿。
                // 仅朝向地板的底壳做0.5mm Depth Bias；顶面不移动，接触法线与高度共同衰减。
                Vector3 supportNormal=new Vector3(-contactGradient.x,1,-contactGradient.y).normalized;
                float underside=Mathf.SmoothStep(0,1,Mathf.Clamp01(-Vector3.Dot(normal,supportNormal)/.25f));
                // 厚圆冠的侧壁共享Vertex在最终面积法线平均后仍可朝上；埋入2cm会让
                // 这类可见肩部穿坡。仅用0.5mm的Depth Bias藏底壳，几何接触本身负责稳定。
                float inset=Mathf.Min(.0005f/_source.CellSize,selectedReal*.05f)*underside*(1-LastSwellWeight);
                p-=supportNormal*inset;
            }
            position=_source.Origin+p*_source.CellSize;
        }
        private static bool Compatible(in GroundPlane a,in GroundPlane b)
        {
            if(a.Ground!=b.Ground)return false;
            float f=a.Floor/a.Coverage,g=b.Floor/b.Coverage;
            // 相接坡面和平地允许连续形变；同向而不同高度的楼层仍分别重建。
            return Mathf.Abs(f-g)<.1f || a.Solid && b.Solid && (a.Slope/a.Coverage-b.Slope/b.Coverage).sqrMagnitude>.0625f;
        }
        public void Weights(Vector3 position,float alpha,float[] result)
        {
            System.Array.Clear(result,0,result.Length);System.Array.Clear(_amounts,0,_amounts.Length);
            GatherPlanes(position,alpha,-1,true,false);float y=(position.y-_source.Origin.y)/_source.CellSize;
            float best=float.PositiveInfinity,chosenFloor=0,chosenTop=1;
            for(int i=0;i<_planeCount;i++)
            {ref var plane=ref _planes[i];float floor=plane.Floor/plane.Coverage,top=plane.Real/plane.Coverage;
                float distance=Mathf.Max(floor-y,Mathf.Max(0,y-floor-top));if(distance>=best)continue;
                best=distance;chosenFloor=floor;chosenTop=top;System.Array.Copy(_planeAmounts,i*8,_amounts,0,8);}
            float all=0;for(int m=0;m<_source.MaterialCount;m++)all+=_amounts[m];if(all<=0)return;
            float local=Mathf.Clamp01((y-chosenFloor)/Mathf.Max(.001f,chosenTop)),bottom=0;
            for(int groupIndex=0;groupIndex<_source.GroupCount;groupIndex++)
            {
                float group=0;for(int m=0;m<_source.MaterialCount;m++)if(_source.GetGroup(m)==groupIndex)group+=_amounts[m];
                if(group<=0)continue;float top=bottom+group/all;
                if(local<=top || groupIndex==_source.GroupCount-1)
                {for(int m=0;m<_source.MaterialCount;m++)if(_source.GetGroup(m)==groupIndex)result[m]=_amounts[m]/group;return;}bottom=top;
            }
        }

        public Vector3 Normal(Vector3 p, float alpha)
        {
            float e = _source.CellSize * .05f;
            Vector3 gradient = new Vector3(Evaluate(p + Vector3.right * e, alpha) - Evaluate(p - Vector3.right * e, alpha),
                Evaluate(p + Vector3.up * e, alpha) - Evaluate(p - Vector3.up * e, alpha),
                Evaluate(p + Vector3.forward * e, alpha) - Evaluate(p - Vector3.forward * e, alpha));
            return gradient.sqrMagnitude > 1e-10f ? -gradient.normalized : Vector3.up;
        }

        // 小球模板也遵守密度组层序；数量是整滴占比，同密度组内继续按量混色。
        public void DropWeights(float localHeight,float[] quantities,float[] result)
        {
            System.Array.Clear(result,0,result.Length);float all=0,bottom=0;
            for(int m=0;m<_source.MaterialCount;m++)all+=quantities[m];if(all<=0)return;
            for(int g=0;g<_source.GroupCount;g++)
            {
                float group=0;for(int m=0;m<_source.MaterialCount;m++)if(_source.GetGroup(m)==g)group+=quantities[m];
                if(group<=0)continue;float top=bottom+group/all;
                if(localHeight<=top || g==_source.GroupCount-1)
                {for(int m=0;m<_source.MaterialCount;m++)if(_source.GetGroup(m)==g)result[m]=quantities[m]/group;return;}
                bottom=top;
            }
        }
    }
}
