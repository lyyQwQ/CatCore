namespace CatCore.Models.Config
{
	internal sealed class BilibiliConfig
	{
		public bool Enabled { get; set; }

		public bool ShowBadge { get; set; } = true;

		public long RoomId { get; set; }

		public string Cookies { get; set; } = string.Empty;

		public bool OverlayTtsEnable { get; set; }

		public string OverlayTtsVoicePackage { get; set; } = string.Empty;

		public int OverlayTtsVoiceSpeed { get; set; } = 10;

		public int OverlayTtsVoicePitch { get; set; } = 10;

		public bool OverlayShowInitWelcome { get; set; } = true;

		public bool OverlayShowUsername { get; set; } = true;

		public bool OverlayShowGiftInSc { get; set; }

		public bool OverlayShowGuardInSc { get; set; }
	}
}
