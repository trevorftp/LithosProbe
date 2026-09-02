using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LithosProbe;

// CPU sampling profiler. The server opens an EventPipe session against its own process.
// Which is the only way that yields real managed stacks from inside the process, then folds the samples into a merged call tree.

// Every reference to the diagnostics libraries lives in this file.
public static class ProbeSampler
{
	private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";
	private const string TempDirectoryVariable = "LITHOSPROBE_TEMP";

	// The sampler emits a sample for every thread on every interval, so time has to be classified.
	// The runtime tags each sample Managed or External. External covers every native frame. 
	// So these leaf names narrow that down to threads that were parked rather than doing native work.
	private static readonly string[] ParkedFrames =
	[
		"Thread.Sleep",
		"Monitor.Wait",
		"WaitOneNoCheck",
		"WaitForSingleObject",
		"WaitForMultipleObjects",
		"GetQueuedCompletionStatus",
		"LowLevelLifoSemaphore",
		"SpinThenBlockingWait",
		"ManualResetEventSlim.Wait",
		"epoll",
		"poll"
	];

	private static int running;

	public static bool IsRunning => Volatile.Read(ref running) != 0;

	public static bool TryStart(ICoreServerAPI sapi, int seconds, Action<SampleProfile> onFinished, Action<string, Exception> onFailed)
	{
		if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;

		Thread worker = new Thread(delegate ()
		{
			try
			{
				SampleProfile profile = Capture(sapi, seconds);
				onFinished(profile);
			}
			catch (Exception e)
			{
				onFailed("The profiler could not capture a session.", e);
			}
			finally
			{
				Volatile.Write(ref running, 0);
			}
		});

		worker.Name = "lithosprofiler";
		worker.IsBackground = true;
		worker.Start();
		return true;
	}

	private static SampleProfile Capture(ICoreServerAPI sapi, int seconds)
	{
		string directory = ProfileDirectory(sapi);
		string stem = Path.Combine(
			directory,
			"lithosprobe-profile-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
		string nettrace = stem + ".nettrace";
		string etlx = stem + ".etlx";
		Stopwatch elapsed = Stopwatch.StartNew();

		try
		{
			DiagnosticsClient client = new DiagnosticsClient(Environment.ProcessId);
			EventPipeProvider[] providers = [new EventPipeProvider(SampleProvider, EventLevel.Informational)];

			using (EventPipeSession session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 128))
			{
				Thread copy = new Thread(delegate ()
				{
					using FileStream file = File.Create(nettrace);
					session.EventStream.CopyTo(file);
				});
				copy.IsBackground = true;
				copy.Start();

				Thread.Sleep(seconds * 1000);
				session.Stop();
				copy.Join(30000);
			}

			double duration = elapsed.Elapsed.TotalSeconds;
			TraceLog.CreateFromEventPipeDataFile(nettrace, etlx, new TraceLogOptions());
			using TraceLog log = new TraceLog(etlx);
			return Fold(log, BuildModMap(sapi), duration);
		}
		finally
		{
			Delete(nettrace);
			Delete(etlx);
			// TraceEvent writes through this staging file. In particular a full disk leaves it behind oops.
			Delete(etlx + ".new");
		}
	}

	private static string ProfileDirectory(ICoreServerAPI sapi)
	{
		string configured = Environment.GetEnvironmentVariable(TempDirectoryVariable);
		if (!string.IsNullOrWhiteSpace(configured))
		{
			string directory = Path.GetFullPath(configured);
			Directory.CreateDirectory(directory);
			return directory;
		}

		return sapi.GetOrCreateDataPath("LithosProbe");
	}

	/// <summary>
	/// Maps an assembly name to the mod that supplied it, which is how a frame gets blamed on a mod.
	/// </summary>
	private static Dictionary<string, string> BuildModMap(ICoreServerAPI sapi)
	{
		Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		IModLoader loader = sapi?.ModLoader;
		if (loader == null) return map;

		foreach (Mod mod in loader.Mods)
		{
			string id = mod.Info?.ModID;
			if (id == null || mod.Systems == null) continue;

			foreach (ModSystem system in mod.Systems)
			{
				string assembly = system.GetType().Assembly.GetName().Name;
				if (assembly != null) map[assembly] = id;
			}
		}

		return map;
	}

