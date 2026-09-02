using System;
using System.Diagnostics;
using System.Runtime;

namespace LithosProbe;

/// <summary>
/// Process and garbage collector counters for one polling interval.
/// </summary>
public struct RuntimeSample
{
	public double ProcessCpuPercent;
	public long WorkingSetBytes;
	public long ManagedHeapBytes;
	public long CommittedBytes;
	public double AllocationBytesPerSecond;
	public double Gen0PerSecond;
	public double Gen1PerSecond;
	public double Gen2PerSecond;
	public double GcPausePercent;
}

/// <summary>
/// Turns the runtime's cumulative counters into per interval rates.
/// </summary>
public sealed class RuntimeMonitor : IDisposable
{
	private readonly Process process = Process.GetCurrentProcess();
	private readonly int processorCount = Math.Max(1, Environment.ProcessorCount);
	private readonly Stopwatch elapsed = Stopwatch.StartNew();

	private TimeSpan lastCpuTime;
	private long lastAllocatedBytes;
	private long lastElapsedTicks;
	private int lastGen0;
	private int lastGen1;
	private int lastGen2;
	private bool primed;

	public bool IsServerGc => GCSettings.IsServerGC;

	/// <summary>
	/// Reads the counters and converts them to rates over the time since the previous call.
	/// </summary>
	public RuntimeSample Sample()
	{
		long nowTicks = elapsed.ElapsedTicks;
		double seconds = (nowTicks - lastElapsedTicks) / (double)Stopwatch.Frequency;
		lastElapsedTicks = nowTicks;

		process.Refresh();
		TimeSpan cpuTime = process.TotalProcessorTime;
		long allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
		int gen0 = GC.CollectionCount(0);
		int gen1 = GC.CollectionCount(1);
		int gen2 = GC.CollectionCount(2);
		GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();

		RuntimeSample sample = default;
		sample.WorkingSetBytes = process.WorkingSet64;
		sample.ManagedHeapBytes = memoryInfo.HeapSizeBytes;
		sample.CommittedBytes = memoryInfo.TotalCommittedBytes;
		sample.GcPausePercent = memoryInfo.PauseTimePercentage;

		if (primed && seconds > 0.0)
		{
			sample.ProcessCpuPercent = (cpuTime - lastCpuTime).TotalSeconds / seconds / processorCount * 100.0;
			sample.AllocationBytesPerSecond = (allocatedBytes - lastAllocatedBytes) / seconds;
			sample.Gen0PerSecond = (gen0 - lastGen0) / seconds;
			sample.Gen1PerSecond = (gen1 - lastGen1) / seconds;
			sample.Gen2PerSecond = (gen2 - lastGen2) / seconds;
		}

		lastCpuTime = cpuTime;
		lastAllocatedBytes = allocatedBytes;
		lastGen0 = gen0;
		lastGen1 = gen1;
		lastGen2 = gen2;
		primed = true;

		return sample;
	}

	public void Dispose()
	{
		process.Dispose();
	}
}
