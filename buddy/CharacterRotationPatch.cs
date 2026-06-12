using System;
using HarmonyLib;
using UnityEngine;

namespace HeartopiaMod
{
    // Installed manually on the Transform.rotation SETTER by EnsureRotationOverridePatched —
    // no [HarmonyPatch] attribute on purpose, a PatchAll must never pick this up.
    public static class CharacterRotationPatch
    {
        public static bool SetRotationPrefix(Transform __instance, ref Quaternion value)
        {
            if (!HeartopiaComplete.OverridePlayerRotation)
            {
                return true;
            }

            // Hot-path prefix called from native code — an escaping exception is fatal.
            // Player skeleton matched by Transform instance id; see TransformPositionPatch.
            try
            {
                if (__instance == null) return true;

                int playerId = HeartopiaComplete.OverridePlayerTransformId;
                if (playerId == 0)
                {
                    GameObject local = HeartopiaComplete.GetLocalPlayer();
                    playerId = local != null ? local.transform.GetInstanceID() : 0;
                    HeartopiaComplete.OverridePlayerTransformId = playerId;
                }
                if (playerId != 0 && __instance.GetInstanceID() == playerId)
                {
                    // Replace the value being set with our override rotation
                    value = HeartopiaComplete.PlayerOverrideRot;
                }
            }
            catch
            {
            }

            return true; // Continue with setter using our modified value
        }
    }
}
