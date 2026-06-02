using UnityEngine;
using UnityEngine.UI;

namespace HeartopiaMod
{
    public partial class HeartopiaComplete
    {
        private const string STATUS_JUMP_BUTTON_CHILD = "super_move@w@go";

        private bool hideJumpButtonEnabled;
        private GameObject cachedJumpButtonGo;
        private float nextJumpButtonHideAt;

        private void ProcessHideJumpButtonOnUpdate()
        {
            if (!this.hideJumpButtonEnabled)
            {
                this.cachedJumpButtonGo = null;
                return;
            }

            if (Time.unscaledTime < this.nextJumpButtonHideAt)
            {
                return;
            }

            this.nextJumpButtonHideAt = Time.unscaledTime + 0.25f;
            this.ApplyHideJumpButtonVisual();
        }

        private void ApplyHideJumpButtonVisual()
        {
            if (!this.TryResolveJumpButtonGameObject(out GameObject jumpGo))
            {
                return;
            }

            if (jumpGo.activeSelf)
            {
                jumpGo.SetActive(false);
            }

            Button button = jumpGo.GetComponent<Button>();
            if (button != null)
            {
                button.interactable = false;
            }

            Graphic[] graphics = jumpGo.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic != null)
                {
                    graphic.raycastTarget = false;
                }
            }
        }

        private bool TryResolveJumpButtonGameObject(out GameObject jumpGo)
        {
            jumpGo = this.cachedJumpButtonGo;
            if (jumpGo != null)
            {
                return true;
            }

            GameObject skillBarWidget = GameObject.Find(STATUS_SKILL_BAR_WIDGET_PATH)
                ?? GameObject.Find("skill_bar@go");
            if (skillBarWidget != null)
            {
                Transform child = skillBarWidget.transform.Find(STATUS_JUMP_BUTTON_CHILD);
                if (child != null)
                {
                    jumpGo = child.gameObject;
                    this.cachedJumpButtonGo = jumpGo;
                    return true;
                }
            }

            GameObject byName = GameObject.Find(STATUS_JUMP_BUTTON_CHILD);
            if (byName != null)
            {
                jumpGo = byName;
                this.cachedJumpButtonGo = jumpGo;
                return true;
            }

            jumpGo = null;
            return false;
        }
    }
}
