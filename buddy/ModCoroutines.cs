using System.Collections;
using UnityEngine;

#if MELONLOADER
using MelonLoader;
#elif BEPINEX
using BepInEx.Unity.IL2CPP.Utils.Collections;
#endif

public static class ModCoroutines
{
#if BEPINEX
    private static MonoBehaviour _host;

    public static void SetHost(MonoBehaviour host) => _host = host;
#endif

    public static object Start(IEnumerator routine)
    {
#if MELONLOADER
        return MelonCoroutines.Start(routine);
#elif BEPINEX
        return _host != null ? _host.StartCoroutine(routine.WrapToIl2Cpp()) : null;
#else
        return null;
#endif
    }

    public static void Stop(object coroutine)
    {
        if (coroutine == null)
        {
            return;
        }

#if MELONLOADER
        MelonCoroutines.Stop(coroutine);
#elif BEPINEX
        if (coroutine is Coroutine unityCoroutine)
        {
            _host?.StopCoroutine(unityCoroutine);
        }
#endif
    }
}
