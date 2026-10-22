using System.Collections.Generic;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 浅层卡通液体的显示轮廓。覆盖率决定圆润岸线，主体厚度受真实填充量限制，
    /// 圆冠保留有界艺术夸张，不改变 GMU；动态和稳定岸线复用同一截面。
    /// </summary>
    internal static class LiquidPuddleShape
    {
        internal const float EdgeHeightCells=.005f;
        // 连续场的半径小于 1.5 Cell，九邻格足够；仍留住单格真孔洞的中心。
        internal const float KernelRadiusSquared=2.1025f; // 1.45²
        internal const float CoverageThreshold=.65f;
        internal const float ShoulderWidthCells=1.2f;
        internal const float CrownStrength=.65f;

        internal static float BodyHeight(float fill, float referenceDepthCells)
        {
            // 最小厚度是艺术参考，不是无条件保底：少量水不能画成与满格同高。
            // 主体最多比实际水深高15%，圆冠仍保留；减少液量时整个液团会随之变薄。
            return fill;
        }

        internal static float CrownHeight(float bodyHeightCells) =>
            Mathf.Min(.15f*bodyHeightCells,CrownStrength*Mathf.Min(bodyHeightCells,Mathf.Max(0f,1f-bodyHeightCells)));

        // 宽幅圆冠先在整数采样点卷积，再做 Bicubic 插值；每个粗点只算一次。
        // 它作用于整个浅滩，避免短圆肩后立即接水平平台；完整湖面支持度仍为 1。
        internal static float CrownKernel(int x,int z)
        {
            // 采样点位于格角，源 Cell 的中心是 offset+0.5；符号错误会把圆冠平移一格。
            float dx=x+.5f,dz=z+.5f;
            float w=Mathf.Max(0f,1f-(dx*dx+dz*dz)/6.25f);
            return w*w*w/4.93056f;
        }
        internal static float Cubic(float a,float b,float c,float d,float t) =>
            b+.5f*t*(c-a+t*(2f*a-5f*b+4f*c-d+t*(3f*(b-c)+d-a)));
        internal static float CrownSupport(float x,float z,System.Func<int,int,float> sample)
        {
            int ix=Mathf.FloorToInt(x),iz=Mathf.FloorToInt(z);
            float tx=x-ix,tz=z-iz;
            float a=Cubic(sample(ix-1,iz-1),sample(ix,iz-1),sample(ix+1,iz-1),sample(ix+2,iz-1),tx);
            float b=Cubic(sample(ix-1,iz),sample(ix,iz),sample(ix+1,iz),sample(ix+2,iz),tx);
            float c=Cubic(sample(ix-1,iz+1),sample(ix,iz+1),sample(ix+1,iz+1),sample(ix+2,iz+1),tx);
            float d=Cubic(sample(ix-1,iz+2),sample(ix,iz+2),sample(ix+1,iz+2),sample(ix+2,iz+2),tx);
            float support=Mathf.Clamp01(Cubic(a,b,c,d,tz)*1.08f);
            // 总高度上限收紧后，用平方把宽冠曲率集中在中央；不增加峰值或GMU。
            return support*support;
        }

        internal static float Kernel(float x,float z,out Vector2 gradient)
        {
            float w=Mathf.Max(0f,1f-(x*x+z*z)/KernelRadiusSquared);
            float square=w*w;
            // 三次紧支撑 Kernel 的解析偏导数。梯度指向覆盖率增加的方向，
            // 可修正轮廓交点，也避免把矩形距离的折脊传入圆肩。
            gradient=new Vector2(x,z)*(-6f*square/KernelRadiusSquared);
            return square*w;
        }

        internal static float ShoreDistance(float coverage,Vector2 gradient)
        {
            // 一阶距离近似：标量场到零面的距离约为 value/|gradient|。
            // 平坦内部梯度很小，距离截断到圆肩宽度，继续走合并平面的快路径。
            return coverage<=0f?0f:Mathf.Min(ShoulderWidthCells,
                coverage/Mathf.Max(.0001f,gradient.magnitude));
        }

        internal static float RefineEdge(float t,float coverage,Vector2 gradient,Vector2 edge)
        {
            float derivative=Vector2.Dot(gradient,edge);
            return Mathf.Abs(derivative)>1e-6f?Mathf.Clamp01(t-coverage/derivative):t;
        }

        internal static Vector2 ProjectContour(Vector2 point,float coverage,Vector2 gradient,
            Vector2 minimum,Vector2 maximum)
        {
            float square=gradient.sqrMagnitude;
            if(square>1e-8f)point-=gradient*(coverage/square);
            // 中点限制在原线段端点的 XZ 包围框内，不跨到相邻网格或越过真实空洞。
            return Vector2.Min(maximum,Vector2.Max(minimum,point));
        }

        internal static void Triangulate(Vector3[] polygon,int count,int first,List<int> indices,int[] scratch)
        {
            // 曲线中点可能形成凹多边形，三角扇会翻面。Ear Clipping 每次裁掉
            // 不包含其它顶点的凸角；最多八点，复用索引数组，不分配热路径对象。
            for(int i=0;i<count;i++)scratch[i]=i;
            int remaining=count;
            while(remaining>3)
            {
                bool removed=false;
                for(int i=0;i<remaining;i++)
                {
                    int a=scratch[(i+remaining-1)%remaining],b=scratch[i],c=scratch[(i+1)%remaining];
                    float area=UpArea(polygon[a],polygon[b],polygon[c]);
                    if(area<=1e-10f)continue;
                    bool contains=false;
                    for(int j=0;j<remaining;j++)
                    {
                        int p=scratch[j];if(p==a || p==b || p==c)continue;
                        if(UpArea(polygon[a],polygon[b],polygon[p])>=-1e-10f
                            && UpArea(polygon[b],polygon[c],polygon[p])>=-1e-10f
                            && UpArea(polygon[c],polygon[a],polygon[p])>=-1e-10f)
                        {contains=true;break;}
                    }
                    if(contains)continue;
                    indices.Add(first+a);indices.Add(first+b);indices.Add(first+c);
                    for(int j=i;j<remaining-1;j++)scratch[j]=scratch[j+1];
                    remaining--;removed=true;break;
                }
                if(removed)continue;
                // 完全共线的轮廓中点无需单独顶面三角形；裙边仍保留该点。
                // 若输入已退化，停止发射，不能回退成穿过孔洞的翻面三角扇。
                for(int i=0;i<remaining;i++)
                {
                    int a=scratch[(i+remaining-1)%remaining],b=scratch[i],c=scratch[(i+1)%remaining];
                    if(Mathf.Abs(UpArea(polygon[a],polygon[b],polygon[c]))>1e-10f)continue;
                    for(int j=i;j<remaining-1;j++)scratch[j]=scratch[j+1];
                    remaining--;removed=true;break;
                }
                if(!removed)return;
            }
            if(remaining==3 && UpArea(polygon[scratch[0]],polygon[scratch[1]],polygon[scratch[2]])>1e-10f)
            {indices.Add(first+scratch[0]);indices.Add(first+scratch[1]);indices.Add(first+scratch[2]);}
        }

        private static float UpArea(Vector3 a,Vector3 b,Vector3 c) =>
            (b.z-a.z)*(c.x-a.x)-(b.x-a.x)*(c.z-a.z);

        internal static float Rim(float shoreDistanceCells)
        {
            float t=Mathf.Clamp01(shoreDistanceCells/ShoulderWidthCells);
            // 椭圆的上半弧：外侧较直立，向内斜率逐渐减小，平滑接上内部顶面。
            // 高度和宽度分别缩放，得到饱满圆肩而不是贴地再突然抬高的 S 形坡。
            return Mathf.Sqrt(Mathf.Max(0f,t*(2f-t)));
        }

        internal static float Height(float fill,float shoreDistanceCells,float minimumDepthCells,
            float crownSupport=0f)
        {
            float top=fill;
            float rim=Rim(shoreDistanceCells);
            return Mathf.Lerp(Mathf.Min(EdgeHeightCells,top*.1f),top,rim)
                // 接近满一格时圆冠逐渐缩小，峰值不会先跨进下一液层；避免看着满槽
                // 却还需要补很多水。连续的余量函数也消除跨过参考厚度时的高度跳变。
                + CrownHeight(top)*crownSupport*rim*rim;
        }
    }
}
