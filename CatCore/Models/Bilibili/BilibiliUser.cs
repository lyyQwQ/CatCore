using System;
using System.Collections.ObjectModel;
using CatCore.Models.Shared;

namespace CatCore.Models.Bilibili
{
	public sealed class BilibiliUser : IChatUserWithBadges
	{
		public BilibiliUser(string id, string userName, string displayName, string color, bool isBroadcaster, bool isModerator,
			ReadOnlyCollection<IChatBadge>? badges = null)
		{
			Id = id;
			UserName = userName;
			DisplayName = displayName;
			Color = color;
			IsBroadcaster = isBroadcaster;
			IsModerator = isModerator;
			Badges = badges ?? new ReadOnlyCollection<IChatBadge>(Array.Empty<IChatBadge>());
		}

		public string Id { get; }
		public string UserName { get; }
		public string DisplayName { get; }
		public string Color { get; }
		public bool IsBroadcaster { get; }
		public bool IsModerator { get; }
		public ReadOnlyCollection<IChatBadge> Badges { get; }
	}
}
