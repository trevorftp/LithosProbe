using System;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.Server;

namespace LithosProbe;

// The only patches this mod applies. Both target public methods and only add prefix and postfix code, so vanilla behaviour is untouched!!
internal static class TickHooks
{
	private static ProbeCollector collector;

	[ThreadStatic]
	private static long tickStart;

	[ThreadStatic]
	private static bool armed;

	internal static void Bind(ProbeCollector target)
	{
		collector = target;
	}

	[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.Process))]
	internal static class ProcessPatch
	{
		private static void Prefix()
		{
			if (collector == null) return;

			tickStart = Stopwatch.GetTimestamp();
			armed = true;
		}
	}

	// This is to keep sleep out of the numbers.
	[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.ProcessMain))]
	internal static class ProcessMainPatch
	{
		private static void Postfix()
		{
			ProbeCollector target = collector;
			if (target == null || !armed) return;

			armed = false;
			target.RecordTick(Stopwatch.GetTimestamp() - tickStart);
		}
	}
}
