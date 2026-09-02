using System;
using System.Threading;

namespace LithosProbe;

/// <summary>
/// Summary of the tick durations that finished inside one time window.
/// </summary>
public readonly struct TickWindow
{
	public readonly int Count;
	public readonly double CoveredSeconds;
	public readonly double TicksPerSecond;
	public readonly double MeanMs;
	public readonly double MedianMs;
	public readonly double P95Ms;
	public readonly double P99Ms;
	public readonly double MaxMs;

	public TickWindow(int count, double coveredSeconds, double ticksPerSecond, double meanMs, double medianMs, double p95Ms, double p99Ms, double maxMs)
	{
		Count = count;
		CoveredSeconds = coveredSeconds;
		TicksPerSecond = ticksPerSecond;
		MeanMs = meanMs;
		MedianMs = medianMs;
		P95Ms = p95Ms;
		P99Ms = p99Ms;
		MaxMs = maxMs;
	}
}

/// <summary>
/// Fixed size ring of completed tick durations, written only by the thread that owns the tick.
/// </summary>
public sealed class TickTimeRing
{
	private readonly int[] durationsUs;
	private readonly long[] endTimesMs;
	private readonly int capacity;
	private int writeIndex;
	private long totalTicks;

	public TickTimeRing(int capacity)
	{
		this.capacity = capacity;
		durationsUs = new int[capacity];
		endTimesMs = new long[capacity];
	}

	public int Capacity => capacity;

	public long TotalTicks => Volatile.Read(ref totalTicks);

	public void Record(int durationUs, long endTimeMs)
	{
		int index = writeIndex;
		durationsUs[index] = durationUs;
		endTimesMs[index] = endTimeMs;
		totalTicks++;
		// Publish last, so a reader never treats a half written slot as readable.
		Volatile.Write(ref writeIndex, (index + 1) % capacity);
	}

	/// <summary>
	/// Copies the durations of every tick that finished at or after the given time into scratch, newest first.
	/// </summary>
	public int CopyWindow(long sinceMs, int[] scratch, out long oldestMs)
	{
		int published = Volatile.Read(ref writeIndex);
		int limit = Math.Min(capacity, scratch.Length);
		int copied = 0;
		oldestMs = 0L;
		while (copied < limit)
		{
			int index = published - 1 - copied;
			if (index < 0) index += capacity;
			// An unwritten slot carries an end time of zero, which also ends the walk.
			long endTime = endTimesMs[index];
			if (endTime < sinceMs) break;
			oldestMs = endTime;
			scratch[copied++] = durationsUs[index];
		}

		return copied;
	}

	/// <summary>
	/// Summarizes the ticks that finished within the last windowSeconds. Sorts the scratch buffer in place.
	/// </summary>
	public TickWindow Summarize(long nowMs, double windowSeconds, int[] scratch)
	{
		int count = CopyWindow(nowMs - (long)(windowSeconds * 1000.0), scratch, out long oldestMs);
		if (count == 0) return new TickWindow(0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

		long totalUs = 0;
		for (int i = 0; i < count; i++)
		{
			totalUs += scratch[i];
		}

		Array.Sort(scratch, 0, count);

		// A server up for 1 minute must not report its 15 minute window as if the missing 14 minutes were idle.
		double covered = Math.Clamp((nowMs - oldestMs) / 1000.0, 1.0, windowSeconds);

		return new TickWindow(
			count,
			covered,
			count / covered,
			totalUs / (double)count / 1000.0,
			Percentile(scratch, count, 0.50),
			Percentile(scratch, count, 0.95),
			Percentile(scratch, count, 0.99),
			scratch[count - 1] / 1000.0);
	}

	private static double Percentile(int[] sorted, int count, double fraction)
	{
		int index = (int)Math.Ceiling(fraction * count) - 1;
		if (index < 0) index = 0;
		if (index >= count) index = count - 1;
		return sorted[index] / 1000.0;
	}
}
