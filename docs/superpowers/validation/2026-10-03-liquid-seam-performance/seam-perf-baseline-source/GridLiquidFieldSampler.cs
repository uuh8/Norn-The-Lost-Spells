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
        private readonly float _crownStrength;
        private readonly float _proxyStep;
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
        public GridLiquidFieldSampler(IGridLiquidFieldSamples source,int subdivision=2) { _source = source;_snapshot=source as GridLiquidFieldFrame;_crownStrength=source is IGridLiquidCrownSamples crown?crown.CrownStrength:.15f;_shapeSource=_crownStrength>.15f?source as IGridLiquidShapeSamples:null;_proxyStep=.7f/Mathf.Max(2,subdivision); }
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
            for(int y=cy-3;y<=cy+3;y++)
            {
                for(int z=cz-2;z<=cz+2;z++)for(int x=cx-2;x<=cx+2;x++)for(int phase=0;phase<2;phase++)
                {
                    if(singlePhase>=0 && phase!=singlePhase)continue;
                    bool previous=phase==0;float mix=singlePhase>=0?1:previous?1-alpha:alpha;if(mix<=0)continue;
                    var cell=new Vector3Int(x,y,z);float q=0;Vector3 velocity=default,offset=default;
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
                    // r2 Surface Nets圆肩会削掉部分离散体积；补偿随收拢量连续变化，上限25%。
                    float reconstructionGain=1+.25f*Mathf.Clamp01((1-scale)/.6f);
                    float real=_shapeSource!=null?(supported && scale<.99f?offset.y:fill/(scale*scale)*reconstructionGain):fill+(MapHeight(fill,_crownStrength)-fill)*crown;
                    if(_shapeSource!=null && supported && scale<.99f)
                    {
                        // 尾格的1 GMU不能和富液格提供相同轮廓覆盖，否则转移的一瞬间会新增整块面积。
                        float meanFill=real*scale*scale/reconstructionGain;
                        float wet=Mathf.SmoothStep(0,1,Mathf.Clamp01(fill/Mathf.Max(.001f,meanFill*.75f)));k*=wet;g*=wet;
                        if(k<=0)continue;
                    }
                    float proxy=Mathf.Max((_shapeSource!=null?.8f:.5f)*(1+Mathf.Abs(slope.x)+Mathf.Abs(slope.y)),real);
                    // 陡坡圆肩的局部厚度远小于中心厚度；一个Cube跨顶/底时Surface Nets
                    // 的单Vertex会把薄斜层折成条片。坡面保留厚代理，再压回同一显示高度。
                    if(_shapeSource!=null && supported && scale<.99f && slope.sqrMagnitude<.0625f && real>=_proxyStep)proxy=real;
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
                // 在三角化前裁掉接触地板下的代理内部；事后压平交叉三角会留下坡脚折片。
                if(plane.Solid)floor=Mathf.Max(floor,contact);
                float edge=(plane.Coverage-CoverageThreshold)/Mathf.Max(.001f,plane.Gradient.magnitude);
                // 圆肩宽度跟随整个液团的横向形变，避免收拢后仍按旧宽度削掉大部分顶面。
                float rim=Mathf.Clamp01(edge/(1.2f*plane.RimScale/plane.Coverage));rim=Mathf.Sqrt(Mathf.Max(0,rim*(2-rim)));
                result=SmoothMaximum(result,Mathf.Min(edge,Mathf.Min(height-floor,floor+top*rim-height)));
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
                float ratio=Mathf.Clamp(real/proxy,.00001f,1),above=Mathf.Max(0,p.y-f),value=f+above*ratio;
                Vector2 fg=(plane.FloorGradient-f*plane.Gradient)/plane.Coverage;
                Vector2 rg=(plane.RealGradient-real*plane.Gradient)/plane.Coverage,pg=(plane.ProxyGradient-proxy*plane.Gradient)/plane.Coverage;
                Vector2 ratioGradient=(rg*proxy-real*pg)/(proxy*proxy);
                float distance=Mathf.Max(f-p.y,Mathf.Max(0,p.y-f-proxy)),denominator=1+16*distance*distance;
                Vector2 dg=p.y<f?fg:p.y>f+proxy?-fg-pg:Vector2.zero;float dy=p.y<f?-1:p.y>f+proxy?1:0;
                float w=plane.Coverage/denominator,wy=-plane.Coverage*32*distance*dy/(denominator*denominator);
                Vector2 wg=plane.Gradient/denominator-plane.Coverage*32*distance*dg/(denominator*denominator);
                Vector2 vg=p.y>f?(1-ratio)*fg+above*ratioGradient:fg;
                weight+=w;mapped+=w*value;weightGradient+=wg;mappedGradient+=w*vg+wg*value;
                derivativeY+=w*(p.y>f?ratio:0)+wy*value;weightY+=wy;
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
                float rimWidth=1.2f*selected.RimScale/selected.Coverage;
                float edge=(selected.Coverage-CoverageThreshold)/Mathf.Max(.001f,selected.Gradient.magnitude),u=Mathf.Clamp01(edge/rimWidth);
                float rim=Mathf.Sqrt(Mathf.Max(0,u*(2-u)));
                // 顶面由场梯度的朝向判定，不能按一半高度硬切；低侧壁也可能朝上。
                // 保留其他顶点的Jacobian法线，避免底面法线沿Triangle插值成一排暗色锯齿。
                if(normal.y>0)
                {Vector2 rimGradient=u>0 && u<1?selected.Gradient.normalized*((1-u)/Mathf.Max(.001f,rimWidth*rim)):Vector2.zero;
                    Vector2 topGradient=floorGradient+realGradient*rim+selectedReal*rimGradient;normal=new Vector3(-topGradient.x,1,-topGradient.y).normalized;}
            }
            if(_crownStrength>.15f && selected.Solid)
            {
                // 封闭底壳若比Collider仅高0.1mm，坡脚Rasterization会把底部Triangle露成薄齿。
                // 仅朝向地板的底壳嵌入最多2cm；顶面不移动，接触法线与高度共同平滑衰减。
                Vector3 supportNormal=new Vector3(-contactGradient.x,1,-contactGradient.y).normalized;
                float underside=Mathf.SmoothStep(0,1,Mathf.Clamp01(-Vector3.Dot(normal,supportNormal)/.25f));
                float inset=Mathf.Min(.02f/_source.CellSize,selectedReal*.05f)*underside*(1-LastSwellWeight);
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
