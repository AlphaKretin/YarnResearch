using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using YarnResearch.Common.Configs;
using YarnResearch.Common.UI;

namespace YarnResearch.Common.Systems
{
	public class InfiniteBuffSystem : ModSystem
	{
		// What this system does with a given item, and which buffType that drives.
		private enum BuffItemKind
		{
			None,
			GardenGnome,
			Banner,
			Placed,
			Consumable,
		}

		// buffType -> explicit toggle state. Only buffTypes actually touched (player toggle, auto-default,
		// tiering) get an entry - absence means "never decided", not "off". Covers potions/food and every
		// whitelisted placed buff item - NOT banners or Garden Gnome, which have no buffType of their own
		// and use bespoke per-tick proximity forcing instead (see ToggledBanners/_gardenGnomeInfinite).
		private static readonly Dictionary<int, bool> InfiniteBuffTypes = new();

		// Curated whitelist of placed buff items that grant a normal Player.buffType buff - itemType ->
		// buffType.
		private static readonly Dictionary<int, int> PlacedBuffItems = new() {
			{ ItemID.AmmoBox, BuffID.AmmoBox },
			{ ItemID.BewitchingTable, BuffID.Bewitched },
			{ ItemID.CrystalBall, BuffID.Clairvoyance },
			{ ItemID.SliceOfCake, BuffID.SugarRush },
			{ ItemID.SharpeningStation, BuffID.Sharpened },
			{ ItemID.WarTable, BuffID.WarTable },
			{ ItemID.DeadCellsPotionStation, BuffID.DeadCellsPotionStation },
			{ ItemID.CatBast, BuffID.CatBast },
			{ ItemID.Campfire, BuffID.Campfire },
			{ ItemID.Fireplace, BuffID.Campfire },
			{ ItemID.HeartLantern, BuffID.HeartLamp },
			{ ItemID.StarinaBottle, BuffID.StarInBottle },
			{ ItemID.Sunflower, BuffID.Sunflower },
			{ ItemID.BottledHoney, BuffID.Honey },
			{ ItemID.HoneyBucket, BuffID.Honey },
			{ ItemID.BottomlessHoneyBucket, BuffID.Honey },
		};

		private static readonly int[] WellFedTierOrder = { BuffID.WellFed, BuffID.WellFed2, BuffID.WellFed3 };

		// Banners are a bespoke case: every banner shares one generic BuffID.MonsterBanner buff, while the
		// per-enemy damage bonus is a separate non-buff bool array (Main.SceneMetrics.NPCBannerBuff) that
		// vanilla recomputes from tile proximity every tick. This stores which individual banner *items* are
		// toggled on. The shared BuffID.MonsterBanner buff itself goes through the normal
		// SetInfinite/TimeLeftDoesNotDecrease path, granted once when the set goes from empty to non-empty;
		// letting vanilla re-grant it from the forced proximity flags instead made the buff flicker on and
		// off. ForceProximityFlags still forces the per-enemy damage-bonus flags every tick, which is read
		// live at hit time rather than in a specific tick phase.
		private static readonly HashSet<int> ToggledBanners = new();

		// Garden Gnome's luck bonus is not a real Player.buffType/BuffID at all - no buff icon, implemented
		// purely via the Player.HasGardenGnomeNearby proximity bool - so it gets the same per-tick forcing
		// as banners, just a single bool instead of a set.
		private static bool _gardenGnomeInfinite;

		private static readonly Color InfiniteHighlightColor = new(255, 140, 0);

		public static ModKeybind ToggleInfiniteBuffKeybind { get; private set; }

		private static LocalizedText _buffBarFullText;

		private static On_Player.hook_DelBuff _delBuffHook;
		private static On_ItemSlot.hook_DrawItemIcon _drawItemIconHook;

		public override void Load()
		{
			ToggleInfiniteBuffKeybind = KeybindLoader.RegisterKeybind(Mod, "ToggleInfiniteBuff", "Mouse3");
			_buffBarFullText = Mod.GetLocalization($"{nameof(InfiniteBuffSystem)}.BuffBarFull");

			_delBuffHook = (On_Player.orig_DelBuff orig, Player self, int b) => {
				if (self.whoAmI == Main.myPlayer)
					HandleDismiss(self.buffType[b]);

				orig(self, b);
			};
			On_Player.DelBuff += _delBuffHook;

			// Marks items shown as infinite-toggled in the Journey Mode Duplication panel. Scoped to that
			// one slot context deliberately, rather than GlobalItem.PreDrawInInventory everywhere, to avoid
			// a distracting highlight in the normal inventory/hotbar/chests. Hooks DrawItemIcon rather than
			// the outer Draw because it hands us the icon's exact center point and size limit directly.
			_drawItemIconHook = (On_ItemSlot.orig_DrawItemIcon orig, Item item, int context, SpriteBatch spriteBatch, Vector2 screenPositionForItemCenter, float scale, float sizeLimit, Color environmentColor, float itemFade, bool flip) => {
				if (context == ItemSlot.Context.CreativeInfinite && IsItemInfinite(item))
					SlotTint.Draw(spriteBatch, screenPositionForItemCenter, sizeLimit * scale, InfiniteHighlightColor);

				return orig(item, context, spriteBatch, screenPositionForItemCenter, scale, sizeLimit, environmentColor, itemFade, flip);
			};
			On_ItemSlot.DrawItemIcon += _drawItemIconHook;
		}

