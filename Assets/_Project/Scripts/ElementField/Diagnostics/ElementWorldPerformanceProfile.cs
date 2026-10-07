using System;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum ElementPerformanceMovement { None, InterestOnly, CameraOnly, TravelReturn }
    public enum ElementPerformanceFrameRate { Uncapped, Target60 }

    /// <summary>实验输入资产。Lab 开始前克隆并验证，避免运行中 Inspector 改动污染对照。</summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Performance Profile")]
    public sealed class ElementWorldPerformanceProfile : ScriptableObject
    {
        public string ScenarioId = "A00-empty";
        public float WarmupSeconds = 10f;
        public float SettleSeconds = 10f;
        public float MeasureSeconds = 30f;
        public float DrainTimeoutSeconds = 5f;
        public int PrefillParticleTarget;
        public int ParticlesPerWrite = 256;
        public float WriteInterval = 0.1f;
        public int MeasureWriteCount;
        public float MeasureWriteInterval = 0.1f;
        public float MeasureWriteDelay = 1f;
        public MaterialId MeasureMaterial = MaterialId.Water;
        public int FireAmountPerWrite = 4096;
        public LiquidMaterialProfile[] Materials;
        public Vector3[] DepositPositions;
        public Vector3 MeasurePosition = new Vector3(8f, 0f, 0f);
        public float Radius = 1f;
        public Vector3 InitialVelocity;
        public Vector3 SurfaceNormal = Vector3.up;
        public ElementPerformanceMovement Movement;
        public Vector3[] Route;
        public float MoveSpeed = 2f;
        public float RouteDwellSeconds;
        public bool UseProbe;
        public StatusKind ProbeStatus = StatusKind.Burning;
        public float ProbeInitialIntensity = 80f;
        public float ProbeWaterDelaySeconds = 1f;
        public float ProbeObservationSeconds = 10f;
        public int TargetCount = 1;
        public ElementPerformanceFrameRate FrameRateMode;
        public int MaxWritesPerFrame = 4;
        public bool CaptureEnabled = true;
        public bool EnableWorldActivityExpansion;
        public Vector3[] ActivitySourcePositions = Array.Empty<Vector3>();
        public Vector3[] ProjectileActivitySourcePositions = Array.Empty<Vector3>();
        public bool RemoveActivitySourcesAtMeasure;
        public bool RequireFreshMaterialAuditAtDrain;

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(ScenarioId)) throw new ArgumentException("ScenarioId 不能为空。");
            NonNegative(WarmupSeconds); NonNegative(SettleSeconds);
            Positive(MeasureSeconds); Positive(DrainTimeoutSeconds); Positive(WriteInterval);
            Positive(MeasureWriteInterval); NonNegative(MeasureWriteDelay); Positive(Radius); Positive(MoveSpeed);
            NonNegative(RouteDwellSeconds);
            NonNegative(ProbeInitialIntensity); NonNegative(ProbeWaterDelaySeconds); Positive(ProbeObservationSeconds);
            if (ParticlesPerWrite <= 0 || PrefillParticleTarget < 0 || PrefillParticleTarget > 1000000
                || MeasureWriteCount < 0 || MeasureWriteCount > 16000 || MaxWritesPerFrame <= 0
                || MaxWritesPerFrame > 64 || TargetCount < 0 || TargetCount > 80
                || FireAmountPerWrite <= 0 || FireAmountPerWrite > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(ParticlesPerWrite));
            if (Materials == null || Materials.Length == 0 || DepositPositions == null || DepositPositions.Length == 0)
                throw new ArgumentException("必须绑定材料 Profile 与落点。");
            for (int i = 0; i < Materials.Length; i++)
            {
                if (Materials[i] == null) throw new ArgumentException("材料引用不能为空。");
                var settings = Materials[i].CreateSettings();
                if ((ulong)settings.AmountUnitsPerParticle * (uint)ParticlesPerWrite > ushort.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(ParticlesPerWrite), "单次 Amount 超过 ushort。");
            }
            foreach (Vector3 point in DepositPositions) FiniteVector(point);
            if (ActivitySourcePositions == null) ActivitySourcePositions = Array.Empty<Vector3>();
            foreach (Vector3 point in ActivitySourcePositions) FiniteVector(point);
            if (ProjectileActivitySourcePositions == null)
                ProjectileActivitySourcePositions = Array.Empty<Vector3>();
            foreach (Vector3 point in ProjectileActivitySourcePositions) FiniteVector(point);
            FiniteVector(MeasurePosition); FiniteVector(InitialVelocity); FiniteVector(SurfaceNormal);
            if (SurfaceNormal.sqrMagnitude < 0.0001f) throw new ArgumentException("需要有效表面法线。");
            if (Movement != ElementPerformanceMovement.None)
            {
                if (Route == null || Route.Length < 2) throw new ArgumentException("移动需要至少两个路线点。");
                foreach (Vector3 point in Route) FiniteVector(point);
            }
            if (MeasureWriteCount > 0 && MeasureMaterial != MaterialId.Fire) ScaleFor(MeasureMaterial);
            double lastWrite = (UseProbe ? ProbeWaterDelaySeconds : MeasureWriteDelay)
                + Math.Max(0, MeasureWriteCount - 1) * (double)MeasureWriteInterval;
            if (MeasureWriteCount > 0 && lastWrite >= MeasureSeconds)
                throw new ArgumentException("测量窗口必须覆盖完整写入排程。");
        }

        public uint ScaleFor(MaterialId material)
        {
            for (int i = 0; i < Materials.Length; i++)
            {
                var settings = Materials[i].CreateSettings();
                if (settings.Material == material) return settings.AmountUnitsPerParticle;
            }
            throw new ArgumentException("测量材料不在绑定 Profile 中。");
        }

        public ElementWorldPerformanceSchedule CreateSchedule(bool prefill)
        {
            int count = prefill ? (int)(((long)PrefillParticleTarget + ParticlesPerWrite - 1) / ParticlesPerWrite)
                : MeasureWriteCount;
            var events = new ElementPerformanceWrite[count];
            int remaining = PrefillParticleTarget;
            for (int i = 0; i < count; i++)
            {
                MaterialId material;
                ushort amount;
                Vector3 position;
                double due;
                if (prefill)
                {
                    var settings = Materials[i % Materials.Length].CreateSettings();
                    material = settings.Material;
                    int particles = Math.Min(remaining, ParticlesPerWrite);
                    remaining -= particles;
                    amount = checked((ushort)((uint)particles * settings.AmountUnitsPerParticle));
                    position = DepositPositions[i % DepositPositions.Length];
                    due = i * (double)WriteInterval;
                }
                else
                {
                    material = MeasureMaterial;
                    amount = material == MaterialId.Fire ? (ushort)FireAmountPerWrite
                        : checked((ushort)((uint)ParticlesPerWrite * ScaleFor(material)));
                    position = MeasurePosition;
                    due = (UseProbe ? ProbeWaterDelaySeconds : MeasureWriteDelay) + i * (double)MeasureWriteInterval;
                }
                var request = new ElementWriteRequest(position, material, amount, Radius, false,
                    InitialVelocity, SurfaceNormal);
                events[i] = new ElementPerformanceWrite(i, due, in request);
            }
            return new ElementWorldPerformanceSchedule(events);
        }

        private static void Positive(float value) { if (!ElementWorldPerformanceSchedule.Finite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); }
        private static void NonNegative(float value) { if (!ElementWorldPerformanceSchedule.Finite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value)); }
        private static void FiniteVector(Vector3 value)
        {
            if (!ElementWorldPerformanceSchedule.Finite(value.x) || !ElementWorldPerformanceSchedule.Finite(value.y)
                || !ElementWorldPerformanceSchedule.Finite(value.z)) throw new ArgumentException("坐标必须有限。");
        }
    }
}
