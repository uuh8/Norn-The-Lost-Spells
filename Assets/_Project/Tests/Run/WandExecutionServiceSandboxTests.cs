using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    public sealed class WandExecutionServiceSandboxTests
    {
        [Test] public void EditorSandboxUsesOwnedInventoryWithoutChangingCurrentSceneOrSourceAssets()
        {
            Type type = Type.GetType("Game.EditorTools.WandExecutionDiagnosticsWindow, Game.Project.Editor");
            Assert.That(type, Is.Not.Null, "开发者需要可以逐步准备/提交真实资源的隔离入口");
            const string path = "Assets/_Project/Tests/Fixtures/Spells/Workbench/SevenCard.asset";
            var sample = AssetDatabase.LoadAssetAtPath<SpellProgramSandboxDefinition>(path);
            Assert.That(sample, Is.Not.Null); bool dirty = SceneManager.GetActiveScene().isDirty;
            var original = sample.Spells[1].ProjectilePrefab;
            var window = ScriptableObject.CreateInstance(type);
            try
            {
                type.GetField("_sample", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, sample);
                type.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var service = (WandExecutionTransactionService)type.GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Assert.That(service, Is.Not.Null); Assert.That(service.TryGetState("sandbox.wand", out var before), Is.True);
                var request = new CastResourceRequestedEvent { InventoryId = "sandbox", SourceWandId = "sandbox.wand", CallerId = "integration",
                    RequestId = 1, ProgramRevision = (uint)before.Program.Revision, ExecutionRevision = before.Draw.ExecutionRevision,
                    ResourceVersion = before.ResourceVersion,
                    InventoryVersion = (uint)type.GetProperty("InventoryVersion", BindingFlags.Instance | BindingFlags.Public).GetValue(window),
                    Operation = CastResourceOperation.PrepareRoot };
                var root = service.Execute(request); Assert.That(root.Success, Is.True, root.Reason.ToString());
                Assert.That(service.TryGetGraph(root.TicketId, out var graph), Is.True);
                Assert.That(graph.RootOutputCount, Is.EqualTo(2)); Assert.That(graph.Scopes.Count, Is.EqualTo(2));
                request.Operation = CastResourceOperation.Complete; request.RequestId++; request.TicketId = root.TicketId;
                Assert.That(service.Execute(request).Success, Is.True);
                Assert.That(service.TryGetState("sandbox.wand", out var after), Is.True); Assert.That(after.Mana, Is.EqualTo(before.Mana - 30));
                Assert.That(sample.Spells[1].ProjectilePrefab, Is.SameAs(original)); Assert.That(EditorUtility.IsDirty(sample), Is.False);
                Assert.That(SceneManager.GetActiveScene().isDirty, Is.EqualTo(dirty));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
