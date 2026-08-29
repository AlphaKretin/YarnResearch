using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Terraria.Utilities;

namespace YarnResearch.Common.Systems
{
	// What actually happens when one prefix is rolled onto one item type: whether that exact prefix lands,
	// and what it does to the item's value.
	//
	// Item.CanRollPrefix is not enough on its own. Item.Prefix re-rolls a random prefix when the one asked
	// for changes no stat on that particular item (ModPrefix.AllStatChangesHaveEffectOn, and vanilla's own
	// equivalent for its own prefixes), so a prefix that passes CanRollPrefix can still come back as
	// something else entirely - and a caller that re-applies it every frame, as the tooltip preview does,
	// gets a different prefix every frame. Item.Prefix's own documentation points at CanApplyPrefix to test
	// for this, but that method was removed from the game, so the only way to know is to roll the prefix on
	// a scratch item and read back what stuck.
	public class PrefixTrial : ModSystem
	{
		// Vanilla's own "no prefix" id - Item.Prefix(0) is documented to do nothing.
		public const int NoPrefixId = 0;

		public readonly struct Result
		{
			// False when Item.Prefix rolled something other than what it was asked for.
			public readonly bool Applied;
			public readonly int Value;

			public Result(bool applied, int value)
			{
				Applied = applied;
				Value = value;
			}
		}

		// A roll costs a SetDefaults plus the roll itself, far too much to repeat for every duplication slot
		// on every frame. The outcome depends only on the item type and the prefix, so it is worked out once.
		private static readonly Dictionary<(int ItemType, int PrefixId), Result> Results = new();

		// Item.value is an int, and a prefix's multiplier is applied by multiplying-and-rounding whatever
		// value the item already has - a low base value (a cheap Pickaxe) makes many distinct multipliers
		// round to the same integer, and List.Sort isn't stable, so those ties land in arbitrary order.
		// Forcing a large fixed baseline before rolling makes the rounding error negligible and keeps the
		// picker's sort order consistent across every item type.
		private const int SortValueBaseline = 1_000_000;

		private const int RollSeed = 0;

		public override void Unload() => Results.Clear();

		public static Result Of(int itemType, int prefixId)
		{
			if (Results.TryGetValue((itemType, prefixId), out Result cached))
				return cached;

			Result result = Roll(itemType, prefixId);
			Results[(itemType, prefixId)] = result;
			return result;
		}

		private static Result Roll(int itemType, int prefixId)
		{
			// A rejected prefix is re-rolled from Main.rand, so roll against a scratch generator rather than
			// perturbing the world's own - same push-fake-state/restore shape as GetExtractinatorOutputs.
			UnifiedRandom realRand = Main.rand;

			try
			{
				Main.rand = new UnifiedRandom(RollSeed);

				var scratch = new Item();
				scratch.SetDefaults(itemType);
				scratch.value = SortValueBaseline;
				scratch.Prefix(prefixId);

				return new Result(scratch.prefix == prefixId, scratch.value);
			}
			finally
			{
				Main.rand = realRand;
			}
		}
	}
}
