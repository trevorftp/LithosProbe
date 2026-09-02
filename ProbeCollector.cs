using System;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace LithosProbe;

public sealed class ProbeCollector
{
	// 15 minutes of headroom at the default 30 tick rate, which is the longest window reported.
	private const int TickRingCapacity = 30 * 60 * 15;
	private const int PollIntervalMs = 1000;
	private const int CensusEveryTicks = 30;

	private readonly ICoreServerAPI sapi;
	private readonly ServerMain server;
	private readonly TickTimeRing ticks = new TickTimeRing(TickRingCapacity);
	private readonly MetricSeries series = new MetricSeries();
	// Separate scratch per caller. Summarize sorts in place, so the polling thread and a command must not share one.
	private readonly int[] pollScratch = new int[4096];
	private readonly int[] reportScratch = new int[TickRingCapacity];
	private readonly object reportSync = new object();
	private readonly ManualResetEventSlim stopSignal = new ManualResetEventSlim(false);
	private readonly Stopwatch uptime = Stopwatch.StartNew();

	private RuntimeMonitor runtime;
	private Thread pollThread;
	private volatile bool monitoring;

	// Census values, published by the tick thread and read by the polling thread.
	private int players;
	private int loadedChunks;
	private int loadedEntities;
	private int censusCountdown;

	private int lastStatsIndex = -1;
	private long lastStatsPackets;
	private long lastStatsBytes;

	public ProbeCollector(ICoreServerAPI sapi)
	{
		this.sapi = sapi;
		server = sapi.World as ServerMain;
	}

	public bool IsMonitoring => monitoring;

	internal TickTimeRing Ticks => ticks;

	internal MetricSeries Series => series;

	internal TimeSpan Uptime => uptime.Elapsed;

	internal float TickTimeMs => server?.Config?.TickTime ?? 33.3333f;

	internal bool IsServerGc => runtime?.IsServerGc ?? false;

	internal void Start()
	{
		if (pollThread != null) return;

		monitoring = true;
		runtime = new RuntimeMonitor();
		pollThread = new Thread(Poll) { Name = "lithosprobe", IsBackground = true };
		pollThread.Start();
	}

	internal void Stop()
	{
		if (pollThread == null) return;

		monitoring = false;
		stopSignal.Set();
		pollThread = null;
	}

	public void SetMonitoring(bool enabled)
	{
		monitoring = enabled;
	}

	public void RecordTick(long durationTicks)
	{
		if (!monitoring) return;
		if (durationTicks < 0) durationTicks = 0;

		int durationUs = (int)Math.Min(int.MaxValue, durationTicks * 1_000_000 / Stopwatch.Frequency);
		ticks.Record(durationUs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

		// The census reads collections the tick thread already owns, so it happens here rather than on the polling thread where a chunk load could tear the read.
		if (--censusCountdown > 0) return;

		censusCountdown = CensusEveryTicks;
		try
		{
			players = sapi.World.AllOnlinePlayers.Length;
			loadedChunks = sapi.WorldManager.AllLoadedChunks.Count;
			loadedEntities = sapi.World.LoadedEntities.Count;
		}
		catch (Exception)
		{
			// The world can be torn down underneath a late tick. A stale census is probably fine.
		}
	}

	private void Poll()
	{
		while (!stopSignal.Wait(PollIntervalMs))
		{
			try
			{
				if (monitoring) Collect();
			}
			catch (Exception e)
			{
				ProbeModSystem.Log?.Error("[Lithos Probe] Health monitor sample failed, monitor stopped.");
				ProbeModSystem.Log?.Error(e);
				monitoring = false;
			}
		}

		runtime?.Dispose();
		runtime = null;
	}

	private void Collect()
	{
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		TickWindow window = ticks.Summarize(nowMs, 1.0, pollScratch);
		RuntimeSample sample = runtime.Sample();
		ReadNetworkRates(out double packetsPerSecond, out double kbPerSecond);

		Span<double> values = stackalloc double[MetricSeries.FieldCount];
		values[MetricSeries.TicksPerSecond] = window.TicksPerSecond;
		values[MetricSeries.MsptMean] = window.MeanMs;
		values[MetricSeries.MsptP95] = window.P95Ms;
		values[MetricSeries.MsptMax] = window.MaxMs;
		values[MetricSeries.CpuPercent] = sample.ProcessCpuPercent;
		values[MetricSeries.WorkingSetMb] = sample.WorkingSetBytes / 1048576.0;
		values[MetricSeries.ManagedHeapMb] = sample.ManagedHeapBytes / 1048576.0;
		values[MetricSeries.AllocationMbPerSecond] = sample.AllocationBytesPerSecond / 1048576.0;
		values[MetricSeries.GcPausePercent] = sample.GcPausePercent;
		values[MetricSeries.Gen0PerSecond] = sample.Gen0PerSecond;
		values[MetricSeries.Gen1PerSecond] = sample.Gen1PerSecond;
		values[MetricSeries.Gen2PerSecond] = sample.Gen2PerSecond;
		values[MetricSeries.Players] = Volatile.Read(ref players);
		values[MetricSeries.LoadedChunks] = Volatile.Read(ref loadedChunks);
		values[MetricSeries.LoadedEntities] = Volatile.Read(ref loadedEntities);
		values[MetricSeries.PacketsPerSecond] = packetsPerSecond;
		values[MetricSeries.NetworkKbPerSecond] = kbPerSecond;

		series.Append(nowMs, values);
	}

	/// <summary>
	/// Vanilla rotates 4 stats buckets every 2 seconds. Reading the completed bucket avoids the 1 being filled.
	/// </summary>
	private void ReadNetworkRates(out double packetsPerSecond, out double kbPerSecond)
	{
		packetsPerSecond = 0.0;
		kbPerSecond = 0.0;
		if (server == null) return;

		StatsCollection[] collector = server.StatsCollector;
		if (collector == null || collector.Length == 0) return;

		int index = (server.StatsCollectorIndex - 1 + collector.Length) % collector.Length;
		StatsCollection stats = collector[index];
		if (stats == null) return;

		long packets = stats.statTotalPackets + stats.statTotalUdpPackets;
		long bytes = stats.statTotalPacketsLength + stats.statTotalUdpPacketsLength;
		if (index == lastStatsIndex)
		{
			// Same bucket as last second, so report the growth rather than counting it twice.
			packetsPerSecond = Math.Max(0L, packets - lastStatsPackets);
			kbPerSecond = Math.Max(0L, bytes - lastStatsBytes) / 1024.0;
		}
		else
		{
			// A completed bucket covers 2 seconds of traffic.
			packetsPerSecond = packets / 2.0;
			kbPerSecond = bytes / 2.0 / 1024.0;
		}

		lastStatsIndex = index;
		lastStatsPackets = packets;
		lastStatsBytes = bytes;
	}

	/// <summary>
	/// Summarizes a reporting window. Serialized because 2 operators can ask for a report at the same time.
	/// </summary>
	internal TickWindow SummarizeWindow(double windowSeconds)
	{
		lock (reportSync)
		{
			return ticks.Summarize(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), windowSeconds, reportScratch);
		}
	}

	internal void ReadCensus(out int playerCount, out int chunkCount, out int entityCount)
	{
		playerCount = Volatile.Read(ref players);
		chunkCount = Volatile.Read(ref loadedChunks);
		entityCount = Volatile.Read(ref loadedEntities);
	}
}
