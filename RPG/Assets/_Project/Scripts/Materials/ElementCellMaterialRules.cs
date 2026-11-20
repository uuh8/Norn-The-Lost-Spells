using System;
using UnityEngine;

namespace Game.Materials
{
    /// <summary>同一种 MaterialId 在本模拟器中采用的运动／支撑规则。Fire 由原有强度场负责。</summary>
    public enum ElementCellMotion : byte { None, Fixed, Grain, Liquid, Gas }

    /// <summary>供世界 Profile 序列化。0 沿用对应材质的内建值，世界启动后只读烘焙副本。</summary>
    [Serializable]
    public struct ElementCellTransportTuning
    {
        public MaterialId Material;
        [Min(0), Tooltip("只用于上下置换排序，较大者能沉入允许被置换的轻液。")] public int RelativeDensity;
        [Min(0), Tooltip("向下最大流量为单格容量除以此值，向上取整。")] public int DownFlowDivisor;
        [Min(0), Tooltip("向四侧合计最大流量为单格容量除以此值，向上取整。")] public int SideFlowDivisor;
        [Range(0, 2), Tooltip("气体上下平衡的空间权重。1 是普通扩散，2 使上方可具有较高平衡浓度。")] public int GasUpperWeight;
    }

    /// <summary>
    /// 三维元胞的只读规则表。它只解释材料如何占据一格和运输，材质外观、伤害和存档身份仍独立。
    /// 密度只参与两格上下置换的排序；流量用单格容量的分数给出，换一套 GMU 容量也可工作。
    /// </summary>
    public static class ElementCellMaterialRules
    {
        internal readonly struct Rule
        {
            public readonly ElementCellMotion Motion;
            public readonly int Density, DownDivisor, SideDivisor, UpperWeight;

            public Rule(ElementCellMotion motion, int density = 0, int downDivisor = 1, int sideDivisor = 1, int upperWeight = 1)
            {
                Motion = motion;
                Density = density;
                DownDivisor = downDivisor;
                SideDivisor = sideDivisor;
                UpperWeight = upperWeight;
            }
        }

        private static readonly Rule[] Rules = CreateRules();
        private static readonly Transport DefaultTransport = new Transport(Rules);

        private static Rule[] CreateRules()
        {
            var rules = new Rule[256];
            rules[(byte)MaterialId.Rock] = new Rule(ElementCellMotion.Fixed);
            rules[(byte)MaterialId.Soil] = new Rule(ElementCellMotion.Fixed);
            rules[(byte)MaterialId.Ice] = new Rule(ElementCellMotion.Fixed);
            rules[(byte)MaterialId.DriedSlime] = new Rule(ElementCellMotion.Fixed);
            rules[(byte)MaterialId.Sand] = new Rule(ElementCellMotion.Grain, 20);
            rules[(byte)MaterialId.Water] = new Rule(ElementCellMotion.Liquid, 10, 1, 4);
            rules[(byte)MaterialId.Oil] = new Rule(ElementCellMotion.Liquid, 8, 2, 8);
            rules[(byte)MaterialId.Poison] = new Rule(ElementCellMotion.Liquid, 10, 1, 4);
            rules[(byte)MaterialId.Sticky] = new Rule(ElementCellMotion.Liquid, 12, 4, 16);
            rules[(byte)MaterialId.Steam] = new Rule(ElementCellMotion.Gas, upperWeight: 2);
            rules[(byte)MaterialId.ToxicVapor] = new Rule(ElementCellMotion.Gas);
            return rules;
        }

        public static ElementCellMotion Motion(MaterialId material) => Rules[(byte)material].Motion;
        public static bool Supports(MaterialId material) => Motion(material) != ElementCellMotion.None;
        public static bool OccupiesWholeCell(MaterialId material)
        {
            ElementCellMotion motion = Motion(material);
            return motion == ElementCellMotion.Grain || motion == ElementCellMotion.Fixed && material != MaterialId.DriedSlime;
        }

        /// <summary>每个世界独立的只读输运表，Inspector 改动不可能在一个 Tick 中途改变已生效的规则。</summary>
        public sealed class Transport
        {
            private readonly Rule[] _rules;
            internal Transport(Rule[] rules) => _rules = rules;
            public int Density(MaterialId material) => _rules[(byte)material].Density;
            public int GasUpperWeight(MaterialId material) => _rules[(byte)material].UpperWeight;
            public uint FlowLimit(MaterialId material, uint capacity, bool downward)
            {
                Rule rule = _rules[(byte)material];
                if (rule.Motion != ElementCellMotion.Liquid || capacity == 0) return 0;
                uint divisor = (uint)(downward ? rule.DownDivisor : rule.SideDivisor);
                return 1 + (capacity - 1) / divisor;
            }
        }

        public static Transport Bake(ElementCellTransportTuning[] tuning)
        {
            if (tuning == null || tuning.Length == 0) return DefaultTransport;
            var copy = (Rule[])Rules.Clone();
            var seen = new bool[copy.Length];
            foreach (ElementCellTransportTuning entry in tuning)
            {
                int index = (byte)entry.Material;
                Rule previous = copy[index];
                if (seen[index] || previous.Motion == ElementCellMotion.None ||
                    entry.RelativeDensity < 0 || entry.DownFlowDivisor < 0 || entry.SideFlowDivisor < 0 ||
                    entry.GasUpperWeight < 0 || entry.GasUpperWeight > 2)
                    throw new ArgumentException($"元胞输运配置含有重复材质、未接管材质或无效参数：{entry.Material}。", nameof(tuning));
                seen[index] = true;
                copy[index] = new Rule(previous.Motion,
                    entry.RelativeDensity > 0 ? entry.RelativeDensity : previous.Density,
                    entry.DownFlowDivisor > 0 ? entry.DownFlowDivisor : previous.DownDivisor,
                    entry.SideFlowDivisor > 0 ? entry.SideFlowDivisor : previous.SideDivisor,
                    entry.GasUpperWeight > 0 ? entry.GasUpperWeight : previous.UpperWeight);
            }
            return new Transport(copy);
        }
    }
}
