#if MELONLOADER
using MelonLoader;

[assembly: MelonInfo(typeof(HeartopiaMod.HeartopiaMelonPlugin), "Heartopia Helper", "1.0.0", "HeartopiaMod")]
[assembly: MelonGame(null, null)]

namespace HeartopiaMod
{
    public class HeartopiaMelonPlugin : MelonMod
    {
        private HeartopiaComplete _mod;

        public override void OnInitializeMelon()
        {
            _mod = new HeartopiaComplete();
            _mod.OnInitializeMelon();
        }

        public override void OnLateUpdate() => _mod?.OnLateUpdate();

        public override void OnUpdate() => _mod?.OnUpdate();

        public override void OnGUI() => _mod?.OnGUI();

        public override void OnDeinitializeMelon() => _mod?.OnDeinitializeMelon();
    }
}
#endif
