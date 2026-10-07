using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    /// <summary>实际绘制生产 Splat Shader，读取 Pixel Coverage；不能只检查材质或 Shader 字符串。</summary>
    public sealed class GpuFireParcelSplatTests
    {
        [UnityTest]
        public IEnumerator UniformFlamesRemainVisibleAcrossFormerNearBoundary()
        {
            using (var draw = new SplatDraw())
            {
                yield return draw.Read(Vector3.zero, Vector3.zero);
                float near = draw.Energy;
                Assert.That(near, Is.GreaterThan(500), "统一模式下，8m Near 内的年轻火苗也必须真正产生像素。");
                yield return draw.Read(Vector3.right * 24, Vector3.zero);
                Assert.That(draw.Energy, Is.EqualTo(near).Within(1), "移开旧局部网格中心不应再改变同一火苗的外观。");
            }
        }

        [UnityTest]
        public IEnumerator DistantFadeIsWideAndContinuous()
        {
            using (var draw = new SplatDraw())
            {
                yield return draw.Read(Vector3.right * 24, Vector3.zero);
                float full = draw.Energy;
                Assert.That(full, Is.GreaterThan(500));
                yield return draw.Read(Vector3.right * 24, Vector3.right * 32);
                Assert.That(draw.Energy, Is.EqualTo(full).Within(1), "32m内应保持完整效果。");
                yield return draw.Read(Vector3.right * 24, Vector3.right * 44);
                Assert.That(draw.Energy / full, Is.EqualTo(.5f).Within(.035f), "24m宽过渡的中点应平滑减半，不能硬切。");
                yield return draw.Read(Vector3.right * 24, Vector3.right * 56);
                Assert.That(draw.Energy, Is.Zero, "56m外完成视觉淡出，Gameplay不受这条视觉曲线影响。");
            }
        }

        [Test]
        public void ProductionDrawBoundsEncloseTheWholeFadeRange()
        {
            var profile = AssetDatabase.LoadAssetAtPath<GpuFireParcelRenderProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset");
            var phase = profile.CreatePhaseSettings();
            float quadExtent = Mathf.Max(phase.InitialRadius, phase.FinalRadius, profile.SurfaceCohesionRadius * .75f)
                * (Mathf.Max(profile.SurfaceVerticalStretch, profile.GasVerticalStretch) + .25f);
            Assert.That(profile.DrawBoundsRadius, Is.GreaterThanOrEqualTo(56f + quadExtent),
                "Indirect Draw的CPU Bounds必须覆盖完整远景，不能仍按8m局部网格裁剪。");
        }

        [UnityTest]
        public IEnumerator BothFlamePhasesKeepCoverageInSteepAndTopViews()
        {
            using (var draw = new SplatDraw())
            {
                for (int phase = 0; phase < 2; phase++)
                {
                    draw.SetView(0, phase, 0);
                    yield return draw.Read(Vector3.zero, Vector3.zero);
                    float front = draw.Energy;
                    Assert.That(front, Is.GreaterThan(150));
                    foreach (int pitch in new[] { 60, 80, 90 })
                    {
                        draw.SetView(pitch, phase, 0);
                        yield return draw.Read(Vector3.zero, Vector3.zero);
                        Assert.That(draw.Energy, Is.GreaterThan(front * .4f), "俯视不能把竖直火舌压成几条近零面积的线。");
                        if (pitch == 90)
                            Assert.That(draw.Height / (float)draw.Width, Is.InRange(.8f, 1.25f), "正俯视应是饱满横截面，而不是薄片。");
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator StraightDownProjectionRemainsStableWithCameraRoll()
        {
            using (var draw = new SplatDraw())
            {
                draw.SetView(90, 0, 0);
                yield return draw.Read(Vector3.zero, Vector3.zero);
                float full = draw.Energy;
                Assert.That(full, Is.GreaterThan(300));
                foreach (int roll in new[] { 45, 90 })
                {
                    draw.SetView(90, 0, roll);
                    yield return draw.Read(Vector3.zero, Vector3.zero);
                    Assert.That(draw.Energy, Is.EqualTo(full).Within(full * .1f), "Camera Right/Up在正俯视或Roll时不能归一化一个零向量。");
                }
            }
        }

        [System.Serializable] private struct ViewPixels
        { public string Utc; public int Phase,Pitch,Roll,Width,Height; public float Energy; }

        private sealed class SplatDraw : System.IDisposable
        {
            private readonly GraphicsBuffer _states;
            private readonly Material _material;
            private readonly RenderTexture _target;
            private readonly Camera _camera;
            private int _phase, _pitch, _roll;
            public float Energy;
            public int Width, Height;

            public SplatDraw()
            {
                if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                    Assert.Ignore("需要实际GPU绘制与AsyncGPUReadback。");
                _states = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, FireParcelGpuLayout.StateStride);
                _states.SetData(new[] { new FireParcelStateGpu
                    { Position=Vector3.zero, SourcePosition=Vector3.zero, Radius=.35f, SurfaceWeight=1, GasWeight=1, Heat=1, Active=1 } });
                _material = new Material(Shader.Find("Game/Elemental/Fire Parcel Splat"));
                _material.SetBuffer("_FireParcelStates", _states);
                _material.SetFloat("_PhaseMode", 0);
                _material.SetFloat("_UseLocalSurface", 0);
                _material.SetFloat("_SurfaceAlphaScale", .65f);
                _material.SetFloat("_GasAlphaScale", .65f);
                _material.SetFloat("_SurfaceRadiusMultiplier", .9f);
                _material.SetFloat("_VerticalStretch", 1.9f);
                _material.SetFloat("_CohesionRadius", .6f);
                _material.SetFloat("_Softness", .72f);
                _material.SetColor("_CoreColor", Color.white);
                _material.SetColor("_EdgeColor", Color.white);
                _material.SetVector("_SurfaceLodHalfSize", Vector3.one * 8);
                _material.SetFloat("_SurfaceLodBlendWidth", 1.5f);
                _material.SetFloat("_VisibilityFadeStart", 32);
                _material.SetFloat("_VisibilityFadeEnd", 56);
                _target = new RenderTexture(128,128,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                _target.Create();
                _camera = new GameObject("Splat pixel regression camera").AddComponent<Camera>();
                _camera.enabled = false;
                _camera.transform.position = new Vector3(0,0,-3);
                _camera.orthographic = true;
                _camera.orthographicSize = 1;
                _camera.aspect = 1;
                _camera.nearClipPlane = .1f;
                _camera.farClipPlane = 100;
            }

            public void SetView(int pitch, int phase, int roll)
            {
                _pitch=pitch; _phase=phase; _roll=roll;
                _material.SetFloat("_PhaseMode", phase);
                float radius = phase == 0 ? .45f : .35f * .78f;
                _camera.transform.rotation = Quaternion.Euler(pitch,0,roll);
                _camera.transform.position = Vector3.up * radius * .25f - _camera.transform.forward * 3;
            }

            public IEnumerator Read(Vector3 lodCenter, Vector3 visibilityCenter)
            {
                _material.SetVector("_SurfaceLodCenter", lodCenter);
                _material.SetVector("_VisibilityCenter", visibilityCenter);
                using (var commands = new CommandBuffer())
                {
                    commands.SetRenderTarget(_target);
                    commands.ClearRenderTarget(false, true, Color.black);
                    commands.SetViewProjectionMatrices(_camera.worldToCameraMatrix,
                        GL.GetGPUProjectionMatrix(_camera.projectionMatrix,true));
                    commands.SetGlobalVector("_WorldSpaceCameraPos", _camera.transform.position);
                    commands.SetGlobalVector("unity_OrthoParams", new Vector4(2,2,0,1));
                    commands.SetGlobalVector("_ScaledScreenParams", new Vector4(128,128,1+1f/128,1+1f/128));
                    // Depth Fade读取的是Eye Depth（沿视线的距离）。提供远平面深度，避免测试画布遮住火苗。
                    commands.SetGlobalTexture("_CameraDepthTexture", SystemInfo.usesReversedZBuffer ? Texture2D.blackTexture : Texture2D.whiteTexture);
                    commands.SetGlobalVector("_ZBufferParams", SystemInfo.usesReversedZBuffer
                        ? new Vector4(999,1,9.99f,.01f) : new Vector4(-999,1000,-9.99f,10));
                    commands.DrawProcedural(Matrix4x4.identity,_material,0,MeshTopology.Triangles,6,1);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                var read = AsyncGPUReadback.Request(_target,0,TextureFormat.RGBA32);
                while (!read.done) yield return null;
                Assert.That(read.hasError, Is.False);
                Energy = 0;
                int minX=128,minY=128,maxX=-1,maxY=-1;
                var pixels = read.GetData<Color32>();
                for (int i=0;i<pixels.Length;i++)
                {
                    Color32 pixel=pixels[i]; Energy+=pixel.g/255f;
                    if (pixel.g <= 6) continue;
                    int x=i%128,y=i/128;
                    minX=Mathf.Min(minX,x); maxX=Mathf.Max(maxX,x);
                    minY=Mathf.Min(minY,y); maxY=Mathf.Max(maxY,y);
                }
                Width=Mathf.Max(0,maxX-minX+1); Height=Mathf.Max(0,maxY-minY+1);
                // 只在显式GPU测试里写证据，不进入生产Renderer或Profiler窗口。
                var metrics=new ViewPixels { Utc=System.DateTime.UtcNow.ToString("O"), Phase=_phase,
                    Pitch=_pitch,Roll=_roll,Width=Width,Height=Height,Energy=Energy };
                System.IO.File.WriteAllText("Temp/ElementPerformance/fire-view-"+_phase+"-"+_pitch+"-"+_roll+".json",
                    JsonUtility.ToJson(metrics,true));
            }

            public void Dispose()
            {
                _states.Dispose();
                _target.Release();
                Object.DestroyImmediate(_target);
                Object.DestroyImmediate(_material);
                Object.DestroyImmediate(_camera.gameObject);
            }
        }
    }
}
