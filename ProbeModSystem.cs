using System;
using System.IO;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace LithosProbe;

public class ProbeModSystem : ModSystem
{
	private const string HarmonyId = "com.lithos.probe";

	internal static ProbeCollector Collector { get; private set; }
	internal static ILogger Log { get; private set; }

	private Harmony harmony;
	private ICoreServerAPI sapi;

	public override bool ShouldLoad(EnumAppSide side)
	{
		return side == EnumAppSide.Server;
	}

	public override double ExecuteOrder()
	{
		// Early so the tick hooks are in place before the world starts running.
		return 0.05;
	}

	public override void StartPre(ICoreAPI api)
	{
		// The decoder is embedded in this assembly, so the runtime has to be told how to reach it before anything touches those types.
		DependencyLoader.Install();
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		Log = Mod.Logger;

		Collector = new ProbeCollector(api);
		Collector.Start();

		TickHooks.Bind(Collector);
		harmony = new Harmony(HarmonyId);
		harmony.PatchAll(typeof(ProbeModSystem).Assembly);

		RegisterCommands(api);

		api.Logger.Notification("[Lithos Probe] Health monitor started. Use /probe health for a report.");
	}

	private void RegisterCommands(ICoreServerAPI api)
	{
		CommandArgumentParsers parsers = api.ChatCommands.Parsers;

		api.ChatCommands.Create("probe")
			.WithDescription("Server profiling and health")
			.RequiresPrivilege(Privilege.controlserver)
			.WithRootAlias("lithosprobe")

			.BeginSubCommand("health")
			.WithDescription("Print tick health, memory and world counts")
			.HandleWith(HandleHealth)
			.EndSubCommand()

			.BeginSubCommand("monitor")
			.WithDescription("Turn the health monitor on or off")
			.WithArgs(parsers.OptionalBool("on"))
			.HandleWith(HandleMonitor)
			.EndSubCommand()

			.BeginSubCommand("profiler")
			.WithDescription("Sample every thread for a few seconds and export the call tree")
			.WithArgs(parsers.OptionalInt("seconds"))
			.HandleWith(HandleProfiler)
			.EndSubCommand()

			.BeginSubCommand("export")
			.WithDescription("Write a health document for the Lithos Probe viewer")
			.HandleWith(HandleExport)
			.EndSubCommand();
	}

	private TextCommandResult HandleHealth(TextCommandCallingArgs args)
	{
		if (Collector == null) return TextCommandResult.Error("The probe is not running.");

		return TextCommandResult.Success(ProbeHealthReport.Render(Collector, Mod.Info.Version));
	}

	private TextCommandResult HandleMonitor(TextCommandCallingArgs args)
	{
		if (Collector == null) return TextCommandResult.Error("The probe is not running.");

		if (args.Parsers[0].IsMissing)
		{
			return TextCommandResult.Success("The health monitor is currently " + (Collector.IsMonitoring ? "on" : "off"));
		}

		bool enabled = (bool)args[0];
		Collector.SetMonitoring(enabled);
		return TextCommandResult.Success("The health monitor is now " + (enabled ? "on" : "off"));
	}

	private TextCommandResult HandleProfiler(TextCommandCallingArgs args)
	{
		if (Collector == null) return TextCommandResult.Error("The probe is not running.");
		if (ProbeSampler.IsRunning) return TextCommandResult.Error("A profiling run is already in progress.");

		int seconds = args.Parsers[0].IsMissing ? 10 : (int)args[0];
		if (seconds < 1 || seconds > 300) return TextCommandResult.Error("Choose between 1 and 300 seconds.");

		bool started = ProbeSampler.TryStart(sapi, seconds, delegate (SampleProfile profile)
		{
			try
			{
				string path = ProbePayload.Write(sapi, Collector, Mod.Info.Version, profile);
				long sizeKb = new FileInfo(path).Length / 1024;
				sapi.Logger.Notification(
					"[Lithos Probe] Captured {0} samples across {1} threads in {2:0.0}s. Wrote {3} ({4} kb)",
					profile.TotalSamples, profile.Threads.Count, profile.DurationSeconds, path, sizeKb);
			}
			catch (Exception e)
			{
				sapi.Logger.Error("[Lithos Probe] The profile was captured but could not be written.");
				sapi.Logger.Error(e);
			}
		}, delegate (string message, Exception e)
		{
			sapi.Logger.Error("[Lithos Probe] " + message);
			sapi.Logger.Error(e);
		});

		if (!started) return TextCommandResult.Error("A profiling run is already in progress.");

		return TextCommandResult.Success($"Profiling for {seconds} seconds. The document is written to the Logs folder when it finishes.");
	}

	private TextCommandResult HandleExport(TextCommandCallingArgs args)
	{
		if (Collector == null) return TextCommandResult.Error("The probe is not running.");

		try
		{
			string path = ProbePayload.Write(sapi, Collector, Mod.Info.Version, null);
			long sizeKb = new FileInfo(path).Length / 1024;
			return TextCommandResult.Success($"Wrote {Path.GetFileName(path)} ({sizeKb} kb) to the Logs folder.");
		}
		catch (Exception e)
		{
			sapi.Logger.Error("[Lithos Probe] Could not write the document.");
			sapi.Logger.Error(e);
			return TextCommandResult.Error("Could not write the document, see server-main.log.");
		}
	}

	public override void Dispose()
	{
		TickHooks.Bind(null);
		harmony?.UnpatchAll(HarmonyId);
		harmony = null;

		Collector?.Stop();
		Collector = null;
	}
}
