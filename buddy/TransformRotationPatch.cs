using System;
using HarmonyLib;
using UnityEngine;

namespace HeartopiaMod
{
    // Installed manually on the Transform.rotation SETTER by EnsureRotationOverridePatched —
    // no [HarmonyPatch] attribute on purpose, a PatchAll must never pick this up.
    public static class TransformRotationPatch
    {
		public static bool SetRotationPrefix(Transform __instance, ref Quaternion value)
		{
			if (!HeartopiaComplete.OverrideCameraPosition)
			{
				return true;
			}

			// Hot-path prefix called from native code — an escaping exception is fatal.
			// Camera matched by Transform instance id; see TransformPositionPatch.
			try
			{
				if (__instance == null)
				{
					return true;
				}
				int cameraId = HeartopiaComplete.OverrideCameraTransformId;
				if (cameraId == 0)
				{
					Camera cam = Camera.main;
					cameraId = cam != null ? cam.transform.GetInstanceID() : 0;
					HeartopiaComplete.OverrideCameraTransformId = cameraId;
				}
				if (cameraId != 0 && __instance.GetInstanceID() == cameraId)
				{
					value = HeartopiaComplete.CameraOverrideRot;
				}
			}
			catch
			{
			}
			return true;
        }
    }
}
