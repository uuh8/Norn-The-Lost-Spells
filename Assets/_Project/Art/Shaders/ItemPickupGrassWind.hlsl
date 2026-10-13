#ifndef NORN_ITEM_PICKUP_GRASS_WIND
#define NORN_ITEM_PICKUP_GRASS_WIND
// 适配现有ANGRYMESH/Grass的Vertex wind。三层必须使用同一全局风、顶点色和时间，
// 否则遮罩在旧姿态上，会给正在摆动的叶片内部着色。原材质与全局风控制器保持原样。
sampler2D ASPW_WindGrassWavesNoiseTexture;
half3 ASPW_WindDirection;
half ASPW_WindToggle,ASPW_WindGrassSpeed,ASPW_WindGrassAmplitude,ASPW_WindGrassFlexibility;
half ASPW_WindGrassWavesAmplitude,ASPW_WindGrassWavesSpeed,ASPW_WindGrassWavesScale,ASPW_WindGrassTurbulence;
float3 PickupGrassPosition(float3 positionOS,half4 vertexColor)
{
    if(_PickupGrassWind<.5)return positionOS;
    // 按源Shader的float Normalize和half中间值/括号顺序计算；大时间相位下重排sin乘法会错位。
    float3 inputDirection=ASPW_WindDirection;
    half3 direction=inputDirection*rsqrt(max(1.175494351e-38,dot(inputDirection,inputDirection)));
    half3 localDirection=mul(GetWorldToObjectMatrix(),float4(direction,0)).xyz;
    half3 axis=half3(localDirection.z,0,-localDirection.x);
    float3 objectPosition=GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23);
    half randomization=frac((objectPosition.x+objectPosition.y+objectPosition.z)*1.23);
    half phase=randomization+_TimeParameters.x;
    half speed=_WindGrassSpeed*ASPW_WindGrassSpeed*10;
    half amplitude=_WindGrassAmplitude*ASPW_WindGrassAmplitude;
    half main=((sin(phase*(speed*1.0))+sin(phase*(speed*1.25))+sin(phase*(speed*1.5))+amplitude)*amplitude/50.0)*((ASPW_WindGrassFlexibility*_WindGrassFlexibility)*.1);
    float3 positionWS=TransformObjectToWorld(positionOS);
    half3 wavesDirection=half3(-direction.x,0,0);
    half2 worldXZ=half2(positionWS.x,positionWS.z);
    float2 wavesUV=((wavesDirection*_TimeParameters.x)*(ASPW_WindGrassWavesSpeed*.1)+half3((worldXZ*ASPW_WindGrassWavesScale)*.1,0)).xy;
    half waves=tex2Dlod(ASPW_WindGrassWavesNoiseTexture,float4(wavesUV,0,0)).r;
    main=lerp(main,main*ASPW_WindGrassWavesAmplitude,waves);
    half turbulencePhase=positionWS.y*(_WindGrassScale*10)+_TimeParameters.x;
    half turbulenceSpeed=ASPW_WindGrassSpeed*_WindGrassSpeed*10.0;
    half clampedAmplitude=clamp(ASPW_WindGrassAmplitude,0.0,1.0);
    half turbulence=((sin(turbulencePhase*(turbulenceSpeed*1.0))+sin(turbulencePhase*(turbulenceSpeed*1.25)))*.5)*((ASPW_WindGrassTurbulence*(clampedAmplitude*(_WindGrassTurbulence*_WindGrassAmplitude)))*.1);
    // Rodrigues矩阵与源Shader一致，保留其Object Space轴与缩放语义。
    half angleHalf=main+turbulence;
    float angle=angleHalf,c=cos(angle),s=sin(angle),t=1-c;
    float3x3 rotation=float3x3(t*axis.x*axis.x+c,t*axis.x*axis.y-s*axis.z,t*axis.x*axis.z+s*axis.y,
        t*axis.x*axis.y+s*axis.z,t*axis.y*axis.y+c,t*axis.y*axis.z-s*axis.x,
        t*axis.x*axis.z-s*axis.y,t*axis.y*axis.z+s*axis.x,t*axis.z*axis.z+c);
    half3 displacement=mul(rotation,positionOS)-positionOS;
    half3 offset=lerp(float3(0,0,0),displacement*vertexColor.r,ASPW_WindToggle);
    return positionOS+offset;
}
#endif
