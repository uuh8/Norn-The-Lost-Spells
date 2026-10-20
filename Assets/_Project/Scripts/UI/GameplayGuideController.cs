using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 新 Run 首次进入 P7 时展示的分页文字引导。使用 RunPauseCoordinator 的独立 Reason，
    /// 因而与 Wand Editor / Pause Menu 同时存在时，关闭引导不会错误恢复 Gameplay。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameplayGuideController : MonoBehaviour
    {
        [SerializeField] private GameplayGuideDefinition _definition;
        [SerializeField] private GameObject _panel;
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _bodyText;
        [SerializeField]
        [Tooltip("正文 Scroll View；翻页后会停止惯性并回到顶部。")]
        private ScrollRect _bodyScrollRect;
        [SerializeField] private Text _pageIndicatorText;
        [SerializeField] private Button _previousButton;
        [SerializeField] private Text _nextButtonLabel;
        [SerializeField] private RunPauseCoordinator _pauseCoordinator;

        private int _pageIndex;
        private bool _isOpen;

        private void Start()
        {
            if (_panel != null)
            {
                _panel.SetActive(false);
            }

            HubPracticeSession session = HubPracticeSession.Current;
            if (session != null &&
                session.OwnsScene(gameObject.scene) &&
                session.TryMarkGameplayGuideShown())
            {
                Open();
            }
        }

        private void OnDisable()
        {
            if (_isOpen && _pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.GameplayGuide);
            }

            _isOpen = false;
        }

        public void PreviousPage()
        {
            if (!_isOpen || _pageIndex <= 0)
            {
                return;
            }

            _pageIndex--;
            Refresh();
        }

        public void NextPageOrClose()
        {
            if (!_isOpen)
            {
                return;
            }

            int count = PageCount;
            if (_pageIndex + 1 >= count)
            {
                Close();
                return;
            }

            _pageIndex++;
            Refresh();
        }

        public void Close()
        {
            if (!_isOpen)
            {
                return;
            }

            _isOpen = false;
            if (_panel != null)
            {
                _panel.SetActive(false);
            }

            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.GameplayGuide);
            }
        }

        private int PageCount =>
            _definition != null && _definition.Pages != null
                ? _definition.Pages.Length
                : 0;

        private void Open()
        {
            if (PageCount == 0 || _panel == null || _titleText == null || _bodyText == null)
            {
                GameLog.Warn("GameplayGuideController 缺少页面数据或必要 UI 引用，已跳过开局引导。", "UI");
                return;
            }

            _pageIndex = 0;
            _isOpen = true;
            _panel.SetActive(true);
            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.RequestPause(RunPauseReason.GameplayGuide);
            }

            Refresh();
        }

        private void Refresh()
        {
            GameplayGuideDefinition.Page page = _definition.Pages[_pageIndex];
            _titleText.text = page.Title;
            _bodyText.text = page.Body;

            if (_pageIndicatorText != null)
            {
                _pageIndicatorText.text = $"{_pageIndex + 1} / {PageCount}";
            }

            if (_previousButton != null)
            {
                _previousButton.interactable = _pageIndex > 0;
            }

            if (_nextButtonLabel != null)
            {
                _nextButtonLabel.text = _pageIndex + 1 >= PageCount ? "开始游戏" : "下一页";
            }

            ResetBodyScrollToTop();
        }

        private void ResetBodyScrollToTop()
        {
            if (_bodyScrollRect == null)
            {
                return;
            }

            // Text 更新后，ContentSizeFitter 的高度尚可能停留在上一页。
            // 先强制完成本次离散翻页的 Layout，再把 1（顶部）写给纵向归一化位置。
            // 这里不在 Update hot path 中执行，不会形成逐帧 Layout Rebuild。
            Canvas.ForceUpdateCanvases();
            if (_bodyScrollRect.content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_bodyScrollRect.content);
            }

            _bodyScrollRect.StopMovement();
            _bodyScrollRect.verticalNormalizedPosition = 1f;
        }
    }
}
