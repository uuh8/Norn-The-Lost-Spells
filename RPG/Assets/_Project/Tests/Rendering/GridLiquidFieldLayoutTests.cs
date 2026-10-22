using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GridLiquidFieldLayoutTests
    {
        [Test]
        public void HalfDecodeKeepsEveryUnityBitPatternAndRemovesRepeatedNativeConversionCost()
        {
            // 包含Subnormal、正负零、Infinity和NaN。缓存必须复制Unity实际位模式，
            // 不能换一套近似转换公式后只检查常见数值；所有16位输入一次穷举。
            for(int bits=0;bits<65536;bits++)
            {
                uint pair=(uint)bits|((uint)(65535-bits)<<16);
                Assert.That(BitConverter.SingleToInt32Bits(GridLiquidPackedCell.Low(pair)),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(Mathf.HalfToFloat((ushort)bits))));
                Assert.That(BitConverter.SingleToInt32Bits(GridLiquidPackedCell.High(pair)),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(Mathf.HalfToFloat((ushort)(65535-bits)))));
            }
            for(int round=0;round<4;round++){DecodeWork(true);DecodeWork(false);}
            var native=new double[9];var actual=new double[9];double expected=DecodeWork(true),result=0;
            long allocated=GC.GetAllocatedBytesForCurrentThread();
            for(int round=0;round<9;round++)
            {
                // 同输入交替测量，消除先后顺序/暖机的偏差。计时只包含数值解码。
                bool nativeFirst=(round&1)==0;
                long started=System.Diagnostics.Stopwatch.GetTimestamp();result=DecodeWork(nativeFirst);
                double first=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                started=System.Diagnostics.Stopwatch.GetTimestamp();result=DecodeWork(!nativeFirst);
                double second=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                native[round]=nativeFirst?first:second;actual[round]=nativeFirst?second:first;
            }
            allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
            Directory.CreateDirectory("Backups/ElementPerformance/L427");
            File.WriteAllText("Backups/ElementPerformance/L427/half-decode-work.json",JsonUtility.ToJson(new HalfWork
            {Utc=DateTime.UtcNow.ToString("O"),NativeMilliseconds=native,ActualMilliseconds=actual,AllocatedBytes=allocated,
                NativeImplementation=typeof(Mathf).GetMethod("HalfToFloat").GetMethodImplementationFlags().ToString()},true));
            Assert.That(result,Is.EqualTo(expected));Assert.That(allocated,Is.Zero);
            Array.Sort(native);Array.Sort(actual);
            Assert.That(actual[4],Is.LessThan(native[4]*.7),"高频解码应明显快于重复调用Unity的HalfToFloat；不能只增加缓存而没有实际收益。");
        }
        private static double DecodeWork(bool native)
        {
            double sum=0;
            for(int i=0;i<131072;i++)
            {
                uint pair=(uint)(0x3800+(i&2047))|((uint)(0x3800+((i*13)&2047))<<16);
                sum+=native?Mathf.HalfToFloat((ushort)pair)+Mathf.HalfToFloat((ushort)(pair>>16))
                    :GridLiquidPackedCell.Low(pair)+GridLiquidPackedCell.High(pair);
            }
            return sum;
        }
        [Serializable]private sealed class HalfWork
        {public string Utc,NativeImplementation;public double[] NativeMilliseconds,ActualMilliseconds;public long AllocatedBytes;}
        [Test]
        public void EmptyPackedCellIgnoresUnusedMotionBits()
        {
            var cell=new GridLiquidPackedCell{QuantityFlags=5u<<16,VelocityXY=GridLiquidPackedCell.HalfPair(4,8),
                VelocityZOffsetX=GridLiquidPackedCell.HalfPair(2,.5f),OffsetYZ=GridLiquidPackedCell.HalfPair(.2f,.8f)};
            var sample=cell.Decode();
            Assert.That(sample.Quantity,Is.Zero);Assert.That(sample.OffsetCells,Is.EqualTo(Vector3.zero));
            Assert.That(sample.VelocityWorld,Is.EqualTo(Vector3.zero));Assert.That(cell.QuantityFlags>>16,Is.EqualTo(5));
        }
        [Test]
        public void DefaultBudgetRetainsNinetySixR2PagesWithin128MiB()
        {
            var profile=AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset");
            // 2MiB是P7完整CPU Owner域的GPU Lookup；容量判断必须包含这份实际开销。
            var budget=profile.Budget(8,3,2*1048576L);
            Assert.That(budget.FineViews,Is.GreaterThanOrEqualTo(96),"稀疏输入不能预留掉全部显示容量，63页时不应整体退CPU。");
            Assert.That(budget.InputChunks,Is.GreaterThanOrEqualTo(256));
            using(var gpu=new GridLiquidFieldResources(budget))Assert.That(gpu.AllocatedBytes+2*1048576L,Is.LessThanOrEqualTo(128*1048576L));
        }
        [Test]
        public void PackedLayoutPreservesQuantityAndBoundedMotion()
        {
            Assert.That(Marshal.SizeOf<GridLiquidPackedCell>(),Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<GridLiquidChunkLookup>(),Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<GridLiquidSmallDrop>(),Is.EqualTo(32));
            var s=new Game.ElementField.LiquidCellPresentationSample(510,new Vector3(-.8f,.125f,.25f),new Vector3(-1.5f,12,2));
            var p=GridLiquidPackedCell.Pack(s,5);var read=p.Decode();
            Assert.That(read.Quantity,Is.EqualTo(510));Assert.That(p.QuantityFlags>>16,Is.EqualTo(5));
            Assert.That((read.VelocityWorld-s.VelocityWorld).magnitude,Is.LessThan(.005f));
            Assert.That((read.OffsetCells-s.OffsetCells).magnitude,Is.LessThan(.001f));
            Assert.That(GridLiquidFieldGpuLayout.FloorDiv(-1,8),Is.EqualTo(-1));
            Assert.That(GridLiquidFieldGpuLayout.FloorDiv(-8,8),Is.EqualTo(-1));
        }
        [Test]
        public void ActualSharedGpuAllocationStaysWithin128MiB()
        {
            if(!SystemInfo.supportsComputeShaders)Assert.Ignore("此GPU不支持Compute。");
            var budget=new GridLiquidFieldBudget(8,3,1024,128,64,128);
            using(var resources=new GridLiquidFieldResources(budget))
            {Assert.That(resources.AllocatedBytes,Is.LessThanOrEqualTo(budget.Bytes));Assert.That(resources.AllocatedBytes,Is.LessThanOrEqualTo(128*1048576L));}
            Assert.That(budget.FineViews,Is.GreaterThan(0));
        }
        [Test]
        public void ContinuousShaderAndAllComputeKernelsImport()
        {
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Art/Elemental/Shaders/ElementLiquidContinuous.shader");
            Assert.That(shader,Is.Not.Null);Assert.That(ShaderUtil.ShaderHasError(shader),Is.False);
            var compute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/GridLiquidField.compute");
            Assert.That(compute,Is.Not.Null);
            foreach(string kernel in new[]{"SampleField","BuildVertices","SmoothVertices","FinishVertices","FinishNormals","BuildFaces","FinishArguments","ClearCounters"})
                Assert.That(compute.HasKernel(kernel),Is.True,kernel);
        }
    }
}
