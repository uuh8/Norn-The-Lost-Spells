using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.UI.Tests
{
    public sealed class OnlineMovementCursorTests
    {
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void OnlineMenuOrDisconnectUnlocksCursorForActualButtons(bool menuOpen, bool connected)
        {
            var mode = Cursor.lockState; bool visible = Cursor.visible;
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                typeof(DungeonEntryDialogController).GetMethod("Awake", flags).Invoke(dialog, null);
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                typeof(DungeonEntryDialogController).GetMethod("OnGameplay", flags).Invoke(dialog,
                    new object[] { new OnlineMovementPresentationEvent { Active = true, MenuOpen = menuOpen, Connected = connected } });
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None)); Assert.That(Cursor.visible, Is.True);
            }
            finally { Object.DestroyImmediate(root); Cursor.lockState = mode; Cursor.visible = visible; }
        }
    }
}
