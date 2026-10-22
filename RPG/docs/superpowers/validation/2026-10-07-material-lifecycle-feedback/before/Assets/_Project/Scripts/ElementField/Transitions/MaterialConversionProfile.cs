using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum MaterialConversionCondition : byte { Heated, Cooled, Freezing, Melting, Drying, Volatile }
    [Serializable]
    public struct MaterialConversionRule
    {
        public MaterialId Source, FirstProduct, SecondProduct;
        public uint InputUnits, FirstUnits, SecondUnits;
        public MaterialConversionCondition Condition;
        public float Threshold, InputGmuPerSecond, CooldownSeconds;
        // 固定 GMU/s 保证稀薄尾量可耗尽；比例项使浓云按库存冷凝，避免密集蒸汽变成数分钟水源。
        public float InputFractionPerSecond;
        public uint MinimumInputGmu;
        public bool IsValid => Source != MaterialId.Empty && FirstProduct != MaterialId.Empty
            && Source != FirstProduct && Source != SecondProduct && FirstProduct != SecondProduct
            && InputUnits > 0 && FirstUnits > 0 && (ulong)FirstUnits + SecondUnits == InputUnits
            && (SecondUnits == 0) == (SecondProduct == MaterialId.Empty)
            && float.IsFinite(Threshold) && float.IsFinite(InputGmuPerSecond) && InputGmuPerSecond > 0
            && float.IsFinite(InputFractionPerSecond) && InputFractionPerSecond >= 0 && InputFractionPerSecond <= 1
            && float.IsFinite(CooldownSeconds) && CooldownSeconds >= 0;
    }
    [CreateAssetMenu(menuName = "Game/Element Field/Material Conversions")]
    public sealed class MaterialConversionProfile : ScriptableObject
    {
        [SerializeField, Min(32)] private int _maximumCandidates = 2048;
        [SerializeField, Range(1, 256)] private int _candidatesPerTick = 64;
        [SerializeField, Range(1, 32)] private int _commitsPerTick = 8;
        [SerializeField] private float _ambientTemperature = 20;
        [SerializeField, Range(0, 1)] private float _ambientDryness = .4f;
        [SerializeField] private MaterialConversionRule[] _rules =
        {
            Rule(MaterialId.Water, MaterialId.Steam, MaterialConversionCondition.Heated, 70, 24),
            Rule(MaterialId.Steam, MaterialId.Water, MaterialConversionCondition.Cooled, 35, 12, .4f, 32),
            Rule(MaterialId.Water, MaterialId.Ice, MaterialConversionCondition.Freezing, -5, 64),
            Rule(MaterialId.Ice, MaterialId.Water, MaterialConversionCondition.Melting, 5, 32),
            new MaterialConversionRule { Source=MaterialId.Sticky, FirstProduct=MaterialId.DriedSlime,
                SecondProduct=MaterialId.Steam, InputUnits=4, FirstUnits=3, SecondUnits=1,
                Condition=MaterialConversionCondition.Drying, Threshold=.65f, InputGmuPerSecond=16, CooldownSeconds=3 },
            Rule(MaterialId.Poison, MaterialId.ToxicVapor, MaterialConversionCondition.Volatile, 0, 8)
        };
        private static MaterialConversionRule Rule(MaterialId source, MaterialId target, MaterialConversionCondition condition, float threshold, float rate,
            float fraction=0, uint minimumInput=1)
            => new MaterialConversionRule { Source=source, FirstProduct=target, InputUnits=1, FirstUnits=1,
                Condition=condition, Threshold=threshold, InputGmuPerSecond=rate, CooldownSeconds=3,
                InputFractionPerSecond=fraction, MinimumInputGmu=minimumInput };
        public int MaximumCandidates => _maximumCandidates;
        public int CandidatesPerTick => _candidatesPerTick;
        public int CommitsPerTick => _commitsPerTick;
        public float AmbientTemperature => _ambientTemperature;
        public float AmbientDryness => _ambientDryness;
        public int RuleCount => _rules?.Length ?? 0;
        public MaterialConversionRule GetRule(int index) => _rules[index];
        public bool IsValid
        {
            get
            {
                if (_maximumCandidates < 32 || _candidatesPerTick < 1 || _commitsPerTick < 1
                    || !float.IsFinite(_ambientTemperature) || !float.IsFinite(_ambientDryness)
                    || _ambientDryness < 0 || _ambientDryness > 1 || RuleCount == 0) return false;
                for (int i=0; i<RuleCount; i++) if (!_rules[i].IsValid) return false;
                return true;
            }
        }
    }
}
