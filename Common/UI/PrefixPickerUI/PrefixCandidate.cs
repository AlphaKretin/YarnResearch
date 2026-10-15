using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace YarnResearch.Common.UI.PrefixPickerUI
{
	public readonly struct PrefixCandidate
	{
		// Vanilla's own "no prefix" id - Item.Prefix(0) is documented to do nothing.
		public const int NoPrefixId = 0;

		private static readonly LocalizedText NoPrefixName =
			ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(PrefixCandidate)}.NoPrefix");

		public readonly int PrefixId;
		public readonly string DisplayName;
		public readonly float ValueMultiplier;

		private PrefixCandidate(int prefixId, string displayName, float valueMultiplier)
		{
			PrefixId = prefixId;
			DisplayName = displayName;
			ValueMultiplier = valueMultiplier;
		}

		public static List<PrefixCandidate> GetCandidates(Item item)
		{
			var results = new List<PrefixCandidate>();
			var seen = new HashSet<int>();

			// Every category the item matches contributes its prefixes to one flat list. Which category a
			// given prefix came from doesn't matter to the caller - a default is keyed on the item's whole
			// PrefixGroup, not on any single category (see PrefixGroup).
			foreach (PrefixCategory category in item.GetPrefixCategories())
			{
				foreach (int prefixId in Item.GetVanillaPrefixes(category))
					TryAdd(item, prefixId, results, seen);

				foreach (ModPrefix modPrefix in PrefixLoader.GetPrefixesInCategory(category))
					TryAdd(item, modPrefix.Type, results, seen);
			}

			if (results.Count == 0)
				return results;

			results.Sort((a, b) => b.ValueMultiplier.CompareTo(a.ValueMultiplier));

			// Pinned to the top rather than sorted in by value: it's the "clear what's set here" row, not a
			// modifier competing with the others on strength.
			results.Insert(0, new PrefixCandidate(NoPrefixId, NoPrefixName.Value, 0f));

			return results;
		}

		private static void TryAdd(Item item, int prefixId, List<PrefixCandidate> results, HashSet<int> seen)
		{
			if (prefixId <= 0 || !seen.Add(prefixId) || !item.CanApplyPrefix(prefixId))
				return;

			results.Add(new PrefixCandidate(prefixId, Lang.prefix[prefixId].Value, GetValueMultiplier(item, prefixId)));
		}

		// Item.Prefix applies a modded prefix's ModifyValue on top of the stat-derived multiplier, then
		// multiplies the item's value by the square of the result - squaring keeps the order, so this sorts
		// the same as the real sell value would.
		private static float GetValueMultiplier(Item item, int prefixId)
		{
			item.TryGetPrefixStatMultipliersForItem(prefixId, out _, out _, out _, out _, out _, out _, out _, out _, out _, out float valueMult);

			if (prefixId >= PrefixID.Count)
				PrefixLoader.GetPrefix(prefixId)?.ModifyValue(ref valueMult);

			return valueMult;
		}
	}
}
