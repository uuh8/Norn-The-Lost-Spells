using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public class StatusIconView : MonoBehaviour
    {
        [SerializeField] private Image _icon;
        [SerializeField] private Text _percentageText;

        public void Set(Sprite icon, float intensity)
        {
            if (_icon != null)
            {
                _icon.sprite = icon;
                _icon.enabled = icon != null;
            }

            if (_percentageText != null)
                _percentageText.text = $"{Mathf.RoundToInt(intensity)}%";

            gameObject.SetActive(true);
        }

        public void Clear()
        {
            if (_icon != null)
            {
                _icon.sprite = null;
                _icon.enabled = false;
            }

            if (_percentageText != null)
                _percentageText.text = "";

            gameObject.SetActive(false);
        }
    }
}
