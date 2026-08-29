using System.Collections.Generic;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.UI.PrefixPickerUI
{
	public readonly struct PrefixCandidate
	{
		private static readonly LocalizedText NoPrefixName =
			ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(PrefixCandidate)}.NoPrefix");

		public readonly int PrefixId;
		public readonly string DisplayName;
		public readonly int Value;

		private PrefixCandidate(int prefixId, string displayName, int value)
		{
			PrefixId = prefixId;
			DisplayName = displayName;
			Value = value;
		}

		public static List<PrefixCandidate> GetCandidates(Item item)
		{
			var results = new List<PrefixCandidate>();
			var seen = new HashSet<int>();

			// Every category the item matches contributes its prefixes to one flat list. Which category a
			// given prefix came from doesn't matter to the caller - a default is keyed on the item's whole
			// PrefixGroup, not on any single category (see PrefixGroup).
			foreach (PrefixCategory category in item.GetPrefixCategories()) {
				foreach (int prefixId in Item.GetVanillaPrefixes(category))
					TryAdd(item, prefixId, results, seen);

				foreach (ModPrefix modPrefix in PrefixLoader.GetPrefixesInCategory(category))
					TryAdd(item, modPrefix.Type, results, seen);
			}

			// No public API exposes a prefix's value multiplier directly (ModPrefix.ModifyValue only runs as
			// a side effect of actually applying the prefix), so GetPrefixValue rolls it onto a scratch item
			// of the same type and reads back the resulting Item.value.
			results.Sort((a, b) => b.Value.CompareTo(a.Value));

			// Pinned to the top rather than sorted in by value: it's the "clear what's set here" row, not a
			// modifier competing with the others on strength.
			results.Insert(0, new PrefixCandidate(PrefixTrial.NoPrefixId, NoPrefixName.Value, 0));

			return results;
		}

		private static void TryAdd(Item item, int prefixId, List<PrefixCandidate> results, HashSet<int> seen)
		{
			if (prefixId <= 0 || !seen.Add(prefixId) || !item.CanRollPrefix(prefixId))
				return;

			// A prefix the item can roll but that doesn't survive being applied is left out of the list
			// entirely - offering it would mean the player picks one prefix and gets a random other one.
			PrefixTrial.Result trial = PrefixTrial.Of(item.type, prefixId);
			if (!trial.Applied)
				return;

			results.Add(new PrefixCandidate(prefixId, Lang.prefix[prefixId].Value, trial.Value));
		}
	}
}
