using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace CatCore.Services.Bilibili.Internal
{
	internal static class LegacySvgRendererBridge
	{
		private const string RendererAssemblyFileName = "CatCore.Svg.dll";
		private const string RendererTypeName = "CatCore.Svg.SvgRenderer";
		private const string RendererMethodName = "TryRenderToPng";

		private static readonly object Sync = new object();
		private static Func<string, string, int, int, bool>? _tryRenderToPng;
		private static bool _initializationAttempted;
		private static string _loadedAssemblyPath = string.Empty;
		private static int _warmUpStarted;
		private static Task? _warmUpTask;

		public static bool TryRenderToPng(string svgTemplate, string outputPath, int width, int height)
		{
			var renderer = GetOrInitializeRenderer();
			if (renderer == null)
			{
				return false;
			}

			try
			{
				return renderer(svgTemplate, outputPath, width, height);
			}
			catch (Exception ex)
			{
				Log.Warning(
					"[BADGE_SVG_BRIDGE] Invoke failed path={Path} ex={ExceptionType}: {Message}",
					_loadedAssemblyPath,
					ex.GetType().Name,
					ex.Message);
				return false;
			}
		}

		public static void WarmUpInBackground()
		{
			if (Interlocked.Exchange(ref _warmUpStarted, 1) != 0)
			{
				return;
			}

			_warmUpTask = Task.Run(() =>
			{
				var stopwatch = Stopwatch.StartNew();
				try
				{
					var renderer = GetOrInitializeRenderer();
					if (renderer == null)
					{
						Log.Warning("[BADGE_SVG_BRIDGE] Warm-up skipped because renderer is unavailable.");
						return;
					}

					var warmUpDirectory = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						".catcore",
						"cache",
						"Badges");
					Directory.CreateDirectory(warmUpDirectory);

					var warmUpPath = Path.Combine(warmUpDirectory, "__badge_warmup.png");
					const string warmUpSvg = "<?xml version=\"1.0\" encoding=\"utf-8\"?><svg version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><rect x=\"0\" y=\"0\" width=\"4\" height=\"4\" fill=\"#00000000\"/></svg>";

					renderer(warmUpSvg, warmUpPath, 4, 4);

					if (File.Exists(warmUpPath))
					{
						File.Delete(warmUpPath);
					}

					Log.Information(
						"[BADGE_SVG_BRIDGE] Warm-up finished elapsed={ElapsedMs}ms path={Path}",
						stopwatch.ElapsedMilliseconds,
						_loadedAssemblyPath);
				}
				catch (Exception ex)
				{
					Log.Warning(
						"[BADGE_SVG_BRIDGE] Warm-up failed ex={ExceptionType}: {Message}",
						ex.GetType().Name,
						ex.Message);
				}
			});
		}

		private static Func<string, string, int, int, bool>? GetOrInitializeRenderer()
		{
			if (_tryRenderToPng != null)
			{
				return _tryRenderToPng;
			}

			lock (Sync)
			{
				if (_tryRenderToPng != null)
				{
					return _tryRenderToPng;
				}

				if (_initializationAttempted)
				{
					return null;
				}

				_initializationAttempted = true;
				var candidatePaths = GetCandidateAssemblyPaths();
				Exception? lastError = null;

				for (var i = 0; i < candidatePaths.Length; i++)
				{
					var candidatePath = candidatePaths[i];
					if (!File.Exists(candidatePath))
					{
						continue;
					}

					try
					{
						var rendererAssembly = Assembly.LoadFrom(candidatePath);
						var rendererType = rendererAssembly.GetType(RendererTypeName, throwOnError: true)!;
						var rendererInstance = Activator.CreateInstance(rendererType);
						var tryRenderMethod = rendererType.GetMethod(
							RendererMethodName,
							BindingFlags.Instance | BindingFlags.Public,
							null,
							new[] { typeof(string), typeof(string), typeof(int), typeof(int) },
							null);

						if (tryRenderMethod == null || rendererInstance == null)
						{
							continue;
						}

						var cachedDelegate = Delegate.CreateDelegate(
							typeof(Func<string, string, int, int, bool>),
							rendererInstance,
							tryRenderMethod,
							throwOnBindFailure: false) as Func<string, string, int, int, bool>;

						if (cachedDelegate == null)
						{
							cachedDelegate = (svgTemplate, outputPath, width, height) =>
							{
								var result = tryRenderMethod.Invoke(rendererInstance, new object[] { svgTemplate, outputPath, width, height });
								return result is bool rendered && rendered;
							};
						}

						_tryRenderToPng = cachedDelegate;
						_loadedAssemblyPath = candidatePath;

						Log.Information(
							"[BADGE_SVG_BRIDGE] Loaded legacy renderer path={Path}",
							_loadedAssemblyPath);

						return _tryRenderToPng;
					}
					catch (Exception ex)
					{
						lastError = ex;
					}
				}

				Log.Warning(
					"[BADGE_SVG_BRIDGE] Legacy renderer unavailable. Candidates={Candidates} lastError={LastError}",
					string.Join(";", candidatePaths),
					lastError == null ? "<none>" : $"{lastError.GetType().Name}: {lastError.Message}");

				return null;
			}
		}

		private static string[] GetCandidateAssemblyPaths()
		{
			return new[]
			{
				Path.Combine(AppContext.BaseDirectory, "Libs", RendererAssemblyFileName),
				Path.Combine(AppContext.BaseDirectory, RendererAssemblyFileName)
			};
		}
	}
}
