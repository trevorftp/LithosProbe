using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace LithosProbe;

// This stays lazy. The bytes sit in the file untouched until these assemblies are needed, which only happens when the profiler runs.
internal static class DependencyLoader
{
	private const string Prefix = "LithosProbe.Deps.";

	private static readonly string[] Owned =
	[
		"Microsoft.Diagnostics.NETCore.Client",
		"Microsoft.Diagnostics.Tracing.TraceEvent",
		"Microsoft.Diagnostics.FastSerialization",
		"Microsoft.Extensions.Logging.Abstractions",
		"Microsoft.Extensions.DependencyInjection.Abstractions",
		"Dia2Lib",
		"TraceReloggerLib"
	];

	private static bool installed;

	internal static void Install()
	{
		if (installed) return;
		installed = true;

		AssemblyLoadContext.Default.Resolving += Resolve;
	}

	private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
	{
		if (!IsOwned(name.Name)) return null;

		Assembly self = typeof(DependencyLoader).Assembly;
		using Stream stream = self.GetManifestResourceStream(Prefix + name.Name + ".dll");
		if (stream != null)
		{
			try
			{
				return context.LoadFromStream(stream);
			}
			catch (Exception e)
			{
				ProbeModSystem.Log?.Error("[Lithos Probe] Could not load embedded {0}.", name.Name);
				ProbeModSystem.Log?.Error(e);
				return null;
			}
		}

		// Nothing should reach here in a packaged build, so say so rather than failing silently.
		Assembly beside = LoadBeside(context, name.Name);
		if (beside == null)
		{
			ProbeModSystem.Log?.Error(
				"[Lithos Probe] {0} is neither embedded in the mod nor present beside it. The mod file looks incomplete.",
				name.Name);
		}

		return beside;
	}

	private static Assembly LoadBeside(AssemblyLoadContext context, string name)
	{
		try
		{
			string directory = Path.GetDirectoryName(typeof(DependencyLoader).Assembly.Location);
			if (string.IsNullOrEmpty(directory)) return null;

			string path = Path.Combine(directory, name + ".dll");
			return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static bool IsOwned(string name)
	{
		foreach (string owned in Owned)
		{
			if (string.Equals(owned, name, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}
}
