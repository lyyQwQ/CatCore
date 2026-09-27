using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CatCore.Models.Config;
using CatCore.Services;
using CatCore.Services.Overlay;
using FluentAssertions;
using Serilog;
using Xunit;

namespace CatCoreTests
{
	[Collection("OverlayWebSocketService")]
	public sealed class OverlayWebSocketServiceTests
	{
		[Fact]
		public async Task OverlayWebSocketService_SubUnsubPingDisconnect_FollowsProtocolAsync()
		{
			using var service = StartOverlayService(out var webSocketUri);
			using var client = await ConnectClientWithRetryAsync(webSocketUri, TimeSpan.FromSeconds(5));

			await SendJsonAsync(client, new { cmd = "sub", channels = new[] { "bilibili", "bilibili_raw", "unknown" } });
			var subAck = await ReceiveRequiredJsonAsync(client, TimeSpan.FromSeconds(3));
			subAck.GetProperty("cmd").GetString().Should().Be("sub");
			ReadChannels(subAck).Should().BeEquivalentTo(new[] { "bilibili", "bilibili_raw" });

			await SendJsonAsync(client, new { cmd = "unsub", channels = new[] { "bilibili_raw", "unknown" } });
			var unsubAck = await ReceiveRequiredJsonAsync(client, TimeSpan.FromSeconds(3));
			unsubAck.GetProperty("cmd").GetString().Should().Be("unsub");
			ReadChannels(unsubAck).Should().BeEquivalentTo(new[] { "bilibili" });

			await SendJsonAsync(client, new { cmd = "ping" });
			var pong = await ReceiveRequiredJsonAsync(client, TimeSpan.FromSeconds(3));
			pong.GetProperty("cmd").GetString().Should().Be("pong");

			await SendJsonAsync(client, new { cmd = "disconnect" });
			var disconnected = await ReceiveRequiredJsonAsync(client, TimeSpan.FromSeconds(3));
			disconnected.GetProperty("cmd").GetString().Should().Be("disconnected");
		}

		[Fact]
		public async Task OverlayWebSocketService_BilibiliAndRawChannels_AreFilteredBySubscriptionAsync()
		{
			using var service = StartOverlayService(out var webSocketUri);
			using var bilibiliClient = await ConnectClientWithRetryAsync(webSocketUri, TimeSpan.FromSeconds(5));
			using var rawClient = await ConnectClientWithRetryAsync(webSocketUri, TimeSpan.FromSeconds(5));

			await SendJsonAsync(bilibiliClient, new { cmd = "sub", channels = new[] { "bilibili" } });
			await SendJsonAsync(rawClient, new { cmd = "sub", channels = new[] { "bilibili_raw" } });

			var bilibiliSubAck = await ReceiveRequiredJsonAsync(bilibiliClient, TimeSpan.FromSeconds(3));
			bilibiliSubAck.GetProperty("cmd").GetString().Should().Be("sub");
			ReadChannels(bilibiliSubAck).Should().BeEquivalentTo(new[] { "bilibili" });

			var rawSubAck = await ReceiveRequiredJsonAsync(rawClient, TimeSpan.FromSeconds(3));
			rawSubAck.GetProperty("cmd").GetString().Should().Be("sub");
			ReadChannels(rawSubAck).Should().BeEquivalentTo(new[] { "bilibili_raw" });

			const string bilibiliPayload = "{\"Message\":\"hello\"}";
			service.BroadcastData("bilibili", bilibiliPayload);

			var bilibiliData = await ReceiveRequiredJsonAsync(bilibiliClient, TimeSpan.FromSeconds(3));
			bilibiliData.GetProperty("cmd").GetString().Should().Be("data");
			bilibiliData.GetProperty("channel").GetString().Should().Be("bilibili");
			bilibiliData.GetProperty("data").GetString().Should().Be(bilibiliPayload);

			const string rawPayload = "{\"cmd\":\"DANMU_MSG\"}";
			service.BroadcastData("bilibili_raw", rawPayload);

			var rawData = await ReceiveRequiredJsonAsync(rawClient, TimeSpan.FromSeconds(3));
			rawData.GetProperty("cmd").GetString().Should().Be("data");
			rawData.GetProperty("channel").GetString().Should().Be("bilibili_raw");
			rawData.GetProperty("data").GetString().Should().Be(rawPayload);

			var bilibiliUnexpected = await ReceiveOptionalTextAsync(bilibiliClient, TimeSpan.FromMilliseconds(500));
			bilibiliUnexpected.Should().BeNull("bilibili client only subscribes bilibili");
		}

