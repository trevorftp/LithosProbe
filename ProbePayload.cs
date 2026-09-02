using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace LithosProbe;

public static class ProbePayload
{
	public const int SchemaVersion = 2;

	private static readonly double[] WindowSeconds = [5.0, 60.0, 300.0, 900.0];
	private static readonly string[] WindowNames = ["5s", "1m", "5m", "15m"];

	/// <summary>
	/// Writes a gzipped JSON document into the log directory and returns its full path.
	/// </summary>
	/// <summary>
	/// Writes a gzipped JSON document into the log directory and returns its full path.
	/// The sampled call tree is included when a profiling run produced one.
	/// </summary>
	public static string Write(ICoreServerAPI sapi, ProbeCollector collector, string modVersion, SampleProfile profile)
	{
		string directory = GamePaths.Logs;
		Directory.CreateDirectory(directory);

		string path = Path.Combine(directory, "lithosprobe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json.gz");
		using FileStream file = File.Create(path);
		using GZipStream compressed = new GZipStream(file, CompressionLevel.Optimal);
		using StreamWriter text = new StreamWriter(compressed);
		using JsonTextWriter json = new JsonTextWriter(text) { Culture = CultureInfo.InvariantCulture };

		json.WriteStartObject();
		json.WritePropertyName("schema");
		json.WriteValue(SchemaVersion);
		json.WritePropertyName("kind");
		json.WriteValue("lithos-probe");
		json.WritePropertyName("generatedAt");
		json.WriteValue(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

		WriteServer(json, sapi, collector, modVersion);
		WriteMods(json, sapi);
		WriteWindows(json, collector);
		WriteCensus(json, collector);
		WriteSeries(json, collector.Series);
		if (profile != null) WriteProfile(json, profile);

		json.WriteEndObject();
		json.Flush();

		return path;
	}

	private static void WriteServer(JsonTextWriter json, ICoreServerAPI sapi, ProbeCollector collector, string modVersion)
	{
		json.WritePropertyName("server");
		json.WriteStartObject();
		WriteMember(json, "lithosVersion", "Probe " + modVersion);
		WriteMember(json, "gameVersion", GameVersion.LongGameVersion);
		WriteMember(json, "runtime", RuntimeInformation.FrameworkDescription);
		WriteMember(json, "os", RuntimeInformation.OSDescription);
		WriteMember(json, "architecture", RuntimeInformation.ProcessArchitecture.ToString());
		WriteMember(json, "processorCount", Environment.ProcessorCount);
		WriteMember(json, "serverGc", collector.IsServerGc);
		WriteMember(json, "uptimeSeconds", (long)collector.Uptime.TotalSeconds);
		WriteMember(json, "totalTicks", collector.Ticks.TotalTicks);
		WriteMember(json, "tickTimeMs", collector.TickTimeMs);
		WriteMember(json, "maxClients", sapi.Server.Config?.MaxClients ?? 0);
		json.WriteEndObject();
	}

	private static void WriteMods(JsonTextWriter json, ICoreServerAPI sapi)
	{
		json.WritePropertyName("mods");
		json.WriteStartArray();

		IModLoader loader = sapi?.ModLoader;
		if (loader != null)
		{
			foreach (Mod mod in loader.Mods)
			{
				json.WriteStartObject();
				WriteMember(json, "id", mod.Info?.ModID);
				WriteMember(json, "name", mod.Info?.Name);
				WriteMember(json, "version", mod.Info?.Version);
				WriteMember(json, "side", mod.Info?.Side.ToString());
				json.WriteEndObject();
			}
		}

		json.WriteEndArray();
	}

	private static void WriteWindows(JsonTextWriter json, ProbeCollector collector)
	{
		json.WritePropertyName("windows");
		json.WriteStartArray();
		for (int i = 0; i < WindowSeconds.Length; i++)
		{
			TickWindow window = collector.SummarizeWindow(WindowSeconds[i]);
			json.WriteStartObject();
			WriteMember(json, "name", WindowNames[i]);
			WriteMember(json, "seconds", WindowSeconds[i]);
			WriteMember(json, "coveredSeconds", window.CoveredSeconds);
			WriteMember(json, "ticks", window.Count);
			WriteMember(json, "tps", window.TicksPerSecond);
			WriteMember(json, "meanMs", window.MeanMs);
			WriteMember(json, "medianMs", window.MedianMs);
			WriteMember(json, "p95Ms", window.P95Ms);
			WriteMember(json, "p99Ms", window.P99Ms);
			WriteMember(json, "maxMs", window.MaxMs);
			json.WriteEndObject();
		}

		json.WriteEndArray();
	}

	private static void WriteCensus(JsonTextWriter json, ProbeCollector collector)
	{
		collector.ReadCensus(out int players, out int chunks, out int entities);
		json.WritePropertyName("census");
		json.WriteStartObject();
		WriteMember(json, "players", players);
		WriteMember(json, "loadedChunks", chunks);
		WriteMember(json, "loadedEntities", entities);
		json.WriteEndObject();
	}

	private static void WriteSeries(JsonTextWriter json, MetricSeries series)
	{
		json.WritePropertyName("series");
		json.WriteStartObject();

		json.WritePropertyName("fields");
		json.WriteStartArray();
		foreach (string name in MetricSeries.FieldNames)
		{
			json.WriteValue(name);
		}

		json.WriteEndArray();

		json.WritePropertyName("tiers");
		json.WriteStartArray();
		for (int tier = 0; tier < series.TierCount; tier++)
		{
			TierSnapshot snapshot = series.Snapshot(tier);
			json.WriteStartObject();
			WriteMember(json, "spanSeconds", snapshot.SpanSeconds);
			WriteMember(json, "count", snapshot.Times.Length);

			json.WritePropertyName("times");
			json.WriteStartArray();
			foreach (long time in snapshot.Times)
			{
				json.WriteValue(time);
			}

			json.WriteEndArray();

			json.WritePropertyName("values");
			json.WriteStartObject();
			for (int field = 0; field < MetricSeries.FieldCount; field++)
			{
				json.WritePropertyName(MetricSeries.FieldNames[field]);
				json.WriteStartArray();
				foreach (float value in snapshot.Columns[field])
				{
					json.WriteValue(Math.Round(value, 3));
				}

				json.WriteEndArray();
			}

			json.WriteEndObject();
			json.WriteEndObject();
		}

		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static void WriteProfile(JsonTextWriter json, SampleProfile profile)
	{
		json.WritePropertyName("profile");
		json.WriteStartObject();
		WriteMember(json, "durationSeconds", Math.Round(profile.DurationSeconds, 2));
		WriteMember(json, "totalSamples", profile.TotalSamples);
		WriteMember(json, "managedSamples", profile.ManagedSamples);
		WriteMember(json, "intervalMs", profile.IntervalMs);

		json.WritePropertyName("threads");
		json.WriteStartArray();
		foreach (SampleThread thread in profile.Threads)
		{
			// Drop frames below a thousandth of the thread, which keeps a deep tree from filling the document with noise that no reader can act on.
			int floor = Math.Max(1, thread.Samples / 1000);

			json.WriteStartObject();
			WriteMember(json, "name", thread.Name);
			WriteMember(json, "samples", thread.Samples);
			WriteMember(json, "managed", thread.ManagedSamples);
			WriteMember(json, "parked", thread.ParkedSamples);
			json.WritePropertyName("children");
			WriteNodes(json, thread.Root, floor, 0);
			json.WriteEndObject();
		}

		json.WriteEndArray();

		WriteTally(json, "mods", "id", profile.SelfByMod);
		WriteTally(json, "modules", "name", profile.SelfByModule);
		json.WriteEndObject();
	}

	private static void WriteNodes(JsonTextWriter json, SampleNode parent, int floor, int depth)
	{
		json.WriteStartArray();
		if (parent.Children != null && depth < 128)
		{
			foreach (SampleNode child in parent.Children.Values)
			{
				if (child.Total < floor) continue;

				json.WriteStartObject();
				WriteMember(json, "name", ProbeSampler.ShortName(child.Name));
				WriteMember(json, "full", child.Name);
				WriteMember(json, "module", child.Module);
				if (child.Mod != null) WriteMember(json, "mod", child.Mod);
				WriteMember(json, "total", child.Total);
				WriteMember(json, "self", child.Self);
				WriteMember(json, "selfManaged", child.SelfManaged);
				json.WritePropertyName("children");
				WriteNodes(json, child, floor, depth + 1);
				json.WriteEndObject();
			}
		}

		json.WriteEndArray();
	}

	private static void WriteTally(JsonTextWriter json, string property, string keyName, Dictionary<string, int> tally)
	{
		json.WritePropertyName(property);
		json.WriteStartArray();
		foreach (KeyValuePair<string, int> entry in tally)
		{
			json.WriteStartObject();
			WriteMember(json, keyName, entry.Key);
			WriteMember(json, "self", entry.Value);
			json.WriteEndObject();
		}

		json.WriteEndArray();
	}

	private static void WriteMember(JsonTextWriter json, string name, object value)
	{
		json.WritePropertyName(name);
		json.WriteValue(value);
	}
}
