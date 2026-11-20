// 现有小滴模板的接地球冠。code负号是instance形状标记，不是假的Gameplay速度。
// 与GridLiquidGroundContactShape.UnitPosition一致；底盘仍是闭合几何，体积按同一模板校准。
float GroundContactHeightRatio(float code){return .12+.8*(clamp(code,1,256)-1)/255;}
void GroundContactVertex(float3 direction,float radius,float code,out float3 offset,out float3 normal)
{
    float k=GroundContactHeightRatio(code),cosine=1-k;
    if(direction.y<0)
    {offset=float3(direction.x*sqrt(k*(2-k)),0,direction.z*sqrt(k*(2-k)))*radius;normal=float3(0,-1,0);return;}
    float c=cosine+k*direction.y,radial=sqrt(max(0,1-c*c)),planar=length(direction.xz);
    float2 radialDirection=planar>.000001?direction.xz/planar:float2(0,0);
    offset=float3(radialDirection.x*radial,k*direction.y,radialDirection.y*radial)*radius;
    normal=normalize(float3(radialDirection.x*radial,c,radialDirection.y*radial));
}
