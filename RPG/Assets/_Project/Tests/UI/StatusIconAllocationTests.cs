using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>
    /// 状态强度的逐步衰减会不断跨过整数百分比。测试真实 Image/Text/View，而不是只测字符串帮助函数；
    /// 只记录同步 Set 调用的当前线程 GC.Alloc 事件，不能把这个小范围等同于 Native 整帧性能。
    /// </summary>
    public sealed class StatusIconAllocationTests
    {
        private GameObject _root;
        private StatusIconView _view;
        private Image _icon;
        private Text _label;
        private Texture2D _texture;
        private Sprite _sprite;

        [SetUp]
        public void CreateRealStatusView()
        {
            _root = new GameObject("StatusAllocationFixture", typeof(RectTransform));
            _icon = new GameObject("Icon", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            _icon.transform.SetParent(_root.transform, false);
            _label = new GameObject("Percent", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            _label.transform.SetParent(_root.transform, false);
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _view = _root.AddComponent<StatusIconView>();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(StatusIconView).GetField("_icon", fields).SetValue(_view, _icon);
            typeof(StatusIconView).GetField("_percentageText", fields).SetValue(_view, _label);
            _texture = new Texture2D(2, 2);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, 2, 2), Vector2.one * .5f);

            // 允许首次实例化、字体/Graphic初始化和有界缓存的冷启动成本。
            // 先走遍有效域，再测相同调用，避免把首次UGUI池初始化误诊为持续分配。
            for (int percent = 0; percent <= 100; percent++) _view.Set(_sprite, percent);
        }

        [TearDown]
        public void DestroyOnlyFixtureObjects()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_sprite != null) Object.DestroyImmediate(_sprite);
            if (_texture != null) Object.DestroyImmediate(_texture);
        }

        [Test]
        public void RepeatedSameRoundedPercentUsesNoManagedAllocationAfterWarmup()
        {
            _view.Set(_sprite, 99.1f);
            using var meter = new CalibratedAllocationEvents();
            meter.Begin();
            for (int i = 0; i < 64; i++) _view.Set(_sprite, 99.1f);
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True, "必须先实际探测到1KiB正控制，且记录器不能溢出。");
            Assert.That(events, Is.Zero, "相同可见百分比的状态事实不能反复创建相同字符串。");
            Assert.That(_label.text, Is.EqualTo("99%"));
        }

        [Test]
        public void ChangingAuthoritativePercentUsesNoManagedAllocationAfterWarmup()
        {
            using var meter = new CalibratedAllocationEvents();
            meter.Begin();
            for (int percent = 0; percent <= 100; percent++) _view.Set(_sprite, percent);
            long events = meter.End();
            Assert.That(meter.IsValid, Is.True, "不能把不支持的零计数当成零分配。");
            Assert.That(events, Is.Zero, "有效域中的每次整数变化也属于持续状态刷新，不能逐次格式化百分比。");
            Assert.That(_label.text, Is.EqualTo("100%"));
        }

        [TestCase(.49f, "0%")]
        [TestCase(.5f, "0%")]
        [TestCase(1.5f, "2%")]
        [TestCase(99.5f, "100%")]
        [TestCase(-2.3f, "-2%")]
        [TestCase(120.2f, "120%")]
        public void CaptionKeepsOriginalRoundingAndOutOfContractMeaning(float intensity, string expected)
        {
            _view.Set(_sprite, intensity);
            Assert.That(_label.text, Is.EqualTo(expected));
            Assert.That(_icon.sprite, Is.SameAs(_sprite));
            Assert.That(_icon.enabled, Is.True); Assert.That(_root.activeSelf, Is.True);
        }

        [Test]
        public void ClearHidesOldCaptionAndLaterSetRebindsOriginalView()
        {
            _view.Set(_sprite, 27.4f); _view.Clear();
            Assert.That(_label.text, Is.Empty); Assert.That(_icon.sprite, Is.Null);
            Assert.That(_icon.enabled, Is.False); Assert.That(_root.activeSelf, Is.False);
            _view.Set(null, 43.1f);
            Assert.That(_root.activeSelf, Is.True); Assert.That(_label.text, Is.EqualTo("43%"));
            Assert.That(_icon.enabled, Is.False);
            _view.Set(_sprite, 27.4f);
            Assert.That(_label.text, Is.EqualTo("27%")); Assert.That(_icon.enabled, Is.True);
        }

        /// <summary>
        /// 与项目网络计量相同的原生GC.Alloc事件口径。保留本地测试小封装以免UI测试反向依赖Networking；
        /// 不使用当前Unity上已证实不可靠的GetAllocatedBytesForCurrentThread，也不把事件数叫Bytes。
        /// </summary>
        private sealed class CalibratedAllocationEvents : IDisposable
        {
            private ProfilerRecorder _recorder;
            private bool _overflowed;
            private readonly long _calibration;
            public bool IsValid => _recorder.Valid && _calibration > 0 && !_overflowed;

            public CalibratedAllocationEvents()
            {
                _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 512,
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                if (!_recorder.Valid) return;
                _recorder.Stop(); Begin(); AllocatePositiveControl(); _calibration = End();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void AllocatePositiveControl()
            {
                var bytes = new byte[1024]; GC.KeepAlive(bytes);
            }

            public void Begin()
            {
                if (!_recorder.Valid) return;
                _recorder.Reset(); _recorder.Start();
            }

            public long End()
            {
                if (!_recorder.Valid) return -1;
                _recorder.Stop();
                if (_recorder.Count >= _recorder.Capacity) _overflowed = true;
                return _overflowed ? -1 : _recorder.Count;
            }

            public void Dispose() { if (_recorder.Valid) _recorder.Dispose(); }
        }
    }
}
