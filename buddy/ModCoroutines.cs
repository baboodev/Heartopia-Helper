using System.Collections;
using System.Collections.Generic;
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

    // GC roots for in-flight coroutines. WrapToIl2Cpp() bridges our managed IEnumerator into an
    // Il2CppManagedEnumerator that Unity drives via a MoveNext trampoline every frame. il2cpp holds
    // that bridge, but the coreclr GC cannot see il2cpp's reference — so once Start() returns,
    // nothing on the managed side keeps the bridge (and the iterator it stores) alive. A GC
    // triggered by our own AuraMono allocations mid-coroutine then collects it, and the next
    // trampoline MoveNext dereferences a dead managed target → NULL read inside coreclr (recurring
    // crash coreclr.dll+0x1D1FDD). Rooting the bridge here for the coroutine's lifetime fixes it;
    // the bridge transitively keeps the wrapped iterator alive.
    private static readonly HashSet<object> _liveRoots = new HashSet<object>();
    private static readonly Dictionary<object, object> _wrapperByHandle = new Dictionary<object, object>();

    public static void SetHost(MonoBehaviour host) => _host = host;
#endif

    public static object Start(IEnumerator routine)
    {
#if MELONLOADER
        return MelonCoroutines.Start(routine);
#elif BEPINEX
        if (_host == null || routine == null)
        {
            return null;
        }

        // Outer iterator removes the roots in its finally when the coroutine ends on its own (most
        // do — they null their handle without calling Stop), preventing a slow leak. holder[0] =
        // the il2cpp bridge (rooted in _liveRoots), holder[1] = the Coroutine handle (added to
        // _wrapperByHandle, whose value also roots the bridge, so both must be cleared).
        object[] tokenHolder = new object[2];
        IEnumerator tracked = TrackRoutine(routine, tokenHolder);
        Il2CppSystem.Collections.IEnumerator wrapped = tracked.WrapToIl2Cpp();
        tokenHolder[0] = wrapped;
        _liveRoots.Add(wrapped);

        Coroutine handle = _host.StartCoroutine(wrapped);
        if (handle != null)
        {
            tokenHolder[1] = handle;
            _wrapperByHandle[handle] = wrapped;
        }
        return handle;
#else
        return null;
#endif
    }

#if BEPINEX
    private static IEnumerator TrackRoutine(IEnumerator inner, object[] tokenHolder)
    {
        try
        {
            while (true)
            {
                // MoveNext is the il2cpp → coreclr boundary; an exception escaping it can corrupt
                // the trampoline, so swallow per-step faults instead of throwing across.
                bool moved;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (System.Exception ex)
                {
                    ModEntryGuard.Report("Coroutine", ex);
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                yield return inner.Current;
            }
        }
        finally
        {
            if (tokenHolder != null)
            {
                if (tokenHolder[0] != null)
                {
                    _liveRoots.Remove(tokenHolder[0]);
                }
                if (tokenHolder[1] != null)
                {
                    _wrapperByHandle.Remove(tokenHolder[1]);
                }
            }
        }
    }
#endif

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

        if (_wrapperByHandle.TryGetValue(coroutine, out object wrapper))
        {
            _liveRoots.Remove(wrapper);
            _wrapperByHandle.Remove(coroutine);
        }
#endif
    }
}
