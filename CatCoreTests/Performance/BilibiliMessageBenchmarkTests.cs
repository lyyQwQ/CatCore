using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using CatCore.Models.Bilibili;
using CatCore.Services.Bilibili.Interfaces;
using Serilog;
using Xunit;

namespace CatCoreTests.Performance
{
	public sealed class BilibiliMessageBenchmarkTests
	{
		private const int WarmupCount = 100;
		private const int MeasureCount = 1_000;
		private static readonly string[] PerfFrameFiles =
		{
			"frame_danmu_v1.bin",
			"frame_gift_v1.bin",
			"frame_sc_v1.bin",
		};
		private const string BenchmarkFrameFile = "frame_danmu_v1.bin";

		[Fact]
		public void PerfDataset_UsesAtLeast3RecordedFrames()
		{
			var harness = CreateHarness();
			var frames = harness.LoadFrames(PerfFrameFiles);

			harness.Counter.Reset();
			for (var i = 0; i < frames.Length; i++)
			{
				harness.Invoke(frames[i]);
			}

			Assert.True(harness.Counter.TextCount > 0 || harness.Counter.DeletedCount > 0,
				"No messages/events were produced by the processing pipeline for the perf dataset");
		}

		[Fact]
		public void SingleMessage_ProcessingTime_Under500Microseconds()
		{
			var harness = CreateHarness();
			var frame = harness.LoadFrame(BenchmarkFrameFile);

			// Warmup
			for (var i = 0; i < WarmupCount; i++)
			{
				harness.Invoke(frame);
			}

			var samplesUs = new double[MeasureCount];
			for (var i = 0; i < MeasureCount; i++)
			{
				var start = Stopwatch.GetTimestamp();
				harness.Invoke(frame);
				var end = Stopwatch.GetTimestamp();
				samplesUs[i] = TicksToMicroseconds(end - start);
			}

			var p95 = Percentile(samplesUs, 0.95);
			Assert.True(p95 < 500,
				$"P95 processing time too high: p95={p95:F2}us (limit 500us), min={samplesUs.Min():F2}us, avg={samplesUs.Average():F2}us, max={samplesUs.Max():F2}us");
			Assert.True(harness.Counter.TextCount > 0, "No text messages were produced by the processing pipeline");
		}

		[Fact]
		public void SingleMessage_GCAllocation_Under1KB()
		{
			var harness = CreateHarness();
			var frame = harness.LoadFrame(BenchmarkFrameFile);

			// Warmup
			for (var i = 0; i < WarmupCount; i++)
			{
				harness.Invoke(frame);
			}

			const int iterations = 100;
			var bytes = new long[iterations];
			harness.Counter.Reset();
			for (var i = 0; i < iterations; i++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				harness.Invoke(frame);
				var after = GC.GetAllocatedBytesForCurrentThread();
				bytes[i] = after - before;
			}

			var avg = bytes.Average();
			Assert.True(avg <= 1024,
				$"Average allocated bytes too high: avg={avg:F0} bytes (limit 1024), min={bytes.Min()} max={bytes.Max()} count={iterations}");
			Assert.True(harness.Counter.TextCount > 0, "No text messages were produced by the processing pipeline");
		}

		[Fact]
		public void Throughput_50MessagesPerSecond_NoBackpressure()
		{
			var harness = CreateHarness();
			var frame = harness.LoadFrame(BenchmarkFrameFile);

			// Warmup
			for (var i = 0; i < WarmupCount; i++)
			{
				harness.Invoke(frame);
			}

			harness.Counter.Reset();
			const int count = 1_000;
			var start = Stopwatch.GetTimestamp();
			for (var i = 0; i < count; i++)
			{
				harness.Invoke(frame);
			}
			var end = Stopwatch.GetTimestamp();

			var seconds = (end - start) / (double)Stopwatch.Frequency;
			var msgPerSecond = count / seconds;
			Assert.True(msgPerSecond >= 50,
				$"Throughput too low: {msgPerSecond:F2} msg/s (limit 50), totalMs={(seconds * 1000):F2}ms count={count}");
			Assert.Equal(count, harness.Counter.TextCount);
		}

		[Fact]
		public void Throughput_BurstOf100Messages_CompletesUnder2Seconds()
		{
			var harness = CreateHarness();
			var frame = harness.LoadFrame(BenchmarkFrameFile);

			// Warmup
			for (var i = 0; i < WarmupCount; i++)
			{
				harness.Invoke(frame);
			}

			harness.Counter.Reset();
			const int count = 100;
			var sw = Stopwatch.StartNew();
			for (var i = 0; i < count; i++)
			{
				harness.Invoke(frame);
			}
			sw.Stop();

			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
				$"Burst processing too slow: elapsedMs={sw.Elapsed.TotalMilliseconds:F2}ms (limit 2000ms) count={count}");
			Assert.Equal(count, harness.Counter.TextCount);
		}

		private static double TicksToMicroseconds(long ticks)
		{
			return ticks * 1_000_000d / Stopwatch.Frequency;
		}

