#if BEPINEX
using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace HeartopiaMod
{
    [BepInPlugin(HeartopiaBepInPlugin.PluginGuid, HeartopiaBepInPlugin.PluginName, HeartopiaBepInPlugin.PluginVersion)]
    public class HeartopiaBepInPlugin : BasePlugin
    {
        public const string PluginGuid = "com.heartopia.helper";
        public const string PluginName = "Heartopia Helper";
        public const string PluginVersion = "1.0.0";

        public override void Load()
        {
            ModLogger.Init(Log);
            AddComponent<HeartopiaBehaviour>();
        }
    }

    public class HeartopiaBehaviour : MonoBehaviour
    {
        private HeartopiaComplete _mod;

        public HeartopiaBehaviour(IntPtr ptr)
            : base(ptr)
        {
        }

        private void Awake()
        {
            ModCoroutines.SetHost(this);
            _mod = new HeartopiaComplete();
            _mod.OnInitializeMelon();
            ModLogger.Msg("HeartopiaBehaviour Awake — Update/OnGUI active on BepInEx manager.");
        }

        private void Update() => _mod?.OnUpdate();

        private void LateUpdate() => _mod?.OnLateUpdate();

        private void OnGUI() => _mod?.OnGUI();

        private void OnDestroy()
        {
            ModLogger.Msg("HeartopiaBehaviour OnDestroy — shutting down mod.");
            _mod?.OnDeinitializeMelon();
        }
    }
}
#endif
