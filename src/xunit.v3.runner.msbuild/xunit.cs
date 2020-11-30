using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Runner.v2;
using Xunit.v3;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Xunit.Runner.MSBuild
{
	public class xunit : MSBuildTask, ICancelableTask
	{
		volatile bool cancel;
		readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();
		XunitFilters? filters;
		IRunnerLogger? logger;
		int? maxThreadCount;
		bool? parallelizeAssemblies;
		bool? parallelizeTestCollections;
		_IMessageSink? reporterMessageHandler;
		bool? shadowCopy;
		bool? stopOnFail;

		public string? AppDomains { get; set; }

		[Required]
		public ITaskItem[]? Assemblies { get; set; }

		public bool DiagnosticMessages { get; set; }

		public string? ExcludeTraits { get; set; }

		[Output]
		public int ExitCode { get; protected set; }

		public bool FailSkips { get; set; }

		protected XunitFilters Filters
		{
			get
			{
				if (filters == null)
				{
					var traitParser = new TraitParser(msg => Log.LogWarning(msg));
					filters = new XunitFilters();
					traitParser.Parse(IncludeTraits, filters.IncludedTraits);
					traitParser.Parse(ExcludeTraits, filters.ExcludedTraits);
				}

				return filters;
			}
		}

		public ITaskItem? Html { get; set; }

		public bool IgnoreFailures { get; set; }

		public string? IncludeTraits { get; set; }

		public bool InternalDiagnosticMessages { get; set; }

		public ITaskItem? JUnit { get; set; }

		public string? MaxParallelThreads { get; set; }

		protected bool NeedsXml =>
			Xml != null || XmlV1 != null || Html != null || NUnit != null || JUnit != null;

		public bool NoAutoReporters { get; set; }

		public bool NoLogo { get; set; }

		public ITaskItem? NUnit { get; set; }

		public bool ParallelizeAssemblies { set { parallelizeAssemblies = value; } }

		public bool ParallelizeTestCollections { set { parallelizeTestCollections = value; } }

		public bool PreEnumerateTheories { get; set; }

		public string? Reporter { get; set; }

		// To be used by the xUnit.net team for diagnostic purposes only
		public bool SerializeTestCases { get; set; }

		public bool ShadowCopy { set { shadowCopy = value; } }

		public bool StopOnFail { set { stopOnFail = value; } }

		public string? WorkingFolder { get; set; }

		public ITaskItem? Xml { get; set; }

		public ITaskItem? XmlV1 { get; set; }

		public void Cancel()
		{
			cancel = true;
		}

		public override bool Execute()
		{
			Guard.ArgumentNotNull(nameof(Assemblies), Assemblies);

			RemotingUtility.CleanUpRegisteredChannels();

			XElement? assembliesElement = null;
			var environment = $"{IntPtr.Size * 8}-bit {$"Desktop .NET {Environment.Version}"}";

			if (NeedsXml)
				assembliesElement = new XElement("assemblies");

			var appDomains = default(AppDomainSupport?);
			switch (AppDomains?.ToLowerInvariant())
			{
				case null:
					break;

				case "ifavailable":
					appDomains = AppDomainSupport.IfAvailable;
					break;

				case "true":
				case "required":
					appDomains = AppDomainSupport.Required;
					break;

				case "false":
				case "denied":
					appDomains = AppDomainSupport.Denied;
					break;

				default:
					Log.LogError("AppDomains value '{0}' is invalid: must be 'ifavailable', 'required', or 'denied'", AppDomains);
					return false;
			}

			switch (MaxParallelThreads)
			{
				case null:
				case "default":
					break;

				case "unlimited":
					maxThreadCount = -1;
					break;

				default:
					int threadValue;
					if (!int.TryParse(MaxParallelThreads, out threadValue) || threadValue < 1)
					{
						Log.LogError("MaxParallelThreads value '{0}' is invalid: must be 'default', 'unlimited', or a positive number", MaxParallelThreads);
						return false;
					}

					maxThreadCount = threadValue;
					break;
			}

			var originalWorkingFolder = Directory.GetCurrentDirectory();
			var internalDiagnosticsMessageSink = DiagnosticMessageSink.ForInternalDiagnostics(Log, InternalDiagnosticMessages);

			using (AssemblyHelper.SubscribeResolveForAssembly(typeof(xunit), internalDiagnosticsMessageSink))
			{
				var reporter = GetReporter();
				if (reporter == null)
					return false;

				logger = new MSBuildLogger(Log);
				reporterMessageHandler = reporter.CreateMessageHandler(logger);

				if (!NoLogo)
					Log.LogMessage(MessageImportance.High, $"xUnit.net v3 MSBuild Runner v{ThisAssembly.AssemblyInformationalVersion} ({environment})");

				var project = new XunitProject();
				foreach (var assembly in Assemblies)
				{
					var assemblyFileName = assembly.GetMetadata("FullPath");
					var configFileName = assembly.GetMetadata("ConfigFile");
					if (configFileName != null && configFileName.Length == 0)
						configFileName = null;

					var projectAssembly = new XunitProjectAssembly { AssemblyFilename = assemblyFileName, ConfigFilename = configFileName };
					if (shadowCopy.HasValue)
						projectAssembly.Configuration.ShadowCopy = shadowCopy;

					project.Add(projectAssembly);
				}

				if (WorkingFolder != null)
					Directory.SetCurrentDirectory(WorkingFolder);

				var clockTime = Stopwatch.StartNew();

				if (!parallelizeAssemblies.HasValue)
					parallelizeAssemblies = project.All(assembly => assembly.Configuration.ParallelizeAssemblyOrDefault);

				if (parallelizeAssemblies.GetValueOrDefault())
				{
					var tasks = project.Assemblies.Select(assembly => Task.Run(() => ExecuteAssembly(assembly, appDomains).AsTask()));
					var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
					foreach (var assemblyElement in results.Where(result => result != null))
						assembliesElement!.Add(assemblyElement);
				}
				else
				{
					foreach (var assembly in project.Assemblies)
					{
						var assemblyElement = ExecuteAssembly(assembly, appDomains);
						if (assemblyElement != null)
							assembliesElement!.Add(assemblyElement);
					}
				}

				clockTime.Stop();

				if (assembliesElement != null)
					assembliesElement.Add(new XAttribute("timestamp", DateTime.Now.ToString(CultureInfo.InvariantCulture)));

				if (completionMessages.Count > 0)
				{
					var summaries = new TestExecutionSummaries { ElapsedClockTime = clockTime.Elapsed };
					foreach (var completionMessage in completionMessages.OrderBy(kvp => kvp.Key))
						summaries.Add(completionMessage.Key, completionMessage.Value);
					reporterMessageHandler.OnMessage(summaries);
				}
			}

			Directory.SetCurrentDirectory(WorkingFolder ?? originalWorkingFolder);

			if (NeedsXml && assembliesElement != null)
			{
				if (Xml != null)
					TransformFactory.Transform("xml", assembliesElement, Xml.GetMetadata("FullPath"));

				if (XmlV1 != null)
					TransformFactory.Transform("xmlv1", assembliesElement, XmlV1.GetMetadata("FullPath"));

				if (Html != null)
					TransformFactory.Transform("html", assembliesElement, Html.GetMetadata("FullPath"));

				if (NUnit != null)
					TransformFactory.Transform("nunit", assembliesElement, NUnit.GetMetadata("FullPath"));

				if (JUnit != null)
					TransformFactory.Transform("junit", assembliesElement, JUnit.GetMetadata("FullPath"));
			}

			// ExitCode is set to 1 for test failures and -1 for Exceptions.
			return ExitCode == 0 || (ExitCode == 1 && IgnoreFailures);
		}

		protected virtual async ValueTask<XElement?> ExecuteAssembly(
			XunitProjectAssembly assembly,
			AppDomainSupport? appDomains)
		{
			if (cancel)
				return null;

			var assemblyElement = NeedsXml ? new XElement("assembly") : null;

			try
			{
				assembly.Configuration.PreEnumerateTheories = PreEnumerateTheories;
				assembly.Configuration.DiagnosticMessages |= DiagnosticMessages;
				assembly.Configuration.InternalDiagnosticMessages |= InternalDiagnosticMessages;

				if (appDomains.HasValue)
					assembly.Configuration.AppDomain = appDomains;

				// Setup discovery and execution options with command-line overrides
				var discoveryOptions = _TestFrameworkOptions.ForDiscovery(assembly.Configuration);
				var executionOptions = _TestFrameworkOptions.ForExecution(assembly.Configuration);
				if (maxThreadCount.HasValue && maxThreadCount.Value > -1)
					executionOptions.SetMaxParallelThreads(maxThreadCount);
				if (parallelizeTestCollections.HasValue)
					executionOptions.SetDisableParallelization(!parallelizeTestCollections);
				if (stopOnFail.HasValue)
					executionOptions.SetStopOnTestFail(stopOnFail);

				var assemblyDisplayName = Path.GetFileNameWithoutExtension(assembly.AssemblyFilename)!;
				var diagnosticMessageSink = DiagnosticMessageSink.ForDiagnostics(Log, assemblyDisplayName, assembly.Configuration.DiagnosticMessagesOrDefault);
				var appDomainSupport = assembly.Configuration.AppDomainOrDefault;
				var shadowCopy = assembly.Configuration.ShadowCopyOrDefault;
				var longRunningSeconds = assembly.Configuration.LongRunningTestSecondsOrDefault;

				await using var controller = new XunitFrontController(appDomainSupport, assembly.AssemblyFilename!, assembly.ConfigFilename, shadowCopy, diagnosticMessageSink: diagnosticMessageSink);
				using var discoverySink = new TestDiscoverySink(() => cancel);

				// Discover & filter the tests
				var appDomainOption = controller.CanUseAppDomains && appDomainSupport != AppDomainSupport.Denied ? AppDomainOption.Enabled : AppDomainOption.Disabled;
				var discoveryStarting = new TestAssemblyDiscoveryStarting
				{
					AppDomain = appDomainOption,
					Assembly = assembly,
					DiscoveryOptions = discoveryOptions,
					ShadowCopy = shadowCopy
				};
				reporterMessageHandler!.OnMessage(discoveryStarting);

				controller.Find(false, discoverySink, discoveryOptions);
				discoverySink.Finished.WaitOne();

				var testCasesDiscovered = discoverySink.TestCases.Count;
				var filteredTestCases = discoverySink.TestCases.Where(Filters.Filter).ToList();
				var testCasesToRun = filteredTestCases.Count;

				var discoveryFinished = new TestAssemblyDiscoveryFinished
				{
					Assembly = assembly,
					DiscoveryOptions = discoveryOptions,
					TestCasesDiscovered = testCasesDiscovered,
					TestCasesToRun = testCasesToRun
				};
				reporterMessageHandler!.OnMessage(discoveryFinished);

				// Run the filtered tests
				if (testCasesToRun == 0)
					completionMessages.TryAdd(controller.TestAssemblyUniqueID, new ExecutionSummary());
				else
				{
					if (SerializeTestCases)
						filteredTestCases = (
							from testCase in filteredTestCases
							select controller.Deserialize(controller.Serialize(testCase))
						).ToList();

					IExecutionSink resultsSink = new DelegatingExecutionSummarySink(reporterMessageHandler!, () => cancel, (summary, _) => completionMessages.TryAdd(controller.TestAssemblyUniqueID, summary));
					if (assemblyElement != null)
						resultsSink = new DelegatingXmlCreationSink(resultsSink, assemblyElement);
					if (longRunningSeconds > 0)
						resultsSink = new DelegatingLongRunningTestDetectionSink(resultsSink, TimeSpan.FromSeconds(longRunningSeconds), diagnosticMessageSink);
					if (FailSkips)
						resultsSink = new DelegatingFailSkipSink(resultsSink);

					var executionStarting = new TestAssemblyExecutionStarting
					{
						Assembly = assembly,
						ExecutionOptions = executionOptions
					};
					reporterMessageHandler!.OnMessage(executionStarting);

					controller.RunTests(filteredTestCases, resultsSink, executionOptions);
					resultsSink.Finished.WaitOne();

					var executionFinished = new TestAssemblyExecutionFinished
					{
						Assembly = assembly,
						ExecutionOptions = executionOptions,
						ExecutionSummary = resultsSink.ExecutionSummary
					};
					reporterMessageHandler!.OnMessage(executionFinished);

					if (resultsSink.ExecutionSummary.Failed != 0 || resultsSink.ExecutionSummary.Errors != 0)
					{
						ExitCode = 1;
						if (stopOnFail == true)
						{
							Log.LogMessage(MessageImportance.High, "Canceling due to test failure...");
							Cancel();
						}
					}
				}
			}
			catch (Exception ex)
			{
				var e = ex;

				while (e != null)
				{
					Log.LogError("{0}: {1}", e.GetType().FullName, e.Message);

					if (e.StackTrace != null)
						foreach (var stackLine in e.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
							Log.LogError(stackLine);

					e = e.InnerException;
				}

				ExitCode = -1;
			}

			return assemblyElement;
		}

		protected virtual List<IRunnerReporter> GetAvailableRunnerReporters()
		{
			var result = new List<IRunnerReporter>();
			var runnerPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().GetLocalCodeBase())!;

			foreach (var dllFile in Directory.GetFiles(runnerPath, "*.dll").Select(f => Path.Combine(runnerPath, f)))
			{
				Type[] types;

				try
				{
					var assembly = Assembly.LoadFile(dllFile);
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types ?? new Type[0];
				}
				catch
				{
					continue;
				}

				foreach (var type in types)
				{
					if (type == null || type.IsAbstract || type == typeof(DefaultRunnerReporter) || !type.GetInterfaces().Any(t => t == typeof(IRunnerReporter)))
						continue;

					var ctor = type.GetConstructor(new Type[0]);
					if (ctor == null)
					{
						Log.LogWarning("Type {0} in assembly {1} appears to be a runner reporter, but does not have an empty constructor.", type.FullName, dllFile);
						continue;
					}

					result.Add((IRunnerReporter)ctor.Invoke(new object[0]));
				}
			}

			return result;
		}

		protected IRunnerReporter? GetReporter()
		{
			var reporters = GetAvailableRunnerReporters();
			IRunnerReporter? reporter = null;
			if (!NoAutoReporters)
				reporter = reporters.FirstOrDefault(r => r.IsEnvironmentallyEnabled);

			if (reporter == null && !string.IsNullOrWhiteSpace(Reporter))
			{
				reporter = reporters.FirstOrDefault(r => string.Equals(r.RunnerSwitch, Reporter, StringComparison.OrdinalIgnoreCase));
				if (reporter == null)
				{
					var switchableReporters =
						reporters
							.Where(r => !string.IsNullOrWhiteSpace(r.RunnerSwitch))
							.Select(r => r.RunnerSwitch!.ToLowerInvariant())
							.OrderBy(x => x)
							.ToList();

					if (switchableReporters.Count == 0)
						Log.LogError("Reporter value '{0}' is invalid. There are no available reporters.", Reporter);
					else
						Log.LogError("Reporter value '{0}' is invalid. Available reporters: {1}", Reporter, string.Join(", ", switchableReporters));

					return null;
				}
			}

			return reporter ?? new DefaultRunnerReporter();
		}
	}
}
