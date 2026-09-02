using System;
using System.Globalization;
using System.Text;
using Vintagestory.API.Config;

namespace LithosProbe;

public static class ProbeHealthReport
{
	private static readonly double[] WindowSeconds = [5.0, 60.0, 300.0, 900.0];
	private static readonly string[] WindowNames = ["5s", "1m", "5m", "15m"];

	public static string Render(ProbeCollector collector, string modVersion)
	{
		StringBuilder text = new StringBuilder();
		CultureInfo culture = GlobalConstants.DefaultCultureInfo;

		float tickTimeMs = collector.TickTimeMs;
		text.Append("Lithos Probe ").Append(modVersion).Append(" health, ")
			.Append(GameVersion.LongGameVersion).AppendLine();
		text.Append("Uptime: ").Append(FormatDuration(collector.Uptime))
			.Append(", ").Append(collector.Ticks.TotalTicks.ToString("N0", culture)).AppendLine(" ticks recorded");

		if (!collector.IsMonitoring)
		{
			text.AppendLine("Monitor is off. Use /probe monitor on to resume sampling.");
			return text.ToString();
		}

		double targetTps = tickTimeMs > 0f ? 1000.0 / tickTimeMs : 0.0;
		text.Append("Target: ").Append(targetTps.ToString("0.#", culture)).Append(" tps, ")
			.Append(tickTimeMs.ToString("0.##", culture)).AppendLine(" ms per tick");

		text.AppendLine();
		text.AppendLine("Window     TPS    mean    median     p95     p99     max");
		bool anyPartial = false;
		for (int i = 0; i < WindowSeconds.Length; i++)
		{
			TickWindow window = collector.SummarizeWindow(WindowSeconds[i]);
			if (window.Count == 0) continue;

			bool partial = window.CoveredSeconds < WindowSeconds[i] * 0.9;
			anyPartial |= partial;
			text.Append((WindowNames[i] + (partial ? "*" : "")).PadRight(9))
				.Append(window.TicksPerSecond.ToString("0.0", culture).PadLeft(5))
				.Append(window.MeanMs.ToString("0.00", culture).PadLeft(8))
				.Append(window.MedianMs.ToString("0.00", culture).PadLeft(10))
				.Append(window.P95Ms.ToString("0.00", culture).PadLeft(8))
				.Append(window.P99Ms.ToString("0.00", culture).PadLeft(8))
				.Append(window.MaxMs.ToString("0.00", culture).PadLeft(8))
				.AppendLine();
		}

		if (anyPartial) text.AppendLine("* window is not full yet, rates cover the recorded span only");

		collector.ReadCensus(out int players, out int chunks, out int entities);
		text.AppendLine();
		text.Append("Players: ").Append(players)
			.Append("   Chunks: ").Append(chunks.ToString("N0", culture))
			.Append("   Entities: ").Append(entities.ToString("N0", culture)).AppendLine();
		text.Append("GC mode: ").Append(collector.IsServerGc ? "server" : "workstation").AppendLine();

		return text.ToString();
	}

	private static string FormatDuration(TimeSpan span)
	{
		if (span.TotalDays >= 1.0) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
		if (span.TotalHours >= 1.0) return $"{(int)span.TotalHours}h {span.Minutes}m";
		if (span.TotalMinutes >= 1.0) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
		return $"{(int)span.TotalSeconds}s";
	}
}