		private static double Percentile(double[] samples, double p)
		{
			if (samples == null || samples.Length == 0)
			{
				return 0;
			}

			var copy = (double[])samples.Clone();
			Array.Sort(copy);
			var index = (int)Math.Ceiling(copy.Length * p) - 1;
			if (index < 0)
			{
				index = 0;
			}
			if (index >= copy.Length)
			{
				index = copy.Length - 1;
			}
			return copy[index];
		}

		private static Harness CreateHarness()
		{
			var catCoreAssembly = typeof(BilibiliMessage).Assembly;
			var bilibiliServiceType = catCoreAssembly.GetType("CatCore.Services.Bilibili.BilibiliService", throwOnError: true)!;
			var protocolCodecAdapterType = catCoreAssembly.GetType("CatCore.Services.Bilibili.Layered.BilibiliProtocolCodecAdapter", throwOnError: true)!;
			var messageNormalizerType = catCoreAssembly.GetType("CatCore.Services.Bilibili.Layered.BilibiliMessageNormalizer", throwOnError: true)!;

			var service = FormatterServices.GetUninitializedObject(bilibiliServiceType);
			var logger = CreateSilentLogger();
			var protocolCodec = Activator.CreateInstance(protocolCodecAdapterType, nonPublic: true)!;
			var messageNormalizer = Activator.CreateInstance(messageNormalizerType, nonPublic: true)!;

			SetPrivateField(bilibiliServiceType, service, "_logger", logger);
			SetPrivateField(bilibiliServiceType, service, "_protocolCodec", protocolCodec);
			SetPrivateField(bilibiliServiceType, service, "_messageNormalizer", messageNormalizer);
			SetPrivateField(bilibiliServiceType, service, "_isStarted", true);
			SetPrivateField(bilibiliServiceType, service, "_currentChannel", new BilibiliChannel("1", "1"));

			var counter = new MessageCounter();
			var onTextMessageReceived = bilibiliServiceType.GetEvent("OnTextMessageReceived", BindingFlags.Instance | BindingFlags.Public);
			if (onTextMessageReceived == null)
			{
				throw new InvalidOperationException("Unable to find OnTextMessageReceived event");
			}
			var handler = new Action<IBilibiliService, BilibiliMessage>(counter.OnMessage);
			onTextMessageReceived.AddEventHandler(service, handler);

			var onMessageDeleted = bilibiliServiceType.GetEvent("OnMessageDeleted", BindingFlags.Instance | BindingFlags.Public);
			if (onMessageDeleted == null)
			{
				throw new InvalidOperationException("Unable to find OnMessageDeleted event");
			}
			var deleteHandler = new Action<IBilibiliService, BilibiliChannel, string>(counter.OnDeleted);
			onMessageDeleted.AddEventHandler(service, deleteHandler);

			var method = bilibiliServiceType.GetMethod("WebSocketClientOnDataReceived", BindingFlags.Instance | BindingFlags.NonPublic);
			if (method == null)
			{
				throw new InvalidOperationException("Unable to find WebSocketClientOnDataReceived(byte[]) method");
			}
			var invoke = (Action<byte[]>)method.CreateDelegate(typeof(Action<byte[]>), service);
			return new Harness(invoke, counter);
		}

		private static void SetPrivateField(Type type, object instance, string fieldName, object? value)
		{
			var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
			if (field == null)
			{
				throw new MissingFieldException(type.FullName, fieldName);
			}
			field.SetValue(instance, value);
		}

		private static ILogger CreateSilentLogger()
		{
			// Avoid building message templates/strings during benchmarks.
			return new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
		}

		private sealed class MessageCounter
		{
			private int _textCount;
			private int _deletedCount;

			public int TextCount => _textCount;
			public int DeletedCount => _deletedCount;

			public void Reset()
			{
				_textCount = 0;
				_deletedCount = 0;
			}

			public void OnMessage(IBilibiliService service, BilibiliMessage message)
			{
				_textCount++;
			}

			public void OnDeleted(IBilibiliService service, BilibiliChannel channel, string messageId)
			{
				_deletedCount++;
			}
		}

		private readonly struct Harness
		{
			public Harness(Action<byte[]> invoke, MessageCounter counter)
			{
				Invoke = invoke;
				Counter = counter;
			}

			public Action<byte[]> Invoke { get; }
			public MessageCounter Counter { get; }

			public byte[] LoadFrame(string fileName)
			{
				var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
				return File.ReadAllBytes(path);
			}

			public byte[][] LoadFrames(string[] fileNames)
			{
				if (fileNames == null) throw new ArgumentNullException(nameof(fileNames));
				if (fileNames.Length == 0) throw new ArgumentException("At least one frame file is required", nameof(fileNames));

				var frames = new byte[fileNames.Length][];
				for (var i = 0; i < fileNames.Length; i++)
				{
					frames[i] = LoadFrame(fileNames[i]);
				}
				return frames;
			}
		}
	}
}
