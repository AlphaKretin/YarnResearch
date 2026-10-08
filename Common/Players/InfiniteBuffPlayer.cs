using System;
using System.Collections.Generic;
using System.Linq;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
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

		// Saved states for content whose mod isn't currently loaded, held so they are written back unchanged.
		private readonly Dictionary<BuffDefinition, bool> _unloadedBuffs = [];
		private readonly Dictionary<ItemDefinition, bool> _unloadedBanners = [];

		public override void SaveData(TagCompound tag)
		{
			SaveStates(tag, BuffStates, _unloadedBuffs, type => new BuffDefinition(type), "BuffsOn", "BuffsOff");
			SaveStates(tag, BannerStates, _unloadedBanners, type => new ItemDefinition(type), "BannersOn", "BannersOff");

			if (GardenGnome is bool gnome)
				tag["InfiniteGardenGnome"] = gnome;
		}

		public override void LoadData(TagCompound tag)
		{
			BuffStates.Clear();
			BannerStates.Clear();
			_unloadedBuffs.Clear();
			_unloadedBanners.Clear();

			LoadRawTypeStates(tag, BuffStates, BuffID.Count, "InfiniteBuffsOn", on: true);
			LoadRawTypeStates(tag, BuffStates, BuffID.Count, "InfiniteBuffsOff", on: false);
			LoadRawTypeStates(tag, BannerStates, ItemID.Count, "InfiniteBannersOn", on: true);
			LoadRawTypeStates(tag, BannerStates, ItemID.Count, "InfiniteBannersOff", on: false);

			LoadStates(tag, BuffStates, _unloadedBuffs, "BuffsOn", on: true);
			LoadStates(tag, BuffStates, _unloadedBuffs, "BuffsOff", on: false);
			LoadStates(tag, BannerStates, _unloadedBanners, "BannersOn", on: true);
			LoadStates(tag, BannerStates, _unloadedBanners, "BannersOff", on: false);

			GardenGnome = tag.TryGet("InfiniteGardenGnome", out bool gnome) ? gnome : null;
		}

		private static void SaveStates<T>(TagCompound tag, Dictionary<int, bool> states, Dictionary<T, bool> unloaded,
			Func<int, T> define, string onKey, string offKey) where T : EntityDefinition
		{
			var all = states.Select(p => (Definition: define(p.Key), On: p.Value))
				.Concat(unloaded.Select(p => (Definition: p.Key, On: p.Value)))
				.ToList();

			List<T> on = [.. all.Where(p => p.On).Select(p => p.Definition)];
			List<T> off = [.. all.Where(p => !p.On).Select(p => p.Definition)];

			if (on.Count > 0)
				tag[onKey] = on;

			if (off.Count > 0)
				tag[offKey] = off;
		}

		private static void LoadStates<T>(TagCompound tag, Dictionary<int, bool> states, Dictionary<T, bool> unloaded,
			string key, bool on) where T : EntityDefinition
		{
			foreach (T definition in tag.GetList<T>(key))
			{
				if (definition.IsUnloaded)
					unloaded[definition] = on;
				else
					states[definition.Type] = on;
			}
		}

		// Characters saved before states were stored by identity hold raw type IDs instead. Only vanilla IDs
		// are stable between sessions, so modded ones are dropped and fall back to "never decided".
		private static void LoadRawTypeStates(TagCompound tag, Dictionary<int, bool> states, int vanillaCount,
			string key, bool on)
		{
			if (!tag.TryGet(key, out int[] types))
				return;

			foreach (int type in types)
			{
				if (type > 0 && type < vanillaCount)
					states[type] = on;
			}
		}
	}
}
