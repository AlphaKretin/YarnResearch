using System.Collections.Generic;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace YarnResearch.Common.UI.PrefixPickerUI
{
	public readonly struct PrefixCandidate
	{
		public readonly int PrefixId;
		public readonly PrefixCategory Category;
		public readonly string DisplayName;
		public readonly int Value;

		private PrefixCandidate(int prefixId, PrefixCategory category, string displayName, int value)
		{
			PrefixId = prefixId;
			Category = category;
			DisplayName = displayName;
			Value = value;
		}

		public static List<PrefixCandidate> GetCandidates(Item item)
		{
			var results = new List<PrefixCandidate>();
			var seen = new HashSet<int>();

			foreach (PrefixCategory category in item.GetPrefixCategories()) {
				foreach (int prefixId in Item.GetVanillaPrefixes(category))
					TryAdd(item, category, prefixId, results, seen);

				foreach (ModPrefix modPrefix in PrefixLoader.GetPrefixesInCategory(category))
					TryAdd(item, category, modPrefix.Type, results, seen);
			}

			// No public API exposes a prefix's value multiplier directly (ModPrefix.ModifyValue only runs as
			// a side effect of actually applying the prefix), so GetPrefixValue rolls it onto a scratch item
			// of the same type and reads back the resulting Item.value.
			results.Sort((a, b) => b.Value.CompareTo(a.Value));
			return results;
		}

		private static void TryAdd(Item item, PrefixCategory category, int prefixId, List<PrefixCandidate> results, HashSet<int> seen)
		{
			if (prefixId <= 0 || !seen.Add(prefixId) || !item.CanRollPrefix(prefixId))
				return;

			results.Add(new PrefixCandidate(prefixId, category, Lang.prefix[prefixId].Value, GetPrefixValue(item.type, prefixId)));
		}

		// Item.value is an int, and the prefix value multiplier gets applied by multiplying-and-rounding
		// whatever the item's current value already is - a low base value (e.g. a cheap Pickaxe) makes many
		// distinct multipliers round to the same integer, and List.Sort isn't stable, so those ties land in
		// arbitrary order. Forcing a large, fixed baseline before rolling the prefix makes rounding error
		// negligible and keeps the sort order consistent across every item type.
		private const int SortValueBaseline = 1_000_000;

		private static int GetPrefixValue(int itemType, int prefixId)
		{
			var scratch = new Item();
			scratch.SetDefaults(itemType);
			scratch.value = SortValueBaseline;
			scratch.Prefix(prefixId);
			return scratch.value;
		}
	}
}
