using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using YarnResearch.Common.Configs;

namespace YarnResearch.Common.Systems
{
	public class InfiniteBuffSystem : ModSystem
	{
		// buffType -> explicit toggle state. Only buffTypes actually touched (player toggle, auto-default,
		// tiering) get an entry - absence means "never decided", not "off". Covers potions/food and every
		// normal category-3 placed item (below) - NOT banners or Garden Gnome, which have no buffType of
		// their own and use bespoke per-tick proximity forcing instead (see ToggledBanners/_gardenGnomeInfinite).
		private static readonly Dictionary<int, bool> InfiniteBuffTypes = new();

		// Curated whitelist for category 3 (placed buff items that grant a normal Player.buffType buff) -
		// itemType -> buffType.
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

		// Banners are a bespoke category-3 case: every banner shares one generic BuffID.MonsterBanner buff,
		// while the per-enemy damage bonus is a separate non-buff bool array (Main.SceneMetrics.NPCBannerBuff)
		// that vanilla recomputes from tile proximity every tick. This stores which individual banner
		// *items* are toggled on. The shared BuffID.MonsterBanner buff itself goes through the normal
		// SetInfinite/TimeLeftDoesNotDecrease path (granted once, when the set goes from empty to non-empty)
		// rather than relying on vanilla's own tick-based re-granting from the forced proximity flags below -
		// relying on vanilla's granting caused the buff to flicker on/off. ForceProximityFlags below still
		// forces the per-enemy damage-bonus flags every tick (that's read live at hit-time, not gated to a
		// specific tick phase, so timing there isn't an issue).
		private static readonly HashSet<int> ToggledBanners = new();

		// Garden Gnome's luck bonus is not a real Player.buffType/BuffID at all (confirmed: no buff icon,
		// implemented purely via the Player.HasGardenGnomeNearby proximity bool) - same per-tick forcing
		// approach as banners, just a single bool instead of a set.
		private static bool _gardenGnomeInfinite;

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

			// Draws a tinted slot-background texture behind items shown as infinite-toggled in the Journey
			// Mode Duplication panel (ItemSlot.Context.CreativeInfinite) - scoped to that one context
			// deliberately, rather than GlobalItem.PreDrawInInventory everywhere, per the plan's design
			// (avoids a distracting highlight in normal inventory/hotbar/chests). Hooks DrawItemIcon
			// specifically (not the outer Draw method) since it hands us the icon's exact center point and
			// size limit directly, unlike Draw's own background-rectangle geometry. Pattern (draw
			// TextureAssets.InventoryBack* directly with an arbitrary Color tint via SpriteBatch.Draw,
			// centered/scaled by hand) confirmed via AutoTrash's own ItemSlot.cs, one of the mods the
			// tModLoader wiki's Open-Source-Mods page names as citable reference material.
			_drawItemIconHook = (On_ItemSlot.orig_DrawItemIcon orig, Item item, int context, SpriteBatch spriteBatch, Vector2 screenPositionForItemCenter, float scale, float sizeLimit, Color environmentColor, float itemFade, bool flip) => {
				if (context == ItemSlot.Context.CreativeInfinite && IsItemInfinite(item))
					DrawInfiniteBackground(spriteBatch, screenPositionForItemCenter, sizeLimit * scale);

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

		private static readonly Color InfiniteHighlightColor = new(255, 140, 0);

		// iconSize is the icon's own max draw box (sizeLimit * scale) - the background is drawn somewhat
		// larger than that so it reads as the tile behind the icon rather than a same-size copy peeking
		// out from the edges; tune BackgroundPadding here if it looks too big/small in practice.
		private const float BackgroundPadding = 1.6f;

		private static void DrawInfiniteBackground(SpriteBatch spriteBatch, Vector2 center, float iconSize)
		{
			Texture2D background = TextureAssets.InventoryBack9.Value;
			float backgroundScale = iconSize * BackgroundPadding / background.Width;
			var origin = new Vector2(background.Width, background.Height) / 2f;
			spriteBatch.Draw(background, center, null, InfiniteHighlightColor, 0f, origin, backgroundScale, SpriteEffects.None, 0f);
		}

		// Whether hoverItem is something TryToggle would actually act on - used to gate the tooltip hint,
		// independent of its current on/off state.
		public static bool IsToggleable(Item item)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.InfiniteResearchedBuffs)
				return false;

			if (!ResearchCascadeSystem.IsResearched(item.type))
				return false;

			if (item.type == ItemID.GardenGnome || IsBannerItem(item.type) || PlacedBuffItems.ContainsKey(item.type))
				return true;

			if (item.buffType <= 0)
				return false;

			return item.potion || ItemID.Sets.IsFood[item.type];
		}