		[Fact]
		public void BilibiliOverlayConfigData_ContainsExpectedOverlayFields()
		{
			var config = new BilibiliConfig
			{
				OverlayTtsEnable = true,
				OverlayTtsVoicePackage = "voice.pkg",
				OverlayTtsVoiceSpeed = 13,
				OverlayTtsVoicePitch = 7,
				OverlayShowInitWelcome = false,
				OverlayShowUsername = true,
				OverlayShowGiftInSc = true,
				OverlayShowGuardInSc = false
			};

			var method = typeof(KittenApiService).GetMethod("BuildOverlayConfigData", BindingFlags.NonPublic | BindingFlags.Static);
			method.Should().NotBeNull();

			var result = method!.Invoke(null, new object[] { config });
			result.Should().BeOfType<Dictionary<string, object>>();

			var data = (Dictionary<string, object>)result!;
			data["overlay_tts_enable"].Should().Be(config.OverlayTtsEnable);
			data["overlay_tts_voice_package"].Should().Be(config.OverlayTtsVoicePackage);
			data["overlay_tts_voice_speed"].Should().Be(config.OverlayTtsVoiceSpeed);
			data["overlay_tts_voice_pitch"].Should().Be(config.OverlayTtsVoicePitch);
			data["overlay_show_init_welcome"].Should().Be(config.OverlayShowInitWelcome);
			data["overlay_show_username"].Should().Be(config.OverlayShowUsername);
			data["overlay_show_gift_in_sc"].Should().Be(config.OverlayShowGiftInSc);
			data["overlay_show_guard_in_sc"].Should().Be(config.OverlayShowGuardInSc);
		}

		[Fact]
		public void KittenApiService_GetResourceNameFromPath_MapsStaticsPathToEmbeddedResourceName()
		{
			const string staticPath = "/Statics/Js/overlay.js";
			var resourceName = InvokeKittenApiStaticMethod<string>("GetResourceNameFromPath", staticPath);

			resourceName.Should().Be("CatCore.Resources.Statics.Js.overlay.js");
		}

		[Fact]
		public void KittenApiService_GetContentType_ReturnsKnownAndFallbackMimeType()
		{
			InvokeKittenApiStaticMethod<string>("GetContentType", "/Statics/Css/overlay.css").Should().Be("text/css");
			InvokeKittenApiStaticMethod<string>("GetContentType", "/Statics/Images/avatar.JPEG").Should().Be("image/jpeg");
			InvokeKittenApiStaticMethod<string>("GetContentType", "/Statics/Unknown/custom.bin").Should().Be("application/octet-stream");
			InvokeKittenApiStaticMethod<string>("GetContentType", "/Statics/Unknown/no_extension").Should().Be("application/octet-stream");
		}

		[Fact]
		public void StaticsRouteContract_KnownJsResourceCanBeResolvedAndHasExpectedContentType()
		{
			const string staticPath = "/Statics/Js/overlay.js";
			var resourceName = InvokeKittenApiStaticMethod<string>("GetResourceNameFromPath", staticPath);
			var contentType = InvokeKittenApiStaticMethod<string>("GetContentType", staticPath);

			var catCoreAssembly = typeof(KittenApiService).Assembly;
			using var resourceStream = catCoreAssembly.GetManifestResourceStream(resourceName);

			resourceStream.Should().NotBeNull();
			resourceStream!.Length.Should().BeGreaterThan(0);
			contentType.Should().Be("application/javascript");
		}

		[Fact]
		public void OverlayRouteContract_OverlayTemplateInjectsSerializedConfigData()
		{
			var config = new BilibiliConfig
			{
				OverlayTtsEnable = true,
				OverlayTtsVoicePackage = "voice.pkg",
				OverlayTtsVoiceSpeed = 8,
				OverlayTtsVoicePitch = 4,
				OverlayShowInitWelcome = true,
				OverlayShowUsername = false,
				OverlayShowGiftInSc = true,
				OverlayShowGuardInSc = false
			};

			var overlayConfigData = InvokeKittenApiStaticMethod<Dictionary<string, object>>("BuildOverlayConfigData", config);
			var overlayConfigJson = JsonSerializer.Serialize(overlayConfigData);

			var catCoreAssembly = typeof(KittenApiService).Assembly;
			using var overlayResourceStream = catCoreAssembly.GetManifestResourceStream("CatCore.Resources.overlay.html");
			overlayResourceStream.Should().NotBeNull();

			using var reader = new StreamReader(overlayResourceStream!, Encoding.UTF8, true, 1024, leaveOpen: false);
			var overlayTemplate = reader.ReadToEnd();
			overlayTemplate.Should().Contain("var config_data = {};");

			var renderedOverlay = overlayTemplate.Replace("var config_data = {};", $"var config_data = {overlayConfigJson};");

			renderedOverlay.Should().Contain($"var config_data = {overlayConfigJson};");
			renderedOverlay.Should().NotContain("var config_data = {};");
			renderedOverlay.Should().Contain("\"overlay_tts_enable\":true");
			renderedOverlay.Should().Contain("\"overlay_show_username\":false");
		}

