using System;
using HarmonyLib;
using UnityEngine;

namespace HeartopiaMod
{
    // Token: 0x02000006 RID: 6
    [HarmonyPatch(typeof(Transform), "position", MethodType.Getter)]
    public static class TransformPositionPatch
    {
        // Token: 0x0600002C RID: 44 RVA: 0x00008344 File Offset: 0x00006544
		public static bool SetPositionPrefix(Transform __instance, ref Vector3 value)
		{
			if (!HeartopiaComplete.OverridePlayerPosition && !HeartopiaComplete.OverrideCameraPosition)
			{
				return true;
			}

			// This prefix sits on a per-frame hot path inside native callers; an exception
			// escaping it can take down the process, so never let one out.
			try
			{
				bool flag = __instance == null || __instance.gameObject == null;
				if (!flag)
				{
					// Only override the local player's position (avoid affecting other players with the same name)
					string gname = __instance.gameObject.name;
					if (HeartopiaComplete.OverridePlayerPosition)
					{
						GameObject local = HeartopiaComplete.GetLocalPlayer();
						bool flag2 = __instance.gameObject == local;
						if (flag2)
						{
							value = HeartopiaComplete.OverridePosition;
						}
					}
					bool flag3 = HeartopiaComplete.OverrideCameraPosition && gname == "Main Camera";
					if (flag3)
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
