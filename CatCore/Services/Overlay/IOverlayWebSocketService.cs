using System;

namespace CatCore.Services.Overlay
{
	internal interface IOverlayWebSocketService : IDisposable
	{
		void Start(Uri webApiUri);
		void Stop();
		void BroadcastData(string channel, string data);
	}
}
