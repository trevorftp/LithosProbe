using System.Collections.Generic;

namespace LithosProbe;

public sealed class SampleNode
{
	public string Name;
	public string Module;
	public string Mod;
	public int Total;
	public int Self;
	// Self samples where the runtime reported the thread was executing managed code.
	public int SelfManaged;
	public Dictionary<string, SampleNode> Children;

	public SampleNode(string name, string module)
	{
		Name = name;
		Module = module;
	}

	public SampleNode Child(string name, string module)
	{
		Children ??= new Dictionary<string, SampleNode>();
		if (!Children.TryGetValue(name, out SampleNode child))
		{
			Children[name] = child = new SampleNode(name, module);
		}

		return child;
	}
}

// The .NET sampler emits a sample for every thread on every interval, so a thread's percentages are only meaningful against its own sample count.
public sealed class SampleThread
{
	public string Name;
	public int Samples;
	// Samples the runtime classified as managed, which is the closest thing to on cpu.
	public int ManagedSamples;
	// Native samples whose leaf frame is a known parking call, so the thread was waiting.
	public int ParkedSamples;
	public SampleNode Root;
}

public sealed class SampleProfile
{
	public double DurationSeconds;
	public int TotalSamples;
	public int ManagedSamples;
	public int IntervalMs = 1;
	public List<SampleThread> Threads = new List<SampleThread>();
	public Dictionary<string, int> SelfByMod = new Dictionary<string, int>();
	public Dictionary<string, int> SelfByModule = new Dictionary<string, int>();
}
