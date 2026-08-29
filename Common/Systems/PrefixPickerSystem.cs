using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using YarnResearch.Common.Players;
using YarnResearch.Common.UI;
using YarnResearch.Common.UI.PrefixPickerUI;

namespace YarnResearch.Common.Systems
{
	// Journey Mode item duplication prefix picker - gated on the Goblin Tinkerer's presence, same as
	// vanilla's own prefix-reroll gating.
	public class PrefixPickerSystem : ModSystem
	{
		public static ModKeybind OpenPrefixPickerKeybind { get; private set; }

		private UserInterface _userInterface;
		private PrefixPickerUIState _uiState;
		private On_ItemSlot.hook_DrawItemIcon _drawItemIconHook;

		// Set within the same frame's Draw pass (no next-frame lag needed, since this is only consumed
		// later in the same Draw pass by ApplyHoverPreview) whenever the hovered duplication-grid item has
		// an armed prefix - swapping Main.HoverItem for a prefixed clone right before Mouse Text draws is
		// what makes the tooltip look exactly like a real prefixed item's tooltip (name, stat lines, colors)
		// for free, rather than bolting extra lines onto the unprefixed item's own tooltip.
		private static int _previewItemType = -1;
		private static int _previewPrefixId;
		private static bool _previewIsPopupRow;

		// Whether the current tooltip preview came from hovering a popup row (as opposed to the duplication
		// grid's own armed-prefix preview) - lets YarnResearchGlobalItem add the popup's control hint only
		// to that specific tooltip, not every armed-prefix preview on the grid.
		public static bool IsPreviewingPopupRow => _previewIsPopupRow && _previewItemType >= 0;

		// Which item type the popup is currently open for, so the target slot can get a highlight
		// (distinct from the tinted-background "armed" meaning above) - otherwise pressing the hotkey on
		// a different item while the popup is already open gives no visible indication that the target
		// changed.
		private static int _activePickerItemType = -1;

		public override void Load()
		{
			OpenPrefixPickerKeybind = KeybindLoader.RegisterKeybind(Mod, "OpenPrefixPicker", "Mouse3");

			if (Main.dedServ)
				return;

			_userInterface = new UserInterface();
			_uiState = new PrefixPickerUIState(ClosePicker);
			_uiState.Activate();

			// Same duplication-panel-only context InfiniteBuffSystem scopes its own icon tint to.
			_drawItemIconHook = (On_ItemSlot.orig_DrawItemIcon orig, Item item, int context, SpriteBatch spriteBatch, Vector2 screenPositionForItemCenter, float scale, float sizeLimit, Color environmentColor, float itemFade, bool flip) => {
				if (context == ItemSlot.Context.CreativeInfinite) {
					DuplicationHoverSystem.MarkGridDrawn();

					float iconSize = sizeLimit * scale;

					// The duplication grid's Main.HoverItem isn't reference-equal to the Item instance drawn
					// here (it's a distinct preview/template object), so identity comparison never matches -
					// checking the mouse against this icon's own drawn bounds is self-sufficient instead.
					// DrawItemIcon only gives us the icon's own (scaled-down) box, not the full slot tile
					// vanilla actually hover-tests against, so widen it by the same factor the tint
					// background uses; a narrower box than vanilla's real hover area lets the unprefixed
					// tooltip peek through near the tile's edges.
					if (!item.IsAir && IsMouseOverIcon(screenPositionForItemCenter, iconSize * SlotTint.BackgroundPadding)) {
						DuplicationHoverSystem.MarkHoveringSlot();

						YarnResearchPlayer player = Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>();
						if (player.TryGetDefaultPrefix(item, out int armedPrefix)) {
							_previewItemType = item.type;
							_previewPrefixId = armedPrefix;
							_previewIsPopupRow = false;
						}
					}

					DrawSlotOverlay(item, spriteBatch, screenPositionForItemCenter, iconSize);
				}

				return orig(item, context, spriteBatch, screenPositionForItemCenter, scale, sizeLimit, environmentColor, itemFade, flip);
			};
			On_ItemSlot.DrawItemIcon += _drawItemIconHook;
		}

		public override void Unload()
		{
			OpenPrefixPickerKeybind = null;
			_userInterface = null;
			_uiState = null;

			if (_drawItemIconHook != null) {
				On_ItemSlot.DrawItemIcon -= _drawItemIconHook;
				_drawItemIconHook = null;
			}
		}

		// Duplicates immediately rather than arming a one-shot for the player's next vanilla click, since
		// the popup already knows which prefix was chosen. The item is built the same way vanilla's own
		// duplication path builds it (SetDefaults + OnCreated(JourneyDuplicationItemCreationContext)), so
		// YarnResearchGlobalItem.OnCreated applies the armed prefix through the same validated path (Goblin
		// Tinkerer gating, CanRollPrefix) every other duplication route uses, and any other mod listening
		// for that context still sees it fire. The result goes to Main.mouseItem rather than straight into
		// the inventory, matching vanilla's duplication-click behavior - including no-opping when the cursor
		// is already holding something, rather than overwriting it.
		public static void DuplicateWithPrefix(int itemType, int prefixId)
		{
			if (!Main.mouseItem.IsAir)
				return;

			Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().ArmOneShotPrefix(itemType, prefixId);

			var duplicate = new Item();
			duplicate.SetDefaults(itemType);
			duplicate.stack = duplicate.OnlyNeedOneInInventory() ? 1 : duplicate.maxStack;
			duplicate.OnCreated(new JourneyDuplicationItemCreationContext());

			Main.mouseItem = duplicate;
			SoundEngine.PlaySound(SoundID.Grab);
		}

