// CPU参考与GPU共用的数学约定：正值在液体内部，位置以粗Grid Cell为单位。
// 仅数量参与层厚/混色；Half只保存有界速度和相对位移，不量化World Position。
StructuredBuffer<uint4> _PreviousCells, _CurrentCells;
StructuredBuffer<float4> _TerrainCells;
StructuredBuffer<int4> _ChunkLookup, _OwnerKeys;
int _K, _MaterialCount, _GroupCount, _LookupMask, _OwnerCount, _Resolution;
// HLSL常量数组每个int元素占16B寄存器；不能把C#连续int[8]直接当作它。
// 使用两个int4明确约定布局，避免GPU把所有材料误读成同一密度组。
int4 _Groups0,_Groups1;
int GroupAt(int slot){return slot<4?_Groups0[slot&3]:_Groups1[slot&3];}
float4 _OriginSize;
float _Capacity, _Interval, _Alpha, _CrownStrength;
struct CellSample { float q; float3 v, offset; uint flags; };
uint HashChunk(int3 c) { return (uint)c.x*73856093u^(uint)c.y*19349663u^(uint)c.z*83492791u; }
int ChunkSlot(int3 key)
{
    uint h=HashChunk(key)&(uint)_LookupMask;
    [loop]for(int i=0;i<32;i++)
    {
        int4 e=_ChunkLookup[h];if(e.w==0)return -1;
        if(all(e.xyz==key))return e.w-1;
        h=(h+1)&(uint)_LookupMask;
    }
    return -1;
}
// 每个采样点只涉及8～12个Chunk。先查一次页槽，随后175个邻格复用，
// 减少GPU随机Hash读取；私有小数组属于当前Compute线程，不是共享跨线程状态。
static int3 CachedMinimum,CachedSpan;
static int CachedSlots[12];
void PrepareLookup(float3 p)
{
    int3 c=(int3)floor(p);
    CachedMinimum=(int3)floor((float3)(c-int3(2,3,2))/_K);
    int3 high=(int3)floor((float3)(c+int3(2,3,2))/_K);
    CachedSpan=high-CachedMinimum+1;int count=CachedSpan.x*CachedSpan.y*CachedSpan.z;
    // 很小的Grid Chunk仍走完整Lookup，不能因优化适用范围而裁掉邻格。
    if(count>12){CachedSpan=0;return;}
    [loop]for(int i=0;i<count;i++)
    {int3 d=int3(i%CachedSpan.x,(i/CachedSpan.x)%CachedSpan.y,i/(CachedSpan.x*CachedSpan.y));CachedSlots[i]=ChunkSlot(CachedMinimum+d);}
}
int CellIndex(int3 c)
{
    int3 key=(int3)floor((float3)c/_K);int slot=-1;
    if(CachedSpan.x==0)slot=ChunkSlot(key);
    else{int3 d=key-CachedMinimum;if(any(d<0) || any(d>=CachedSpan))return -1;slot=CachedSlots[d.x+CachedSpan.x*(d.y+CachedSpan.y*d.z)];}
    if(slot<0)return -1;int3 local=c-key*_K;return slot*_K*_K*_K+local.x+_K*(local.y+_K*local.z);
}
CellSample ReadCell(int index,int m,bool previous)
{
    CellSample s=(CellSample)0;if(index<0)return s;
    uint4 p=previous?_PreviousCells[index*_MaterialCount+m]:_CurrentCells[index*_MaterialCount+m];
    s.q=p.x&65535u;s.flags=p.x>>16;
    if(s.q==0)return s;
    s.v=float3(f16tof32(p.y&65535u),f16tof32(p.y>>16),f16tof32(p.z&65535u));
    s.offset=float3(f16tof32(p.z>>16),f16tof32(p.w&65535u),f16tof32(p.w>>16));return s;
}
float HeightMap(float f)
{
    if(f<=0 || f>=1)return max(0,f);
    float calibrated=f+min(.15,saturate(_CrownStrength))*min(f,1-f);
    float art=saturate((_CrownStrength-.15)/.65);art=art*art*(3-2*art);
    return lerp(calibrated,pow(max(f,1e-6),.25),art);
}
float Union(float a,float b) { float h=max(.08-abs(a-b),0)/.08;return max(a,b)+h*h*h*(.08/6); }
float Kernel(float2 d,out float2 gradient)
{
    float w=max(0,1-dot(d,d)/2.1025);gradient=d*(-6*w*w/2.1025);return w*w*w;
}
// 两个Tick只前推已提交速度的有限距离，单位从World Space换到粗格坐标。
float3 BoundedWarp(float3 velocity,float phase)
{
    float3 warp=velocity*(_Interval*phase)/_OriginSize.w;
    return warp*min(1,3/max(.000001,length(warp)));
}
struct GroundPlane
{
    float coverage,floorHeight,realHeight,proxyHeight,rimScale;
    float2 gradient,floorGradient,realGradient,proxyGradient,slope;
    bool ground,solid;
};
// 显式inout传递工作数组，明确函数读写边界，不依赖跨函数的可变static状态。
int AddPlane(GroundPlane row,inout GroundPlane planes[8],inout int planeCount)
{
    float floorHeight=row.floorHeight/row.coverage;float2 slope=row.slope/row.coverage;
    // 固定槽展开让FXC明确数组写入边界；动态inout数组循环会产生无效循环警告。
    [unroll]for(int i=0;i<8;i++)if(i<planeCount)
    {
        GroundPlane old=planes[i];float coverage=max(1e-6,old.coverage);
        // FXC可能提前计算if候选，空槽也须有有限分母，避免预测执行产生NaN/导入警告。
        if(old.ground==row.ground && old.solid==row.solid && abs(old.floorHeight/coverage-floorHeight)<.1
            && dot(old.slope/coverage-slope,old.slope/coverage-slope)<.04)
        {
            old.coverage+=row.coverage;old.floorHeight+=row.floorHeight;old.realHeight+=row.realHeight;old.proxyHeight+=row.proxyHeight;old.rimScale+=row.rimScale;
            old.gradient+=row.gradient;old.floorGradient+=row.floorGradient;old.realGradient+=row.realGradient;old.proxyGradient+=row.proxyGradient;old.slope+=row.slope;
            planes[i]=old;return i;
        }
    }
    if(planeCount<8){int slot=planeCount;planes[slot]=row;planeCount=slot+1;return slot;}
    // 固定容量满时合并最近面；不裁掉数量，极端密集多平台属于几何近似边界。
    int closest=0;float distance=1e10;
    [unroll]for(int j=0;j<8;j++){float d=abs(planes[j].floorHeight/planes[j].coverage-floorHeight);if(d<distance){closest=j;distance=d;}}
    GroundPlane p=planes[closest];p.coverage+=row.coverage;p.floorHeight+=row.floorHeight;p.realHeight+=row.realHeight;p.proxyHeight+=row.proxyHeight;p.rimScale+=row.rimScale;
    p.gradient+=row.gradient;p.floorGradient+=row.floorGradient;p.realGradient+=row.realGradient;p.proxyGradient+=row.proxyGradient;p.slope+=row.slope;planes[closest]=p;return closest;
}
bool CompatiblePlane(GroundPlane a,GroundPlane b)
{
    if(a.ground!=b.ground)return false;
    float2 delta=a.slope/a.coverage-b.slope/b.coverage;
    return abs(a.floorHeight/a.coverage-b.floorHeight/b.coverage)<.1 || a.solid && b.solid && dot(delta,delta)>.0625;
}
float GatherPlanes(float3 at,int singlePhase,bool colors,bool air,out GroundPlane planes[8],out float4 planeFirst[8],out float4 planeSecond[8],out int planeCount)
{
    planeCount=0;PrepareLookup(at);
    [unroll]for(int clear=0;clear<8;clear++){planes[clear]=(GroundPlane)0;planeFirst[clear]=0;planeSecond[clear]=0;}
    int3 atCell=(int3)floor(at);float free=-.65;
    [loop]for(int y=atCell.y-3;y<=atCell.y+3;y++)
    {
        [loop]for(int z=atCell.z-2;z<=atCell.z+2;z++)[loop]for(int x=atCell.x-2;x<=atCell.x+2;x++)
        {
            int index=CellIndex(int3(x,y,z));if(index<0)continue;
            [loop]for(int phase=0;phase<2;phase++)
            {
                if(singlePhase>=0 && phase!=singlePhase)continue;
                bool previous=phase==0;float mix=singlePhase>=0?1:previous?1-_Alpha:_Alpha;if(mix<=0)continue;
                float q=0;float3 velocity=0,offset=0;uint flags=0;
                [loop]for(int m=0;m<_MaterialCount;m++){CellSample sample=ReadCell(index,m,previous);q+=sample.q;velocity+=sample.v*sample.q;offset+=sample.offset*sample.q;flags|=sample.flags;}
                if(q<=0)continue;velocity/=q;offset/=q;
                float3 warp=BoundedWarp(velocity,previous?_Alpha:_Alpha-1),center=float3(x,y,z)+.5+offset+warp;
                bool supported=(flags&1u)!=0;
                // 与贴地液体共用连续外壳；自由液体形成液舌/液帘，不走圆球实例。
                if(!supported && !colors && !air)continue;
                bool shaped=_CrownStrength>.15 && (flags&16u)!=0;float scale=shaped?max(.4,(flags>>8)/255.0):1;
                float2 d=(at.xz-center.xz)/scale,g;float k=Kernel(d,g)*mix;if(k<=0)continue;g*=mix/scale;
                float4 terrain=_TerrainCells[index];float2 slope=supported?-terrain.xz/max(.25,terrain.y):float2(0,0);
                // 水平覆盖前推，真实支撑固定；落水History不能移动Collider平面。
                float floorHeight=supported?(terrain.w-_OriginSize.y)/_OriginSize.w+dot(slope,at.xz-float2(x,z)-.5):center.y-.5;
                float fill=q/_Capacity,crown=(flags&8u)!=0?(flags>>8)/255.0:1;
                if(_CrownStrength>.15)crown=lerp(1,crown,.2);
                // 代理厚度覆盖r2 Cube沿坡面的高差，最后压回真实液厚；不增加Gameplay量。
                // 横向面积s²与纵向1/s²互相补偿，保留GMU对应的可见体积。
                // 有界的离散圆肩补偿，与CPU参考相同；不对Gameplay数量作任何补写。
                float reconstructionGain=1+.25*saturate((1-scale)/.6);
                float realHeight=shaped?((flags&32u)!=0?offset.y:fill/(scale*scale)*reconstructionGain):fill+(HeightMap(fill)-fill)*crown;
                if(shaped && (flags&32u)!=0)
                {float meanFill=realHeight*scale*scale/reconstructionGain,wet=saturate(fill/max(.001,meanFill*.75));wet=wet*wet*(3-2*wet);k*=wet;g*=wet;if(k<=0)continue;}
                float proxyHeight=max((shaped?.8:.5)*(1+abs(slope.x)+abs(slope.y)),realHeight);
                // 倾斜圆肩不能用中心厚度判断是否充分采样；保留代理避免Cube跨顶/底。
                if(shaped && (flags&32u)!=0 && dot(slope,slope)<.0625 && realHeight>=.7/_Resolution)proxyHeight=realHeight;
                GroundPlane row=(GroundPlane)0;row.ground=supported;row.solid=supported && ((flags&2u)!=0 || (flags&4u)==0);row.coverage=k;row.floorHeight=k*floorHeight;row.realHeight=k*realHeight;row.proxyHeight=k*proxyHeight;row.rimScale=k*scale;row.slope=k*slope;
                row.gradient=g;row.floorGradient=g*floorHeight+k*slope;row.realGradient=g*realHeight;row.proxyGradient=g*proxyHeight;
                int plane=AddPlane(row,planes,planeCount);float4 rowFirst=0,rowSecond=0;
                // Vector分量写入须用固定8槽展开，不能让FXC强制展开有[loop]属性的动态材料循环。
                if(colors)[unroll]for(int material=0;material<8;material++)if(material<_MaterialCount)
                {float amount=k*ReadCell(index,material,previous).q;if(material<4)rowFirst[material&3]+=amount;else rowSecond[material&3]+=amount;}
                if(colors){planeFirst[plane]+=rowFirst;planeSecond[plane]+=rowSecond;}
            }
        }
    }
    int anchor=-1;float best=1e10;
    [loop]for(int a=0;a<planeCount;a++)if(planes[a].solid)
    {float d=abs(at.y-planes[a].floorHeight/planes[a].coverage);if(d<best){anchor=a;best=d;}}
    if(anchor>=0)
    {
        GroundPlane basis=planes[anchor],joined=(GroundPlane)0;int kept=0;
        float floor=-1e10;float2 floorGradient=0;float4 joinedFirst=0,joinedSecond=0;
        [loop]for(int b=0;b<planeCount;b++)
        {
            GroundPlane row=planes[b];
            if(row.solid && CompatiblePlane(basis,row))
            {
                float f=row.floorHeight/row.coverage;if(f>floor){floor=f;floorGradient=(row.floorGradient-f*row.gradient)/row.coverage;}
                joined.coverage+=row.coverage;joined.realHeight+=row.realHeight;joined.proxyHeight+=row.proxyHeight;joined.rimScale+=row.rimScale;
                joined.gradient+=row.gradient;joined.realGradient+=row.realGradient;joined.proxyGradient+=row.proxyGradient;
                if(colors){joinedFirst+=planeFirst[b];joinedSecond+=planeSecond[b];}
            }
            else{planes[kept]=row;if(colors){planeFirst[kept]=planeFirst[b];planeSecond[kept]=planeSecond[b];}kept++;}
        }
        // 相接面先合并成一个覆盖场，再生成共同圆肩；避免独立厚代理壳相交后产生折片。
        joined.ground=joined.solid=true;joined.floorHeight=floor*joined.coverage;
        joined.floorGradient=floor*joined.gradient+floorGradient*joined.coverage;joined.slope=floorGradient*joined.coverage;
        planes[kept]=joined;if(colors){planeFirst[kept]=joinedFirst;planeSecond[kept]=joinedSecond;}planeCount=kept+1;
    }
    // 相接坡面和平地统一采样代理，随后各自压回真实液厚，避免代理Union先造出竖墙。
    float commonProxy=0,coverage=0;float2 commonGradient=0,coverageGradient=0;
    [loop]for(int j=0;j<planeCount;j++)if(planes[j].solid)
    {GroundPlane row=planes[j];commonProxy+=row.proxyHeight;coverage+=row.coverage;commonGradient+=row.proxyGradient;coverageGradient+=row.gradient;}
    if(coverage>0){commonProxy/=coverage;commonGradient=(commonGradient-commonProxy*coverageGradient)/coverage;}
    [loop]for(int k=0;k<planeCount;k++)if(planes[k].solid)
    {GroundPlane row=planes[k];row.proxyHeight=commonProxy*row.coverage;row.proxyGradient=commonGradient*row.coverage+commonProxy*row.gradient;planes[k]=row;}
    return free;
}
float PhaseField(float3 at,bool previous)
{
    GroundPlane planes[8];float4 planeFirst[8],planeSecond[8];int planeCount;
    float result=GatherPlanes(at,previous?0:1,false,true,planes,planeFirst,planeSecond,planeCount);
    float contact=-1e10;[loop]for(int j=0;j<planeCount;j++)if(planes[j].solid)contact=max(contact,planes[j].floorHeight/planes[j].coverage);
    [loop]for(int i=0;i<planeCount;i++)
    {
        GroundPlane plane=planes[i];float floorHeight=plane.floorHeight/plane.coverage,top=plane.proxyHeight/plane.coverage;
        if(plane.solid)floorHeight=max(floorHeight,contact);
        float distance=(plane.coverage-.15)/max(.001,length(plane.gradient));float rim=saturate(distance/(1.2*plane.rimScale/plane.coverage));rim=sqrt(max(0,rim*(2-rim)));
        result=Union(result,min(distance,min(at.y-floorHeight,floorHeight+top*rim-at.y)));
    }
    return result;
}
float Field(float3 p){return lerp(PhaseField(p,true),PhaseField(p,false),_Alpha);}
void CompressGroundProxy(inout float3 p,inout float3 normal,out float swellWeight)
{
    swellWeight=0;
    GroundPlane planes[8];float4 planeFirst[8],planeSecond[8];int planeCount;
    GatherPlanes(p,-1,false,true,planes,planeFirst,planeSecond,planeCount);int chosen=-1;float best=1e10;
    [loop]for(int i=0;i<planeCount;i++)
    {GroundPlane plane=planes[i];float floorHeight=plane.floorHeight/plane.coverage,proxyHeight=plane.proxyHeight/plane.coverage;
        float distance=max(floorHeight-p.y,max(0,p.y-floorHeight-proxyHeight));if(distance<best){chosen=i;best=distance;}}
    if(chosen<0)return;
    GroundPlane plane=planes[chosen];float floorHeight=plane.floorHeight/plane.coverage,realHeight=plane.realHeight/plane.coverage,proxyHeight=plane.proxyHeight/plane.coverage;
    if(best>max(.15,proxyHeight))return;
    float weight=0,mapped=0,derivativeY=0,weightY=0,contactFloor=-1e10;float2 mappedGradient=0,weightGradient=0,contactGradient=0;int contributors=0;
    [loop]for(int candidate=0;candidate<planeCount;candidate++)
    {
        GroundPlane row=planes[candidate];if(!CompatiblePlane(plane,row))continue;contributors++;
        float f=row.floorHeight/row.coverage,real=row.realHeight/row.coverage,proxy=row.proxyHeight/row.coverage;
        float ratio=clamp(real/proxy,.00001,1),above=max(0,p.y-f),value=f+above*ratio;
        float2 fg=(row.floorGradient-f*row.gradient)/row.coverage;
        float2 rg=(row.realGradient-real*row.gradient)/row.coverage,pg=(row.proxyGradient-proxy*row.gradient)/row.coverage;
        float2 ratioGradient=(rg*proxy-real*pg)/(proxy*proxy);
        float distance=max(f-p.y,max(0,p.y-f-proxy)),denominator=1+16*distance*distance;
        float2 dg=p.y<f?fg:p.y>f+proxy?-fg-pg:float2(0,0);float dy=p.y<f?-1:p.y>f+proxy?1:0;
        float w=row.coverage/denominator,wy=-row.coverage*32*distance*dy/(denominator*denominator);
        float2 wg=row.gradient/denominator-row.coverage*32*distance*dg/(denominator*denominator);
        float2 vg=p.y>f?(1-ratio)*fg+above*ratioGradient:fg;
        weight+=w;mapped+=w*value;weightGradient+=wg;mappedGradient+=w*vg+wg*value;
        derivativeY+=w*(p.y>f?ratio:0)+wy*value;weightY+=wy;
        if(row.solid && row.ground && f>contactFloor){contactFloor=f;contactGradient=fg;}
    }
    float height=mapped/weight,scaleY=max(.00001,(derivativeY-height*weightY)/weight);float2 slope=(mappedGradient-height*weightGradient)/weight;
    [loop]for(int barrierIndex=0;barrierIndex<planeCount;barrierIndex++)
    {GroundPlane barrier=planes[barrierIndex];if(!barrier.solid)continue;float f=barrier.floorHeight/barrier.coverage;
        if(f>height && f-height<1.5 && f>contactFloor){contactFloor=f;contactGradient=(barrier.floorGradient-f*barrier.gradient)/barrier.coverage;}}
    // 连续形变及其Jacobian；地板取接触平面包络，不在Kernel进入时硬切换支撑。
    if(height<contactFloor){height=contactFloor;slope=contactGradient;scaleY=1;}
    normal=normalize(float3(normal.x-normal.y*slope.x/scaleY,normal.y/scaleY,normal.z-normal.y*slope.y/scaleY));p.y=height+.0002;
    if(contactFloor< -1e9)contactFloor=floorHeight;
    float2 floorGradient=(plane.floorGradient-floorHeight*plane.gradient)/plane.coverage;
    float2 realGradient=(plane.realGradient-realHeight*plane.gradient)/plane.coverage;
    // 接触边缘保持固定；GPU与CPU均把同一衰减系数传给Vertex阶段。
    float contact=saturate((p.y-contactFloor)*_OriginSize.w/.12);swellWeight=contact*contact*(3-2*contact);
    if(_CrownStrength>.15 && contributors==1 && dot(floorGradient,floorGradient)<.0625)
    {
        // 平地圆肩解析Normal，陡坡保留Jacobian法线；CPU采用同一适用边界。
        float rimWidth=1.2*plane.rimScale/plane.coverage;
        float edge=(plane.coverage-.15)/max(.001,length(plane.gradient)),u=saturate(edge/rimWidth),rim=sqrt(max(0,u*(2-u)));
        // 按场梯度朝向选择顶面；低侧壁不能按高度阈值硬设为朝下的底面Normal。
        // 其余顶点保留inverse transpose结果，避免插值光照出现规则三角暗齿。
        if(normal.y>0)
        {float2 rimGradient=u>0 && u<1?plane.gradient/max(.001,length(plane.gradient))*((1-u)/max(.001,rimWidth*rim)):float2(0,0);
            float2 topGradient=floorGradient+realGradient*rim+realHeight*rimGradient;normal=normalize(float3(-topGradient.x,1,-topGradient.y));}
    }
    if(_CrownStrength>.15 && plane.solid)
    {
        // 与CPU一致：把接地的封闭底壳轻微藏入Collider，消除坡脚底面露出的薄齿。
        // 显示顶面、层身份和GMU保持原规则；最大嵌入2cm，不用于掩盖大范围穿透。
        float3 supportNormal=normalize(float3(-contactGradient.x,1,-contactGradient.y));
        float underside=saturate(-dot(normal,supportNormal)/.25);underside=underside*underside*(3-2*underside);
        float inset=min(.02/_OriginSize.w,realHeight*.05)*underside*(1-swellWeight);
        p-=supportNormal*inset;
    }
}
void MaterialWeights(float3 p,out float4 first,out float4 second)
{
    GroundPlane planes[8];float4 planeFirst[8],planeSecond[8];int planeCount;
    GatherPlanes(p,-1,true,false,planes,planeFirst,planeSecond,planeCount);float best=1e10,chosenFloor=0,chosenTop=1;int chosen=-1;
    [loop]for(int i=0;i<planeCount;i++)
    {GroundPlane plane=planes[i];float floorHeight=plane.floorHeight/plane.coverage,top=plane.realHeight/plane.coverage;
        float distance=max(floorHeight-p.y,max(0,p.y-floorHeight-top));if(distance>=best)continue;best=distance;chosen=i;chosenFloor=floorHeight;chosenTop=top;}
    first=0;second=0;if(chosen<0)return;
    float4 a=planeFirst[chosen],b=planeSecond[chosen];float allAmount=dot(a,1)+dot(b,1);if(allAmount<=0)return;
    float local=saturate((p.y-chosenFloor)/max(.001,chosenTop)),bottom=0;
    [loop]for(int groupIndex=0;groupIndex<_GroupCount;groupIndex++)
    {
        float group=0;[unroll]for(int material=0;material<8;material++)if(material<_MaterialCount && GroupAt(material)==groupIndex)group+=material<4?a[material&3]:b[material&3];
        if(group<=0)continue;float top=bottom+group/allAmount;
        if(local<=top || groupIndex==_GroupCount-1)
        {[unroll]for(int outputSlot=0;outputSlot<8;outputSlot++)if(outputSlot<_MaterialCount && GroupAt(outputSlot)==groupIndex)
            {if(outputSlot<4)first[outputSlot&3]=a[outputSlot&3]/group;else second[outputSlot&3]=b[outputSlot&3]/group;}return;}
        bottom=top;
    }
}