		private static T InvokeKittenApiStaticMethod<T>(string methodName, params object[] args)
		{
			var method = typeof(KittenApiService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
			method.Should().NotBeNull($"KittenApiService should contain private static method '{methodName}'");

			var result = method!.Invoke(null, args);
			result.Should().BeOfType<T>();
			return (T)result!;
		}

		private static OverlayWebSocketService StartOverlayService(out Uri webSocketUri)
		{
			for (var attempt = 0; attempt < 10; attempt++)
			{
				var webApiPort = FindAvailablePort();
				var websocketPort = webApiPort - 1;
				webSocketUri = new Uri($"ws://127.0.0.1:{websocketPort}/");

				var service = new OverlayWebSocketService(CreateSilentLogger());
				try
				{
					service.Start(new Uri($"http://127.0.0.1:{webApiPort}/"));
					return service;
				}
				catch (HttpListenerException) when (attempt < 9)
				{
					service.Dispose();
				}
			}

			throw new InvalidOperationException("Unable to start overlay test service on an available port pair.");
		}

		private static ILogger CreateSilentLogger()
		{
			return new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
		}

		private static int FindAvailablePort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			try
			{
				listener.Start();
				var port = ((IPEndPoint)listener.LocalEndpoint).Port;
				if (port <= 1)
				{
					throw new InvalidOperationException("No available TCP port was found for websocket listener.");
				}

				return port;
			}
			finally
			{
				listener.Stop();
			}
		}

		private static async Task<ClientWebSocket> ConnectClientWithRetryAsync(Uri webSocketUri, TimeSpan timeout)
		{
			Exception? lastException = null;
			for (var attempt = 0; attempt < 10; attempt++)
			{
				var client = new ClientWebSocket();
				using var cts = new CancellationTokenSource(timeout);
				try
				{
					await client.ConnectAsync(webSocketUri, cts.Token);
					return client;
				}
				catch (Exception e)
				{
					lastException = e;
					client.Dispose();
				}

				await Task.Delay(100);
			}

			throw new InvalidOperationException($"Unable to connect to overlay websocket endpoint: {webSocketUri}", lastException);
		}

		private static async Task SendJsonAsync(ClientWebSocket client, object payload)
		{
			var json = JsonSerializer.Serialize(payload);
			var bytes = Encoding.UTF8.GetBytes(json);
			await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
		}

		private static async Task<JsonElement> ReceiveRequiredJsonAsync(ClientWebSocket client, TimeSpan timeout)
		{
			var text = await ReceiveOptionalTextAsync(client, timeout);
			text.Should().NotBeNullOrWhiteSpace($"expected websocket message within {timeout.TotalMilliseconds}ms");

			using var document = JsonDocument.Parse(text!);
			return document.RootElement.Clone();
		}

		private static async Task<string?> ReceiveOptionalTextAsync(ClientWebSocket client, TimeSpan timeout)
		{
			var buffer = new byte[1024];
			using var stream = new MemoryStream();
			using var cts = new CancellationTokenSource(timeout);
			while (true)
			{
				WebSocketReceiveResult result;
				try
				{
					result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
				}
				catch (OperationCanceledException)
				{
					return null;
				}
				catch (WebSocketException)
				{
					return null;
				}

				if (result.MessageType == WebSocketMessageType.Close)
				{
					return null;
				}

				if (result.MessageType != WebSocketMessageType.Text)
				{
					continue;
				}

				if (result.Count > 0)
				{
					stream.Write(buffer, 0, result.Count);
				}

				if (!result.EndOfMessage)
				{
					continue;
				}

				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		private static string[] ReadChannels(JsonElement payload)
		{
			if (!payload.TryGetProperty("channels", out var channelsNode) || channelsNode.ValueKind != JsonValueKind.Array)
			{
				return Array.Empty<string>();
			}

			return channelsNode
				.EnumerateArray()
				.Where(item => item.ValueKind == JsonValueKind.String)
				.Select(item => item.GetString())
				.Where(channel => !string.IsNullOrWhiteSpace(channel))
				.Select(channel => channel!)
				.ToArray();
		}
	}

	[CollectionDefinition("OverlayWebSocketService", DisableParallelization = true)]
	public sealed class OverlayWebSocketServiceCollection
	{
	}
}
