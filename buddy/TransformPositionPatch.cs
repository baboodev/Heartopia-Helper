using System;
using HarmonyLib;
using UnityEngine;

namespace HeartopiaMod
{
    // Installed manually on the Transform.position SETTER by EnsurePositionOverridePatched —
    // no [HarmonyPatch] attribute on purpose, a PatchAll must never pick this up.
    public static class TransformPositionPatch
    {
		public static bool SetPositionPrefix(Transform __instance, ref Vector3 value)
		{
			if (!HeartopiaComplete.OverridePlayerPosition && !HeartopiaComplete.OverrideCameraPosition)
			{
				return true;
			}

			// This prefix sits on a per-frame hot path inside native callers; an exception
			// escaping it can take down the process, so never let one out. Targets are matched
			// by Transform instance id (refreshed each frame in OnUpdate, filled lazily below
			// for the same-frame enable case) — no gameObject.name string fetch per call.
			try
			{
				if (__instance == null)
				{
					return true;
				}
				int id = __instance.GetInstanceID();
				if (HeartopiaComplete.OverridePlayerPosition)
				{
					int playerId = HeartopiaComplete.OverridePlayerTransformId;
					if (playerId == 0)
					{
						GameObject local = HeartopiaComplete.GetLocalPlayer();
						playerId = local != null ? local.transform.GetInstanceID() : 0;
						HeartopiaComplete.OverridePlayerTransformId = playerId;
					}
					if (playerId != 0 && id == playerId)
					{
						value = HeartopiaComplete.OverridePosition;
					}
				}
				if (HeartopiaComplete.OverrideCameraPosition)
				{
					int cameraId = HeartopiaComplete.OverrideCameraTransformId;
					if (cameraId == 0)
					{
						Camera cam = Camera.main;
						cameraId = cam != null ? cam.transform.GetInstanceID() : 0;
						HeartopiaComplete.OverrideCameraTransformId = cameraId;
					}
					if (cameraId != 0 && id == cameraId)
					{
						value = HeartopiaComplete.CameraOverridePos;
					}
				}
			}
			catch
			{
			}
			return true;
        }
    }
}
