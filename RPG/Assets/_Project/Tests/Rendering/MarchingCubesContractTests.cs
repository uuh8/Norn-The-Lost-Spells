using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// Lookup Table 是 Marching Cubes 的拓扑 Contract：任意一个数字漂移都可能让三角形开裂或越界。
    /// 因此测试不仅抽查 Case，还会把 256x16 个 signed byte 序列做 SHA-256 精确比对。
    /// </summary>
    public sealed class MarchingCubesContractTests
    {
        private const string TablePath =
            "Assets/_Project/Art/Elemental/Compute/MarchingCubesTables.hlsl";
        private const string CanonicalTableSha256 =
            "19bf7699e214903d72c94c296546f2e31337d637a1e4b118c3108a0f428e809b";

        [Test]
        public void SurfaceGpuLayoutKeepsThree32ByteVerticesPerTriangle()
        {
            Assert.That(GpuLiquidSurfaceRenderer.SurfaceVertexStride, Is.EqualTo(32));
            Assert.That(GpuLiquidSurfaceRenderer.SurfaceTriangleStride, Is.EqualTo(96));
        }

        [Test]
        public void PackedLookupTableIsExactCanonical256By16Table()
        {
            sbyte[] table = ReadPackedTable();
            Assert.That(table.Length, Is.EqualTo(256 * 16));

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = new byte[table.Length];
                Buffer.BlockCopy(table, 0, bytes, 0, bytes.Length);
                string actual = ToLowerHex(sha256.ComputeHash(bytes));
                Assert.That(actual, Is.EqualTo(CanonicalTableSha256));
            }
        }

        [Test]
        public void CornerOffsetsAndEdgeEndpointsMatchClassicCaseConventionExactly()
        {
            string source = File.ReadAllText(TablePath);
            int[] cornerOffsets = ReadIntegerTuples(
                source,
                @"FLUID_MC_CORNER_OFFSETS\s*\[\s*8\s*\]\s*=\s*\{(?<body>[\s\S]*?)\};",
                @"int3\s*\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)",
                tupleWidth: 3,
                expectedTupleCount: 8);
            CollectionAssert.AreEqual(
                new[]
                {
                    0, 0, 0,  1, 0, 0,  1, 1, 0,  0, 1, 0,
                    0, 0, 1,  1, 0, 1,  1, 1, 1,  0, 1, 1
                },
                cornerOffsets,
                "Corner numbering drift changes every Case bit and invalidates the tri table.");

            int[] edgeEndpoints = ReadIntegerTuples(
                source,
                @"FLUID_MC_EDGE_CORNERS\s*\[\s*12\s*\]\s*=\s*\{(?<body>[\s\S]*?)\};",
                @"uint2\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)",
                tupleWidth: 2,
                expectedTupleCount: 12);
            CollectionAssert.AreEqual(
                new[]
                {
                    0, 1,  1, 2,  2, 3,  3, 0,
                    4, 5,  5, 6,  6, 7,  7, 4,
                    0, 4,  1, 5,  2, 6,  3, 7
                },
                edgeEndpoints,
                "Edge numbering must match the exact values stored in every lookup row.");
        }

        [Test]
        public void LookupRowsHaveValidEdgesTerminatorsAndAtMostFiveTriangles()
        {
            sbyte[] table = ReadPackedTable();
            AssertRow(table, 0, Array.Empty<int>());
            AssertRow(table, 1, new[] { 0, 8, 3 });
            AssertRow(table, 255, Array.Empty<int>());

            int maximumTriangleCount = 0;
            for (int row = 0; row < 256; row++)
            {
                int edgeCount = 0;
                bool foundTerminator = false;
                for (int column = 0; column < 16; column++)
                {
                    int edge = table[row * 16 + column];
                    if (edge == -1)
                    {
                        foundTerminator = true;
                        continue;
                    }

                    Assert.That(foundTerminator, Is.False,
                        $"Case {row} contains an edge after its -1 terminator.");
                    Assert.That(edge, Is.InRange(0, 11),
                        $"Case {row}, column {column} has an invalid edge.");
                    edgeCount++;
                }

                Assert.That(foundTerminator, Is.True, $"Case {row} has no terminator.");
                Assert.That(edgeCount % 3, Is.Zero, $"Case {row} is not whole triangles.");
                maximumTriangleCount = Math.Max(maximumTriangleCount, edgeCount / 3);
            }

            Assert.That(maximumTriangleCount, Is.EqualTo(5));
        }

        private static sbyte[] ReadPackedTable()
        {
            string source = File.ReadAllText(TablePath);
            Match block = Regex.Match(
                source,
                @"FLUID_TRI_TABLE_PACKED\s*\[\s*256\s*\]\s*=\s*\{(?<body>[\s\S]*?)\};");
            Assert.That(block.Success, Is.True, "Missing 256-row packed tri table.");

            MatchCollection words = Regex.Matches(block.Groups["body"].Value, @"0x([0-9a-fA-F]{8})u");
            Assert.That(words.Count, Is.EqualTo(256 * 4));
            var unpacked = new List<sbyte>(256 * 16);
            foreach (Match word in words)
            {
                uint packed = Convert.ToUInt32(word.Groups[1].Value, 16);
                for (int byteIndex = 0; byteIndex < 4; byteIndex++)
                    unpacked.Add(unchecked((sbyte)((packed >> (byteIndex * 8)) & 0xffu)));
            }

            return unpacked.ToArray();
        }

        private static int[] ReadIntegerTuples(
            string source,
            string blockPattern,
            string tuplePattern,
            int tupleWidth,
            int expectedTupleCount)
        {
            Match block = Regex.Match(source, blockPattern);
            Assert.That(block.Success, Is.True, "Missing Marching Cubes mapping block.");
            MatchCollection tuples = Regex.Matches(block.Groups["body"].Value, tuplePattern);
            Assert.That(tuples.Count, Is.EqualTo(expectedTupleCount));

            var values = new int[expectedTupleCount * tupleWidth];
            for (int tupleIndex = 0; tupleIndex < tuples.Count; tupleIndex++)
            {
                for (int component = 0; component < tupleWidth; component++)
                {
                    values[tupleIndex * tupleWidth + component] =
                        int.Parse(tuples[tupleIndex].Groups[component + 1].Value);
                }
            }

            return values;
        }

        private static void AssertRow(sbyte[] table, int row, int[] expectedEdges)
        {
            for (int column = 0; column < 16; column++)
            {
                int expected = column < expectedEdges.Length ? expectedEdges[column] : -1;
                Assert.That(table[row * 16 + column], Is.EqualTo(expected),
                    $"Case {row}, column {column} differs.");
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