		public override void Unload()
		{
			ToggleInfiniteBuffKeybind = null;

			if (_delBuffHook != null) {
				On_Player.DelBuff -= _delBuffHook;
				_delBuffHook = null;
			}

			if (_drawItemIconHook != null) {
				On_ItemSlot.DrawItemIcon -= _drawItemIconHook;
				_drawItemIconHook = null;
			}
		}

		// Single source of truth for what an item counts as here, so the tooltip gate, the auto-default on
		// research, and the manual toggle can't disagree about a given item. BuffType is 0 for Garden Gnome,
		// which has no buff of its own.
		private static (BuffItemKind Kind, int BuffType) Classify(Item item)
		{
			if (item.type == ItemID.GardenGnome)
				return (BuffItemKind.GardenGnome, 0);

			if (IsBannerItem(item.type))
				return (BuffItemKind.Banner, BuffID.MonsterBanner);

			if (PlacedBuffItems.TryGetValue(item.type, out int placedBuffType))
				return (BuffItemKind.Placed, placedBuffType);

			// item.consumable, not item.potion: item.potion only marks items that inflict Potion Sickness,
			// which excludes non-sickness buff potions (Builder, Flipper, Torch God's Flavor, etc.) - those
			// still need to toggle like any other consumed buff item.
			if (item.consumable && item.buffType > 0)
				return (BuffItemKind.Consumable, item.buffType);

			return (BuffItemKind.None, 0);
		}

		// The gate every entry point shares: the feature is on, and the item is researched.
		private static bool BuffTogglesAllowed(Item item) =>
			ModContent.GetInstance<YarnResearchConfig>().InfiniteResearchedBuffs &&
			ResearchCascadeSystem.IsResearched(item.type);

		// Whether hoverItem is something TryToggle would act on - used to gate the tooltip hint,
		// independent of its current on/off state.
		public static bool IsToggleable(Item item) =>
			BuffTogglesAllowed(item) && Classify(item).Kind != BuffItemKind.None;

		public static bool IsItemInfinite(Item item)
		{
			if (item == null || item.IsAir)
				return false;

			(BuffItemKind kind, int buffType) = Classify(item);

			return kind switch {
				BuffItemKind.None => false,
				BuffItemKind.GardenGnome => _gardenGnomeInfinite,
				BuffItemKind.Banner => ToggledBanners.Contains(item.type),
				_ => IsInfinite(buffType),
			};
		}

		public override void UpdateUI(GameTime gameTime)
		{
			if (Main.gameMenu || !ToggleInfiniteBuffKeybind.JustPressed)
				return;

			if (!IsHoveringDuplicationMenu())
				return;

			Item hoverItem = Main.HoverItem;
			if (!hoverItem.IsAir)
				TryToggle(hoverItem);
		}

		// Main.CreativeMenu (type CreativeUI) backs both the Research/sacrifice and Duplication panels of
		// the Journey Mode power-icon menu as one instance - vanilla's own Main.cs gates input on exactly
		// this combination (Main.cs.patch: "bool flag9 = CreativeMenu.Enabled && !CreativeMenu.Blocked;").
		// There's no further public distinction between its Research/Duplication tabs, so this scopes the
		// hotkey to "the Journey Mode power menu is open" rather than the Duplication tab specifically.
		internal static bool IsHoveringDuplicationMenu()
		{
			return Main.CreativeMenu.Enabled && !Main.CreativeMenu.Blocked;
		}

		// ItemID.Sets.BannerStrength[type].Enabled is NOT an "is this item a banner" flag - it's true for
		// every item, gating the per-item damage-scaling curve override - so the actual banner check goes
		// through NPCLoader's own item<->banner mapping, the same one ModBannerTile.NearbyEffects uses.
		// Returns -1 for anything that isn't a banner item.
		private static bool IsBannerItem(int itemType) => NPCLoader.BannerItemToNPC(itemType) >= 0;

