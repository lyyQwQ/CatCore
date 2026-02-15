using CatCore.Models.Shared;

namespace CatCore.Models.Bilibili
{
	public sealed class BilibiliEmote : IChatEmote
	{
		public string Id { get; }
		public string Name { get; }
		public int StartIndex { get; }
		public int EndIndex { get; }
		public string Url { get; }
		public bool Animated { get; }

		public BilibiliEmote(string id, string name, int startIndex, string url, bool animated)
		{
			Id = id;
			Name = name;
			StartIndex = startIndex;
			EndIndex = startIndex + (name?.Length ?? 0);
			Url = url;
			Animated = animated;
		}
	}
}
