using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class BeffioWaterCompatibilityTests
    {
        [TestCase("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity")]
        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity")]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity")]
        public void FormalSceneWaterDoesNotDependOnBuiltInGrabPass(string scene)
        {
            foreach(string path in AssetDatabase.GetDependencies(scene,true))
            {
                if(!path.EndsWith(".mat"))continue;var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(mat==null||mat.shader==null)continue;
                Assert.That(mat.shader.name,Is.Not.EqualTo("beffio/Medieval_Kingdom/Water"),path);
                Assert.That(mat.shader.name,Is.Not.EqualTo("beffio/Medieval_Kingdom/Water_Mobile"),path);
                if(mat.shader.name!="Game/Environment/BeffioDecorativeWaterURP")continue;
                Assert.That(mat.shader.isSupported,Is.True,path);
                foreach(var message in ShaderUtil.GetShaderMessages(mat.shader))
                    Assert.That(message.severity.ToString(),Is.Not.EqualTo("Error"),message.message);
                Assert.That(mat.GetTexture("_Normalmapinput"),Is.Not.Null,path);
            }
        }
    }
}