		// Called every tick from YarnResearchPlayer.PreModifyLuck, after vanilla's own tile-proximity scan
		// has run for this tick - re-forces the bespoke proximity flags for banners/Garden Gnome so
		// vanilla's own buff-granting and luck calculation (which read these later the same tick) see them
		// as active. Vanilla resets both to their real proximity values every tick, so this must run every
		// tick, not just once at toggle time.
		public static void ForceProximityFlags(Player player)
		{
			if (_gardenGnomeInfinite)
				player.HasGardenGnomeNearby = true;

			if (ToggledBanners.Count == 0)
				return;

			Main.SceneMetrics.hasBanner = true;
			foreach (int itemType in ToggledBanners) {
				int bannerId = NPCLoader.BannerItemToNPC(itemType);
				if (bannerId >= 0 && bannerId < Main.SceneMetrics.NPCBannerBuff.Length)
					Main.SceneMetrics.NPCBannerBuff[bannerId] = true;
			}
		}

		// Auto-default on research: a newly-researched buff item turns itself on, unless the player has
		// already made an explicit decision about that buffType.
		public static void HandleItemResearched(Item item)
		{
			if (!BuffTogglesAllowed(item))
				return;

			(BuffItemKind kind, int buffType) = Classify(item);

			switch (kind) {
				case BuffItemKind.GardenGnome:
					_gardenGnomeInfinite = true;
					break;

				case BuffItemKind.Banner:
					if (!ToggledBanners.Contains(item.type) &&
						(ToggledBanners.Count > 0 || SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true)))
						ToggledBanners.Add(item.type);
					break;

				case BuffItemKind.Placed:
					if (!InfiniteBuffTypes.ContainsKey(buffType))
						SetInfinite(Main.LocalPlayer, buffType, on: true);
					break;

				case BuffItemKind.Consumable:
					if (ItemID.Sets.IsFood[item.type])
						HandleFoodResearched(buffType);
					else if (!InfiniteBuffTypes.ContainsKey(buffType))
						// Tipsy is vanilla's one mixed-effect buff registered as a debuff
						// (Main.debuff[BuffID.Tipsy] = true), so right-click dismissal never works on it the
						// way it does for other potion buffs - defaults OFF instead of the usual default-ON
						// so it can't get stuck permanently active.
						SetInfinite(Main.LocalPlayer, buffType, on: buffType != BuffID.Tipsy);
					break;
			}
		}

		// Well Fed comes in three escalating tiers that share no buffType - only the best researched tier
		// should be held, so a better one turns the others off.
		private static void HandleFoodResearched(int buffType)
		{
			int tierRank = Array.IndexOf(WellFedTierOrder, buffType);
			if (tierRank < 0) {
				// Non-Well-Fed food: same default-ON rule as potions.
				if (!InfiniteBuffTypes.ContainsKey(buffType))
					SetInfinite(Main.LocalPlayer, buffType, on: true);
				return;
			}

			int currentBestRank = -1;
			for (int i = 0; i < WellFedTierOrder.Length; i++) {
				if (InfiniteBuffTypes.GetValueOrDefault(WellFedTierOrder[i]))
					currentBestRank = i;
			}

			if (tierRank <= currentBestRank)
				return;

			SetInfinite(Main.LocalPlayer, buffType, on: true);
			for (int i = 0; i < WellFedTierOrder.Length; i++) {
				if (i != tierRank && InfiniteBuffTypes.GetValueOrDefault(WellFedTierOrder[i]))
					SetInfinite(Main.LocalPlayer, WellFedTierOrder[i], on: false);
			}
		}

		// Manual hotkey entry point - unlike HandleItemResearched's auto-default, a refused toggle-ON here
		// gives chat feedback (see SetInfinite) since it's a direct player action.
		public static void TryToggle(Item hoverItem)
		{
			if (!BuffTogglesAllowed(hoverItem))
				return;

			(BuffItemKind kind, int buffType) = Classify(hoverItem);

			switch (kind) {
				case BuffItemKind.GardenGnome:
					_gardenGnomeInfinite = !_gardenGnomeInfinite;
					break;

				case BuffItemKind.Banner:
					// The shared buff is granted with the first toggled banner and dropped with the last.
					if (ToggledBanners.Remove(hoverItem.type)) {
						if (ToggledBanners.Count == 0)
							SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: false);
					}
					else if (ToggledBanners.Count > 0 ||
						SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true, isManualToggle: true)) {
						ToggledBanners.Add(hoverItem.type);
					}
					break;

