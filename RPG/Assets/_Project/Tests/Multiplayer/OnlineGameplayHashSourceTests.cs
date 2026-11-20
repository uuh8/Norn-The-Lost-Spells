using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 直接调用构建Hash使用的源码枚举入口，核对磁盘上的全部规则文件。
    /// 不复制待测模块列表来生成actual，不加载/保存Scene，也不重算整套地图Hash。
    /// 测试通过只证明源码输入覆盖；刷新Manifest及配套Client/DS构建仍由正式工具负责。
    /// </summary>
    public sealed class OnlineGameplayHashSourceTests
    {
        [TestCase("Core")]
        [TestCase("Combat")]
        [TestCase("Skills")]
        [TestCase("Run")]
        [TestCase("Character")]
        [TestCase("Multiplayer")]
        [TestCase("Networking")]
        [TestCase("ElementField")]
        [TestCase("Materials")]
        public void ProductionSourceInputsCoverEveryRuntimeFileInGameplayModule(string module)
        {
            HashSet<string> actual=ReadProductionSources();
            string directory="Assets/_Project/Scripts/"+module;
            Assert.That(Directory.Exists(directory),Is.True,"缺少约定的Gameplay模块目录："+directory);
            string[] expected=Directory.GetFiles(directory,"*.cs",SearchOption.AllDirectories);
            Assert.That(expected.Length,Is.GreaterThan(0),"覆盖检查必须有实际源码，不能空集通过。");
            int missing=0;var examples=new List<string>();
            foreach(string source in expected)
            {
                string path=source.Replace('\\','/');
                if(actual.Contains(path))continue;
                missing++;if(examples.Count<8)examples.Add(path);
            }
            // 模块的全部文件都是契约；只截断错误消息示例，不截断核对范围。
            Assert.That(missing,Is.Zero,module+" 的Gameplay Hash缺少源码；expected="+expected.Length
                +", missing examples="+string.Join(", ",examples));
        }

        [TestCase("UI")]
        [TestCase("Rendering")]
        public void ProductionSourceInputsKeepPresentationModulesOutsideRuntimeSourceBoundary(string module)
        {
            string prefix="Assets/_Project/Scripts/"+module+"/";
            int included=0;
            foreach(string path in ReadProductionSources())
                if(path.StartsWith(prefix,StringComparison.Ordinal))included++;
            Assert.That(included,Is.Zero,"补材料规则不能顺带把全部纯表现模块加入Runtime源码摘要。");
        }

        private static HashSet<string> ReadProductionSources()
        {
            // 沿现有Multiplayer authoring tests的反射入口，避免Runtime测试程序集反向依赖Editor工具程序集。
            Type tools=null;
            foreach(Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                tools=assembly.GetType("Game.Project.Editor.OnlineCombatTools",false);
                if(tools!=null)break;
            }
            Assert.That(tools,Is.Not.Null,"必须加载真实OnlineCombatTools，不能用测试替身枚举输入。");
            MethodInfo enumerate=tools.GetMethod("EnumerateGameplaySourceInputs",BindingFlags.Static|BindingFlags.NonPublic);
            Assert.That(enumerate,Is.Not.Null,"ComputeGameplayHash需要可独立核对的统一源码输入入口。");
            var inputs=enumerate.Invoke(null,null) as IEnumerable<string>;
            Assert.That(inputs,Is.Not.Null);
            var sources=new HashSet<string>(StringComparer.Ordinal);
            foreach(string path in inputs)sources.Add(path);
            Assert.That(sources.Count,Is.GreaterThan(0));
            return sources;
        }
    }
}