		private static void DrawSlotOverlay(Item item, SpriteBatch spriteBatch, Vector2 center, float iconSize)
		{
			// Only the slot the picker is currently open for is marked. Having a default prefix isn't marked
			// at all: the player has to hover an item to see which prefix it would get anyway, so an
			// at-a-glance indicator on every affected item earned nothing for the visual noise.
			if (item == null || item.IsAir || item.type != _activePickerItemType)
				return;

			SlotTint.Draw(spriteBatch, center, iconSize, YarnColors.PrefixPickerTargetSlot);
		}

		private static bool IsMouseOverIcon(Vector2 center, float iconSize)
		{
			// Main.mouseX/Y are already-truncated ints, so comparing them against un-rounded float bounds
			// biases the check toward missing the low edge (the true float mouse position can be up to
			// ~1px above the truncated int without the int ever reading as "inside"). Flooring the low
			// bound and ceiling the high bound removes that bias on both edges instead of trading it
			// between them.
			float half = iconSize / 2f;
			int left = (int)MathF.Floor(center.X - half);
			int top = (int)MathF.Floor(center.Y - half);
			int right = (int)MathF.Ceiling(center.X + half);
			int bottom = (int)MathF.Ceiling(center.Y + half);
			return Main.mouseX >= left && Main.mouseX <= right && Main.mouseY >= top && Main.mouseY <= bottom;
		}

		// UpdateUI (not ModPlayer.ProcessTriggers) so the hotkey and popup keep working during Journey
		// autopause - same reasoning as ResearchCascadeSystem's own keybind.
		public override void UpdateUI(GameTime gameTime)
		{
			_previewItemType = -1;
			_previewIsPopupRow = false;

			if (_userInterface.CurrentState != null) {
				_userInterface.Update(gameTime);

				// Gated on the grid specifically, not on the power menu being open at all: switching to
				// another power category leaves the item the picker is for off screen.
				if (!DuplicationHoverSystem.IsGridVisible)
					ClosePicker();
			}

			if (Main.gameMenu || !OpenPrefixPickerKeybind.JustPressed)
				return;

			if (!DuplicationHoverSystem.IsHoveringSlot || !NPC.AnyNPCs(NPCID.GoblinTinkerer))
				return;

			Item hoverItem = Main.HoverItem;
			if (hoverItem.IsAir)
				return;

			// Pressing the hotkey again on the item the picker is already open for closes it. Pressing it on
			// a different item re-targets instead, which is what _activePickerItemType's highlight is for.
			if (hoverItem.type == _activePickerItemType) {
				ClosePicker();
				return;
			}

			List<PrefixCandidate> candidates = PrefixCandidate.GetCandidates(hoverItem);
			if (candidates.Count == 0)
				return;

			_uiState.Populate(hoverItem.type, candidates);
			_userInterface.SetState(_uiState);
			_activePickerItemType = hoverItem.type;
		}

		private void ClosePicker()
		{
			_userInterface.SetState(null);
			_activePickerItemType = -1;
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
			if (index == -1)
				return;

			layers.Insert(index, new LegacyGameInterfaceLayer(
				"YarnResearch: Prefix Picker",
				() => {
					// Draw the popup first, then apply the preview - not the other way around. The popup's
					// own rows set the preview from DrawSelf (see PrefixEntryElement), and _previewItemType
					// gets reset once per tick at the top of UpdateUI; applying before drawing would mean the
					// row's value never survives to be read at all (wiped by next tick's reset before this
					// layer runs again), not just delayed by a frame.
					if (_userInterface?.CurrentState != null)
						_userInterface.Draw(Main.spriteBatch, new GameTime());

					ApplyHoverPreview();

					return true;
				},
				InterfaceScaleType.UI));
		}

		// Called from PrefixEntryElement.DrawSelf while the mouse sits over a popup row, so its tooltip
		// preview (applied below) shows exactly what right-clicking that row would produce.
		public static void SetHoverPreview(int itemType, int prefixId)
		{
			_previewItemType = itemType;
			_previewPrefixId = prefixId;
			_previewIsPopupRow = true;
		}

		// Runs right before vanilla's own Mouse Text layer draws the tooltip for whatever Main.HoverItem
		// currently is - swapping in a prefixed clone here (set earlier this same Draw pass, by the
		// DrawItemIcon hook above or by a popup row's DrawSelf) makes the tooltip render exactly as a real
		// prefixed item's would, with no extra tooltip-line code. Vanilla re-sets Main.HoverItem from real
		// hover state during each frame's slot draws, which run before this layer, so this doesn't leak into
		// other frames or other readers of Main.HoverItem.
		//
		// Main.instance.MouseText("") + Main.mouseText are required, not optional: setting HoverItem alone
		// draws nothing. Real ItemSlot hover code sets that flag itself, but the popup's rows aren't real
		// ItemSlots, so nothing else does it for them. Same pattern as UICommon.TooltipMouseText.
		private static void ApplyHoverPreview()
		{
			if (_previewItemType < 0)
				return;

			var preview = new Item();
			preview.SetDefaults(_previewItemType);
			preview.Prefix(_previewPrefixId);
			Main.HoverItem = preview;
			Main.instance.MouseText("");
			Main.mouseText = true;
		}
	}
}
