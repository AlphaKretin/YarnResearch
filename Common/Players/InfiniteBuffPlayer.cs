using System.Collections.Generic;
using System.Linq;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace YarnResearch.Common.Players
{
	// Infinite-buff toggle state (see InfiniteBuffSystem). Saved with the character rather than the world:
	// in multiplayer the world file is written by the server process, which never sees a client's toggles.
	public class InfiniteBuffPlayer : ModPlayer
	{
		// buffType -> explicit toggle state. Absence means "never decided", not "off".
		public Dictionary<int, bool> BuffStates { get; } = [];

		// Banner item type -> explicit toggle state, same absence rule.
		public Dictionary<int, bool> BannerStates { get; } = [];

		// Null means never decided.
		public bool? GardenGnome { get; set; }

		public override void SaveData(TagCompound tag)
		{
			SaveStates(tag, BuffStates, "InfiniteBuffsOn", "InfiniteBuffsOff");
			SaveStates(tag, BannerStates, "InfiniteBannersOn", "InfiniteBannersOff");

			if (GardenGnome is bool gnome)
				tag["InfiniteGardenGnome"] = gnome;
		}

		public override void LoadData(TagCompound tag)
		{
			LoadStates(tag, BuffStates, "InfiniteBuffsOn", "InfiniteBuffsOff");
			LoadStates(tag, BannerStates, "InfiniteBannersOn", "InfiniteBannersOff");
			GardenGnome = tag.TryGet("InfiniteGardenGnome", out bool gnome) ? gnome : null;
		}

		private static void SaveStates(TagCompound tag, Dictionary<int, bool> states, string onKey, string offKey)
		{
			int[] on = [.. states.Where(p => p.Value).Select(p => p.Key)];
			int[] off = [.. states.Where(p => !p.Value).Select(p => p.Key)];

			if (on.Length > 0)
				tag[onKey] = on;

			if (off.Length > 0)
				tag[offKey] = off;
		}

		private static void LoadStates(TagCompound tag, Dictionary<int, bool> states, string onKey, string offKey)
		{
			states.Clear();

			if (tag.TryGet(onKey, out int[] on))
			{
				foreach (int type in on)
					states[type] = true;
			}

			if (tag.TryGet(offKey, out int[] off))
			{
				foreach (int type in off)
					states[type] = false;
			}
		}
	}
}