		public static bool IsItemInfinite(Item item)
		{
			if (item == null || item.IsAir)
				return false;

			if (item.type == ItemID.GardenGnome)
				return _gardenGnomeInfinite;

			if (IsBannerItem(item.type))
				return ToggledBanners.Contains(item.type);

			if (PlacedBuffItems.TryGetValue(item.type, out int placedBuffType))
				return IsInfinite(placedBuffType);

			return item.buffType > 0 && IsInfinite(item.buffType);
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
		private static bool IsHoveringDuplicationMenu()
		{
			return Main.CreativeMenu.Enabled && !Main.CreativeMenu.Blocked;
		}

		// ItemID.Sets.BannerStrength[type].Enabled is NOT a "is this item a banner" flag - it's true for
		// every item (it gates the per-item damage-scaling curve override, unrelated to banner-ness) - so
		// the actual banner check has to go through NPCLoader's own item<->banner mapping instead, the same
		// one ModBannerTile.NearbyEffects itself uses (returns -1 for anything that isn't a banner item).
		private static bool IsBannerItem(int itemType) => NPCLoader.BannerItemToNPC(itemType) >= 0;

		// Called every tick from YarnResearchPlayer.PostUpdateMiscEffects, after vanilla's own tile-proximity
		// scan has run for this tick - re-forces the bespoke proximity flags for banners/Garden Gnome so
		// vanilla's own buff-granting/luck-calculation logic (which reads these later the same tick) sees
		// them as active. Both flags reset to their real proximity values every tick by vanilla itself, so
		// this must run every tick, not just once at toggle time.
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

		public static void HandleItemResearched(Item item)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.InfiniteResearchedBuffs)
				return;

			if (!ResearchCascadeSystem.IsResearched(item.type))
				return;

			if (item.type == ItemID.GardenGnome) {
				_gardenGnomeInfinite = true;
				return;
			}

			if (IsBannerItem(item.type)) {
				if (!ToggledBanners.Contains(item.type)) {
					bool granted = ToggledBanners.Count > 0 || SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true);
					if (granted)
						ToggledBanners.Add(item.type);
				}
				return;
			}

			if (PlacedBuffItems.TryGetValue(item.type, out int placedBuffType)) {
				if (!InfiniteBuffTypes.ContainsKey(placedBuffType))
					SetInfinite(Main.LocalPlayer, placedBuffType, on: true);
				return;
			}

			if (item.potion && item.buffType > 0) {
				if (!InfiniteBuffTypes.ContainsKey(item.buffType))
					SetInfinite(Main.LocalPlayer, item.buffType, on: true);
				return;
			}

			if (ItemID.Sets.IsFood[item.type] && item.buffType > 0)
				HandleFoodResearched(item.buffType);
		}

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
		// gives chat feedback (see SetInfinite/CanGrantMonsterBanner callers) since it's a direct player action.
		public static void TryToggle(Item hoverItem)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.InfiniteResearchedBuffs)
				return;

			if (!ResearchCascadeSystem.IsResearched(hoverItem.type))
				return;

			if (hoverItem.type == ItemID.GardenGnome) {
				_gardenGnomeInfinite = !_gardenGnomeInfinite;
				return;
			}

			if (IsBannerItem(hoverItem.type)) {
				if (ToggledBanners.Remove(hoverItem.type)) {
					if (ToggledBanners.Count == 0)
						SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: false);
				}
				else if (ToggledBanners.Count > 0 || SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true, isManualToggle: true)) {
					ToggledBanners.Add(hoverItem.type);
				}

				return;
			}

			if (PlacedBuffItems.TryGetValue(hoverItem.type, out int placedBuffType)) {
				bool placedCurrentlyOn = InfiniteBuffTypes.GetValueOrDefault(placedBuffType);
				SetInfinite(Main.LocalPlayer, placedBuffType, on: !placedCurrentlyOn, isManualToggle: true);
				return;
			}

			if (hoverItem.buffType <= 0)
				return;

			bool currentlyOn = InfiniteBuffTypes.GetValueOrDefault(hoverItem.buffType);
			SetInfinite(Main.LocalPlayer, hoverItem.buffType, on: !currentlyOn, isManualToggle: true);
		}

		// Returns whether the toggle was actually applied - false means a toggle-ON was refused by the
		// buff-cap safeguard (see callers: banner toggling needs to know this to decide whether to add the
		// banner to ToggledBanners).
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

		// Only categories 1-2 (potions/food) auto-untoggle on removal - a category-3 (placed item) buff can
		// legitimately expire via a real DelBuff call on leaving proximity, which must NOT be treated as a
		// dismiss (see plan's "Detecting dismissal" section). BuffID.MonsterBanner is the one category-3
		// exception: since it's actively held via TimeLeftDoesNotDecrease (not proximity-dependent) once any
		// banner is toggled on, any DelBuff on it can only be a deliberate right-click dismiss - clears
		// every toggled banner to match. Confirmed live 2026-08-27: right-clicking a toggled Cozy Fire (the
		// Campfire buff) never calls Player.DelBuff at all, even once, across several attempts - this isn't
		// something our TimeLeftDoesNotDecrease flag is blocking, vanilla simply doesn't wire proximity/aura
		// buffs to the right-click-dismiss UI in the first place. The mod's own toggle hotkey is therefore
		// the only way to turn these off, which is already correct - no further fix needed or possible here.
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

		// Called from YarnResearchPlayer.OnRespawn and OnEnterWorld - re-grants every toggled-on buffType the
		// player doesn't currently have. Banners/Garden Gnome need no special handling here - ForceProximityFlags
		// runs every tick regardless, including the tick right after respawn or entering the world.
		public static void RegrantOnRespawn(Player player)
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