				case BuffItemKind.Placed:
				case BuffItemKind.Consumable:
					SetInfinite(Main.LocalPlayer, buffType, on: !InfiniteBuffTypes.GetValueOrDefault(buffType),
						isManualToggle: true);
					break;
			}
		}

		// Returns whether the toggle was actually applied - false means a toggle-ON was refused by the
		// buff-cap safeguard, which banner toggling needs to know before adding to ToggledBanners.
		public static bool SetInfinite(Player player, int buffType, bool on, bool isManualToggle = false)
		{
			if (on && IsBuffBarFull(player) && player.FindBuffIndex(buffType) < 0) {
				if (isManualToggle)
					Main.NewText(_buffBarFullText.Value);
				else
					InfiniteBuffTypes.Remove(buffType);

				return false;
			}

			InfiniteBuffTypes[buffType] = on;
			BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = on;
			Main.buffNoTimeDisplay[buffType] = on;

			if (on) {
				if (player.FindBuffIndex(buffType) < 0)
					player.AddBuff(buffType, 60 * 60 * 60);
			}
			else {
				int index = player.FindBuffIndex(buffType);
				if (index >= 0)
					player.DelBuff(index);
			}

			return true;
		}

		private static bool IsBuffBarFull(Player player)
		{
			for (int i = 0; i < Player.MaxBuffs; i++) {
				if (player.buffType[i] <= 0)
					return false;
			}

			return true;
		}

		// Only potion/food buffs auto-untoggle on removal - a placed-item buff can legitimately expire via a
		// real DelBuff call on leaving proximity, which must not be read as a dismiss. BuffID.MonsterBanner
		// is the one exception: it's actively held via TimeLeftDoesNotDecrease rather than being
		// proximity-dependent once any banner is toggled on, so a DelBuff on it can only be a deliberate
		// right-click dismiss, and clears every toggled banner to match.
		//
		// Vanilla never wires proximity/aura buffs to the right-click-dismiss UI at all (right-clicking a
		// toggled Cozy Fire never reaches Player.DelBuff), so the mod's own toggle hotkey is the only way to
		// turn those off.
		public static void HandleDismiss(int buffType)
		{
			if (!InfiniteBuffTypes.GetValueOrDefault(buffType))
				return;

			if (buffType == BuffID.MonsterBanner) {
				ToggledBanners.Clear();
			}
			else if (PlacedBuffItems.ContainsValue(buffType)) {
				return;
			}

			InfiniteBuffTypes[buffType] = false;
			BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = false;
			Main.buffNoTimeDisplay[buffType] = false;
		}

		// Re-grants every toggled-on buffType the player doesn't currently have. Called on respawn and on
		// entering the world: LoadWorldData restores the bookkeeping (the dictionary,
		// TimeLeftDoesNotDecrease, buffNoTimeDisplay) but never calls AddBuff. Banners/Garden Gnome need no
		// handling here - ForceProximityFlags runs every tick regardless.
		public static void RegrantToggledBuffs(Player player)
		{
			foreach (var pair in InfiniteBuffTypes.ToArray()) {
				if (!pair.Value)
					continue;

				if (player.FindBuffIndex(pair.Key) < 0)
					SetInfinite(player, pair.Key, on: true);
			}
		}

		public static bool IsInfinite(int buffType) => InfiniteBuffTypes.GetValueOrDefault(buffType);

		public override void SaveWorldData(TagCompound tag)
		{
			int[] onTypes = InfiniteBuffTypes.Where(p => p.Value).Select(p => p.Key).ToArray();
			int[] offTypes = InfiniteBuffTypes.Where(p => !p.Value).Select(p => p.Key).ToArray();

			if (onTypes.Length > 0)
				tag["onTypes"] = onTypes;

			if (offTypes.Length > 0)
				tag["offTypes"] = offTypes;

			if (ToggledBanners.Count > 0)
				tag["toggledBanners"] = ToggledBanners.ToArray();

			if (_gardenGnomeInfinite)
				tag["gardenGnomeInfinite"] = true;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			InfiniteBuffTypes.Clear();
			ToggledBanners.Clear();
			_gardenGnomeInfinite = false;

			if (tag.TryGet("onTypes", out int[] onTypes)) {
				foreach (int buffType in onTypes) {
					InfiniteBuffTypes[buffType] = true;
					BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = true;
					Main.buffNoTimeDisplay[buffType] = true;
				}
			}

			if (tag.TryGet("offTypes", out int[] offTypes)) {
				foreach (int buffType in offTypes)
					InfiniteBuffTypes[buffType] = false;
			}

			if (tag.TryGet("toggledBanners", out int[] toggledBanners)) {
				foreach (int itemType in toggledBanners)
					ToggledBanners.Add(itemType);
			}

			_gardenGnomeInfinite = tag.ContainsKey("gardenGnomeInfinite");
		}

		public override void ClearWorld()
		{
			foreach (int buffType in InfiniteBuffTypes.Keys) {
				BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = false;
				Main.buffNoTimeDisplay[buffType] = false;
			}

			InfiniteBuffTypes.Clear();
			ToggledBanners.Clear();
			_gardenGnomeInfinite = false;
		}
	}
}
