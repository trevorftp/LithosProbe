using System;

namespace LithosProbe;

public sealed class TierSnapshot
{
	public int SpanSeconds;
	public long[] Times;
	public float[][] Columns;
}

/// <summary>
/// Retained time series for the health monitor, kept at three resolutions.
/// Samples are stored column wise so the export can emit one array per metric.
/// </summary>
public sealed class MetricSeries
{
	public const int TicksPerSecond = 0;
	public const int MsptMean = 1;
	public const int MsptP95 = 2;
	public const int MsptMax = 3;
	public const int CpuPercent = 4;
	public const int WorkingSetMb = 5;
	public const int ManagedHeapMb = 6;
	public const int AllocationMbPerSecond = 7;
	public const int GcPausePercent = 8;
	public const int Gen0PerSecond = 9;
	public const int Gen1PerSecond = 10;
	public const int Gen2PerSecond = 11;
	public const int Players = 12;
	public const int LoadedChunks = 13;
	public const int LoadedEntities = 14;
	public const int PacketsPerSecond = 15;
	public const int NetworkKbPerSecond = 16;
	public const int FieldCount = 17;

	public static readonly string[] FieldNames =
	[
		"tps",
		"msptMean",
		"msptP95",
		"msptMax",
		"cpuPercent",
		"workingSetMb",
		"managedHeapMb",
		"allocationMbPerSecond",
		"gcPausePercent",
		"gen0PerSecond",
		"gen1PerSecond",
		"gen2PerSecond",
		"players",
		"loadedChunks",
		"loadedEntities",
		"packetsPerSecond",
		"networkKbPerSecond"
	];

	private sealed class Tier
	{
		public readonly int SpanSeconds;
		public readonly int GroupSize;
		public readonly int Capacity;
		public readonly long[] Times;
		public readonly float[] Values;
		public readonly double[] Accumulator = new double[FieldCount];
		public int AccumulatedCount;
		public int WriteIndex;
		public int Count;

		public Tier(int spanSeconds, int groupSize, int capacity)
		{
			SpanSeconds = spanSeconds;
			GroupSize = groupSize;
			Capacity = capacity;
			Times = new long[capacity];
			Values = new float[capacity * FieldCount];
		}

		public void Add(long unixMs, ReadOnlySpan<double> values)
		{
			int offset = WriteIndex * FieldCount;
			for (int i = 0; i < FieldCount; i++)
			{
				Values[offset + i] = (float)values[i];
			}

			Times[WriteIndex] = unixMs;
			WriteIndex = (WriteIndex + 1) % Capacity;
			if (Count < Capacity) Count++;
		}

		public TierSnapshot Snapshot()
		{
			float[][] columns = new float[FieldCount][];
			for (int field = 0; field < FieldCount; field++)
			{
				columns[field] = new float[Count];
			}

			long[] times = new long[Count];
			int start = (WriteIndex - Count + Capacity) % Capacity;
			for (int i = 0; i < Count; i++)
			{
				int index = (start + i) % Capacity;
				times[i] = Times[index];
				int offset = index * FieldCount;
				for (int field = 0; field < FieldCount; field++)
				{
					columns[field][i] = Values[offset + field];
				}
			}

			return new TierSnapshot { SpanSeconds = SpanSeconds, Times = times, Columns = columns };
		}
	}

	private readonly Tier[] tiers;
	private readonly object sync = new object();
	private readonly double[] carry = new double[FieldCount];

	public MetricSeries()
	{
		// 1 second for 10 minutes, 10 seconds for 2 hours, 1 minute for a day.
		tiers =
		[
			new Tier(1, 1, 600),
			new Tier(10, 10, 720),
			new Tier(60, 6, 1440)
		];
	}

	public int TierCount => tiers.Length;

	/// <summary>
	/// Appends 1 second of measurements and cascades the averages into the coarser tiers.
	/// Each coarse sample carries the timestamp of the newest measurement inside it.
	/// </summary>
	public void Append(long unixMs, ReadOnlySpan<double> values)
	{
		lock (sync)
		{
			tiers[0].Add(unixMs, values);
			values.CopyTo(carry);

			for (int t = 1; t < tiers.Length; t++)
			{
				Tier tier = tiers[t];
				for (int i = 0; i < FieldCount; i++)
				{
					tier.Accumulator[i] += carry[i];
				}

				if (++tier.AccumulatedCount < tier.GroupSize) break;

				for (int i = 0; i < FieldCount; i++)
				{
					carry[i] = tier.Accumulator[i] / tier.AccumulatedCount;
					tier.Accumulator[i] = 0.0;
				}

				tier.AccumulatedCount = 0;
				tier.Add(unixMs, carry);
			}
		}
	}

	public TierSnapshot Snapshot(int tierIndex)
	{
		lock (sync)
		{
			return tiers[tierIndex].Snapshot();
		}
	}
}
