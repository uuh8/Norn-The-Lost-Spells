using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>完整 Authority Tick，包含碰撞、反应、休眠和变化发布。规模核对在计时窗口之外，
    /// 没有摄像机或网络传输；两者分别由 Native Player 和复制实验计量。</summary>
    public sealed class CellularAuthorityScaleTests
    {
        [Serializable] private sealed class Sample
        {
            public int lakeSide, seedSide, seedCells, ticks, simultaneousTicks, peakActive, peakResident, endActive;
            public long lakeCells, movedWater, movedSand, movedSteam, initialWater, finalWater, initialSand, finalSand, initialSteam, finalSteam, consumedSteam;
            public double medianMs, p95Ms, maxMs, preparationMeanMs, transportMeanMs, commitMeanMs;
            public double[] tickMs, transportMs, commitMs;
        }
        [Serializable] private sealed class Report { public string utc, cpu; public Sample[] cases; }
        private struct Amounts { public uint Water, Sand, Steam; }
        private const string Root = "Assets/_Project/ScriptableObjects/";
        private const int Ticks = 80, Warmup = 8;

        [Test]
        public void SameMovingFrontInTwoWorldSizesAndAtHigherLoad()
        {
            var report = new Report { utc = DateTime.UtcNow.ToString("O"), cpu = SystemInfo.processorType,
                cases = new[] { Measure(256, 0), Measure(32, 18), Measure(256, 18), Measure(256, 42) } };
            Directory.CreateDirectory("Temp/ElementWorldScale");
            File.WriteAllText("Temp/ElementWorldScale/authority-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json", JsonUtility.ToJson(report, true));
            foreach (var sample in report.cases)
            {
                Assert.AreEqual(sample.initialWater, sample.finalWater);
                Assert.AreEqual(sample.initialSand, sample.finalSand);
                Assert.AreEqual(sample.initialSteam, sample.finalSteam + sample.consumedSteam);
                if (sample.seedSide != 0) Assert.AreEqual(Ticks, sample.simultaneousTicks,
                    "连续下落期间每个实测 Tick 都应包含三类物质的真实再分配。");
            }
            // 只有远处封闭湖的大小改变。活动区采用相同的初态、顺序和种子，检查行为是否一致。
            var small = report.cases[1]; var large = report.cases[2];
            Assert.AreEqual(small.movedWater, large.movedWater);
            Assert.AreEqual(small.movedSand, large.movedSand);
            Assert.AreEqual(small.movedSteam, large.movedSteam);
        }

        private static Sample Measure(int lakeSide, int side)
        {
            var data = new Sample { lakeSide = lakeSide, lakeCells = 16L * lakeSide * lakeSide, seedSide = side, ticks = Ticks };
            var host = new GameObject("Temporary cellular authority load"); host.SetActive(false);
            var profile = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(Root + "ElementField/ElementWorldProfile_Cellular.asset"));
            var cells = Object.Instantiate(profile.CellularWorld);
            var times = new double[Ticks];
            data.transportMs = new double[Ticks];
            data.commitMs = new double[Ticks];
            var changed = new List<Vector3Int>(65536);
            var previous = new Dictionary<Vector3Int, Amounts>(65536);
            var world = host.AddComponent<ElementWorldRuntime>();
            int sampleIndex = -1;
            void Timing(ElementWorldRuntime source, double ms) { if (source == world && sampleIndex >= 0) times[sampleIndex] = ms; }
            try
            {
                Set(profile, "_cellularWorld", cells);
                // 同一前景和密封蓄水层，后者由一个水盒以及五面岩石组成。空中材料在真实重力下
                // 落向独立地板，前景相邻的水和沙会接触，蒸汽保留气体阈值和配置中的时间预算。
                const int x = 128;
                ElementTerrainRegion Rock(int a,int b,int c,int w,int h,int d) =>
                    new ElementTerrainRegion { Cells = new BoundsInt(a,b,c,w,h,d), Material = MaterialId.Rock };
                Set(cells, "_terrain", new[] { Rock(-64,-8,-64,128,8,128), Rock(x,-16,0,lakeSide,16,lakeSide),
                    Rock(x-1,0,0,1,16,lakeSide), Rock(x+lakeSide,0,0,1,16,lakeSide),
                    Rock(x,0,-1,lakeSide,16,1), Rock(x,0,lakeSide,lakeSide,16,1) });
                Set(cells, "_restingLiquids", new[] { new CellularRestingLiquidRegion { Cells = new BoundsInt(x,0,0,lakeSide,16,lakeSide), Material = MaterialId.Water } });
                Set(world, "_profile", profile);
                var gas = host.AddComponent<GasWorldRuntime>();
                Set(gas, "_profile", AssetDatabase.LoadAssetAtPath<GasWorldProfile>(Root + "World/Online/Materials/P7_OnlineCPU_GasWorldProfile.asset"));
                Set(world, "_gasRuntime", gas);
                Set(world, "_conversionProfile", AssetDatabase.LoadAssetAtPath<MaterialConversionProfile>(Root + "ElementField/Phases/MaterialConversions_Default.asset"));
                world.ConfigureExecutionMode(WorldExecutionMode.Authority);
                host.SetActive(true);
                if (!world.IsInitialized)
                    Assert.IsTrue((bool)typeof(ElementWorldRuntime).GetMethod("TryInitialize", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(world, null));
                Assert.IsTrue(world.UsesCellularWorld && world.CellularSimulation.HasGasEnvironment);
                for (int z = 0; z < side; z++)
                    for (int a = 0; a < side; a++)
                        for (int y = 0; y < 9; y++)
                        {
                            var kind = y < 3 ? MaterialId.Water : y < 6 ? MaterialId.Sand : MaterialId.Steam;
                            var pos = new Vector3Int(a-side/2, 60+y, z-side/2);
                            Assert.IsTrue(world.CellularSimulation.TryDeposit(pos, kind, 128));
                            world.NotifyExternalMaterialInjection(kind,128);
                            previous[pos] = Read(world, pos);
                            data.seedCells++;
                        }
                world.PublishCellularChanges();
                data.initialWater = Total(world, MaterialId.Water); data.initialSand = Total(world, MaterialId.Sand); data.initialSteam = Total(world, MaterialId.Steam);
                Assert.AreEqual(data.lakeCells * cells.Capacity + (long)side*side*3*128, data.initialWater);
                world.CellularContentsChanged += changed.Add;
                ElementWorldRuntime.FrameBenchmarkTickCompleted += Timing;
                for (int step = 0; step < Warmup + Ticks; step++)
                {
                    changed.Clear(); sampleIndex = step-Warmup;
                    world.StepAuthority(world.CellularTickDuration, (uint)step+1);
                    Assert.AreEqual(1,world.LastFrameTickCount);
                    long water = 0, sand = 0, steam = 0;
                    foreach (var pos in changed)
                    {
                        previous.TryGetValue(pos, out var before);
                        var now = Read(world, pos);
                        water += Math.Max(0L,(long)now.Water-before.Water);
                        sand += Math.Max(0L,(long)now.Sand-before.Sand);
                        steam += Math.Max(0L,(long)now.Steam-before.Steam);
                        if ((now.Water | now.Sand | now.Steam) != 0) previous[pos] = now; else previous.Remove(pos);
                    }
                    if (sampleIndex < 0) continue;
                    if (water>0 && sand>0 && steam>0) data.simultaneousTicks++;
                    data.movedWater += water; data.movedSand += sand; data.movedSteam += steam;
                    data.peakActive = Math.Max(data.peakActive,world.CellularActiveCount);
                    data.peakResident = Math.Max(data.peakResident,world.CellularOccupiedCount);
                    data.preparationMeanMs += world.MeasuredPreparationMs/Ticks;
                    data.transportMeanMs += world.MeasuredTransportMs/Ticks;
                    data.commitMeanMs += world.MeasuredCommitMs/Ticks;
                    data.transportMs[sampleIndex] = world.MeasuredTransportMs;
                    data.commitMs[sampleIndex] = world.MeasuredCommitMs;
                }
                data.endActive = world.CellularActiveCount;
                // 保留时间顺序，便于把长尾帧对应到传输或碰撞提交。
                data.tickMs = (double[])times.Clone();
                Array.Sort(times); data.medianMs=times[Ticks/2]; data.p95Ms=times[(int)Math.Ceiling(Ticks*.95)-1]; data.maxMs=times[Ticks-1];
                data.finalWater = Total(world, MaterialId.Water); data.finalSand = Total(world, MaterialId.Sand); data.finalSteam = Total(world, MaterialId.Steam);
                data.consumedSteam = (long)world.ConversionLedger.ExplicitlyConsumed(MaterialId.Steam);
                // 挖掘与回填应使负坐标页中本格的缓存立即失效，同页邻格仍然可读。
                var bedrock = new Vector3Int(-5, -5, -5);
                Assert.IsTrue(world.IsSolid(bedrock));
                Assert.IsTrue(world.IsSolid(bedrock + Vector3Int.left));
                Assert.IsTrue(world.TrySetTerrainMaterial(bedrock, MaterialId.Empty));
                Assert.IsFalse(world.IsSolid(bedrock));
                Assert.IsTrue(world.IsSolid(bedrock + Vector3Int.left));
                Assert.IsTrue(world.TrySetTerrainMaterial(bedrock, MaterialId.Rock));
                Assert.IsTrue(world.IsSolid(bedrock));
                return data;
            }
            finally
            {
                ElementWorldRuntime.FrameBenchmarkTickCompleted -= Timing; world.CellularContentsChanged -= changed.Add;
                Object.DestroyImmediate(host); Object.DestroyImmediate(cells); Object.DestroyImmediate(profile);
            }
        }

        private static long Total(ElementWorldRuntime w, MaterialId m) =>
            checked((long)(w.CellularSimulation.GetTotalQuantity(m) + w.RestingLiquids.GetTotalQuantity(m)));
        private static Amounts Read(ElementWorldRuntime w, Vector3Int pos)
        {
            w.TryGetCellularQuantity(pos,MaterialId.Water,out uint water);
            w.TryGetCellularQuantity(pos,MaterialId.Sand,out uint sand);
            w.TryGetCellularQuantity(pos,MaterialId.Steam,out uint steam);
            return new Amounts { Water = water, Sand = sand, Steam = steam };
        }
        private static void Set(object target,string field,object value) =>
            target.GetType().GetField(field,BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target,value);
    }
}
