using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace YarnResearch.Common.Systems
{
	// The set of items one chosen default prefix applies to. Two items share a group only if their whole
	// PrefixCategory lists match: vanilla gives most weapons several categories at once (a broadsword is
	// [Melee, AnyWeapon], a vanilla spear only [AnyWeapon]), so keying a default on any single category
	// leaks it between item kinds that overlap only partly.
	//
	// Tools are one more entry in the same key, on top of vanilla's categories. A pickaxe and a broadsword
	// have identical category lists but want different prefixes, which is a distinction vanilla doesn't
	// draw anywhere.
	//
	// The key is the sorted category names joined into a string rather than a bitmask of their values: a
	// copy is required either way, since GetPrefixCategories hands back the live cached list out of
	// PrefixLoader rather than one of its own, and names survive PrefixCategory being renumbered or having
	// a value inserted, which saved ordinals would not. It also reads plainly in the save file.
	public readonly struct PrefixGroup : IEquatable<PrefixGroup>
	{
		// This mod's own marker, not a PrefixCategory name.
		private const string ToolMarker = "Tool";

		private readonly string _key;

		private PrefixGroup(string key)
		{
			_key = key;
		}

		public static PrefixGroup Of(Item item)
		{
			var names = new List<string>();

			foreach (PrefixCategory category in item.GetPrefixCategories())
				names.Add(category.ToString());

			if (IsTool(item))
				names.Add(ToolMarker);

			// Sorted so the key can't depend on the order vanilla happens to add categories in.
			names.Sort(StringComparer.Ordinal);

			return new PrefixGroup(string.Join(',', names));
		}

		public static PrefixGroup OfType(int itemType) => Of(ContentSamples.ItemsByType[itemType]);

		// The same test vanilla uses in Item.RestoreMeleeSpeedBehaviorOnVanillaItems, which holds it in a
		// local rather than exposing it, so there is nothing to call. It reads the public pick/axe/hammer
		// power fields, so a modded tool satisfies it on its own terms; only the Gravedigger's Shovel is
		// named outright, because it digs without carrying any of those three.
		private static bool IsTool(Item item) =>
			item.pick > 0 || item.axe > 0 || item.hammer > 0 || item.type == ItemID.GravediggerShovel;

		public bool Equals(PrefixGroup other) => _key == other._key;

		public override bool Equals(object obj) => obj is PrefixGroup other && Equals(other);

		public override int GetHashCode() => _key?.GetHashCode() ?? 0;

		public void Save(TagCompound tag) => tag["Group"] = _key;

		public static PrefixGroup Load(TagCompound tag) => new(tag.GetString("Group"));
	}
}
