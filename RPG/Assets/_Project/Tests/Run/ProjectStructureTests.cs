using System;
using NUnit.Framework;
using UnityEditor;
using Game.Skills;

namespace Game.Run.Tests
{
    public sealed class ProjectStructureTests
    {
        [TestCase("SpellProgrammingV2")]
        [TestCase("SpellProgrammingV2Sandbox")]
        [TestCase("SinglePlayer")]
        [TestCase("Test")]
        [TestCase("Tests")]
        public void FormalConfigurationHasNoDevelopmentBatchDirectories(string oldName)
        {Assert.That(AssetDatabase.IsValidFolder("Assets/_Project/ScriptableObjects/"+oldName),Is.False);}

        [Test] public void ProductionSpellDefinitionsAreInTheSpellDomain()
        {
            foreach(string guid in AssetDatabase.FindAssets("t:SpellDefinition",new[]{"Assets/_Project/ScriptableObjects"}))
                Assert.That(AssetDatabase.GUIDToAssetPath(guid),Does.StartWith("Assets/_Project/ScriptableObjects/Spell/"));
        }

        [Test] public void ProjectEditorAssemblyUsesTheProjectName()
        {Assert.That(Type.GetType("Game.EditorTools.ProjectLayoutMaintenance, Game.Project.Editor"),Is.Not.Null);}
    }
}