	private static SampleProfile Fold(TraceLog log, Dictionary<string, string> modByAssembly, double duration)
	{
		SampleProfile profile = new SampleProfile { DurationSeconds = duration };
		Dictionary<int, SampleThread> byThread = new Dictionary<int, SampleThread>();
		TraceCallStacks stacks = log.CallStacks;
		List<string> names = new List<string>(64);
		List<string> modules = new List<string>(64);

		foreach (TraceEvent data in log.Events)
		{
			if (data.ProviderName != SampleProvider) continue;

			CallStackIndex leaf = data.CallStackIndex();
			if (leaf == CallStackIndex.Invalid) continue;

			names.Clear();
			modules.Clear();
			for (CallStackIndex i = leaf; i != CallStackIndex.Invalid; i = stacks.Caller(i))
			{
				CodeAddressIndex address = stacks.CodeAddressIndex(i);
				MethodIndex method = stacks.CodeAddresses.MethodIndex(address);
				string name = method != MethodIndex.Invalid
					? stacks.CodeAddresses.Methods.FullMethodName(method)
					: stacks.CodeAddresses.Name(address);

				names.Add(string.IsNullOrEmpty(name) ? "(unresolved)" : name);
				modules.Add(stacks.CodeAddresses.ModuleFile(address)?.Name ?? "");
			}

			if (names.Count == 0) continue;

			// Field zero of Thread/Sample should be the runtime's own Managed or External classification.
			bool managed = "Managed".Equals(data.PayloadValue(0)?.ToString(), StringComparison.Ordinal);

			if (!byThread.TryGetValue(data.ThreadID, out SampleThread thread))
			{
				byThread[data.ThreadID] = thread = new SampleThread
				{
					Name = "thread " + data.ThreadID,
					Root = new SampleNode("root", "")
				};
			}

			profile.TotalSamples++;
			thread.Samples++;
			thread.Root.Total++;
			if (managed)
			{
				profile.ManagedSamples++;
				thread.ManagedSamples++;
			}
			else if (IsParked(names[0]))
			{
				thread.ParkedSamples++;
			}

			SampleNode node = thread.Root;
			for (int i = names.Count - 1; i >= 0; i--)
			{
				node = node.Child(names[i], modules[i]);
				node.Total++;
			}

			node.Self++;
			if (managed) node.SelfManaged++;

			string module = modules[0];
			if (module.Length > 0)
			{
				profile.SelfByModule[module] = profile.SelfByModule.GetValueOrDefault(module) + 1;
				if (modByAssembly.TryGetValue(module, out string mod))
				{
					profile.SelfByMod[mod] = profile.SelfByMod.GetValueOrDefault(mod) + 1;
				}
			}
		}

		foreach (SampleThread thread in byThread.Values)
		{
			Label(thread, modByAssembly);
			profile.Threads.Add(thread);
		}

		// Busiest threads first, since that is the order an operator wants to read them in.
		profile.Threads.Sort(delegate (SampleThread a, SampleThread b)
		{
			int byManaged = b.ManagedSamples.CompareTo(a.ManagedSamples);
			return byManaged != 0 ? byManaged : b.Samples.CompareTo(a.Samples);
		});

		return profile;
	}

	/// <summary>
	/// Names a thread after the deepest frame its whole stack shares. Traces carry no thread names, and the entry frame identifies a server thread better than an operating system id does.
	/// </summary>
	private static void Label(SampleThread thread, Dictionary<string, string> modByAssembly)
	{
		SampleNode node = thread.Root;
		SampleNode best = null;
		while (node.Children != null && node.Children.Count == 1)
		{
			foreach (SampleNode only in node.Children.Values)
			{
				node = only;
			}

			best = node;
			if (node.Self > 0) break;
		}

		if (best != null) thread.Name = ShortName(best.Name);
		Attribute(thread.Root, modByAssembly);
	}

	private static void Attribute(SampleNode node, Dictionary<string, string> modByAssembly)
	{
		if (node.Module != null && node.Module.Length > 0 && modByAssembly.TryGetValue(node.Module, out string mod))
		{
			node.Mod = mod;
		}

		if (node.Children == null) return;

		foreach (SampleNode child in node.Children.Values)
		{
			Attribute(child, modByAssembly);
		}
	}

	/// <summary>
	/// Trims a full signature down to Type.Method, which is what reads in a tree.
	/// </summary>
	public static string ShortName(string fullName)
	{
		if (string.IsNullOrEmpty(fullName)) return fullName;

		int parenthesis = fullName.IndexOf('(');
		string withoutArgs = parenthesis > 0 ? fullName.Substring(0, parenthesis) : fullName;

		int lastDot = withoutArgs.LastIndexOf('.');
		if (lastDot <= 0) return withoutArgs;

		int typeDot = withoutArgs.LastIndexOf('.', lastDot - 1);
		return typeDot < 0 ? withoutArgs : withoutArgs.Substring(typeDot + 1);
	}

	private static bool IsParked(string leafName)
	{
		foreach (string frame in ParkedFrames)
		{
			if (leafName.Contains(frame, StringComparison.Ordinal)) return true;
		}

		return false;
	}

	private static void Delete(string path)
	{
		if (path == null) return;

		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
