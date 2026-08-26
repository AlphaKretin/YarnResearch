using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using YarnResearch.Common.Players;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// A small always-visible (while the inventory is open) toggle icon sits just below the vanilla
	// Journey Mode powers button (tModLoader 1.4.5 has no API to add a category into that menu itself -
	// CreativePowerManager/CreativePowers ship with no patch files, confirmed via a dedicated gh search
	// code pass) and opens a vertical column of item-icon buttons for the mod's config-toggle-gated
	// auto-research behaviors, mirroring that menu's visual style.
	public class ManualTriggersUIState : UIState
	{
		// The vanilla Journey Mode powers button's on-screen box, measured live in-game at 1440p in
		// screen pixels (not available from the public source repo): top-left (47, 427), ~50x50. The
		// vanilla popup's own inner power buttons measure ~64x64 in the same screen-pixel terms. These
		// are screen pixels, not UI-layer units - ToUIUnits() converts by dividing out Main.UIScale,
		// since the layer this draws into (InterfaceScaleType.UI, same as vanilla's own icon) multiplies
		// logical UI-unit coordinates by the current UI scale at render time.
		private const float VanillaButtonLeftPx = 47f;
		private const float VanillaButtonTopPx = 427f;
		private const float VanillaButtonSizePx = 50f;
		private const float ActionButtonSizePx = 64f;
		private const float IconGapPx = 16f; // horizontal gap between the vanilla icon and ours
		private const float PopupDropPx = 195f; // vertical gap from the toggle down to its popup
		private const float PaddingPx = 8f; // internal padding within the shared popup background

		private ItemIconButton _toggleButton;
		private UIPanel _actionsContainer;
		private ItemIconButton _shimmerButton;
		private ItemIconButton _sacrificeButton;
		private ItemIconButton _clearButton;
		private bool _panelOpen;
		private bool _shimmerButtonShown;

		private readonly ConfirmGuard _sacrificeGuard = new();
		private readonly ConfirmGuard _clearGuard = new();

		public override void OnInitialize()
		{
			// To the right of the vanilla icon, same row - directly below it would overlap the crafting
			// menu (or the vanilla popup itself when open).
			float toggleTop = ToUIUnits(VanillaButtonTopPx);
			float toggleSize = ToUIUnits(VanillaButtonSizePx);

			float buttonSize = ToUIUnits(ActionButtonSizePx);
			float padding = ToUIUnits(PaddingPx);
			float containerLeft = ToUIUnits(VanillaButtonLeftPx + VanillaButtonSizePx + IconGapPx);
			float containerWidth = buttonSize + padding * 2f;

			// Centered above the popup, not left-aligned to it - the popup is wider than the toggle icon.
			float toggleLeft = containerLeft + (containerWidth - toggleSize) / 2f;

			_toggleButton = new ItemIconButton(GetItemIcon(ItemID.UnluckyYarn), "YARN manual research triggers", drawBackground: false, hoverBorder: GetToggleHoverBorder());
			SetRectangle(_toggleButton, toggleLeft, toggleTop, toggleSize, toggleSize);
			_toggleButton.OnLeftClick += ToggleClicked;
			Append(_toggleButton);

			float popupTop = toggleTop + toggleSize + ToUIUnits(PopupDropPx);

			// Small gap between buttons - pixel-sampling the reference screenshot showed vanilla's own
			// popup has a thin divider line (a few px, background color unchanged on either side)
			// between cells, not a wide empty gap, but not zero either - a prior attempt to close the
			// gap to zero produced overlapping/bulging rounded corners between the individually-backed
			// buttons instead, since UIPanel rounds all four corners uniformly with no way to flatten
			// just the middle ones.
			_actionsContainer = new UIPanel();
			_actionsContainer.SetPadding(0);
			_actionsContainer.Left.Set(containerLeft, 0f);
			_actionsContainer.Top.Set(popupTop, 0f);

			var heldItemsButton = new ItemIconButton(GetItemIcon(ItemID.Binoculars), "Research Held Items");
			SetSlotRectangle(heldItemsButton, 0, buttonSize, padding);
			heldItemsButton.OnLeftClick += HeldItemsClicked;
			_actionsContainer.Append(heldItemsButton);

			var cascadeButton = new ItemIconButton(GetItemIcon(ItemID.WorkBench), "Research Craftable Recipes");
			SetSlotRectangle(cascadeButton, 1, buttonSize, padding);
			cascadeButton.OnLeftClick += CascadeClicked;
			_actionsContainer.Append(cascadeButton);

			_shimmerButton = new ItemIconButton(GetItemIcon(ItemID.BottomlessShimmerBucket), "Research Shimmered Items");
			SetSlotRectangle(_shimmerButton, 2, buttonSize, padding);
			_shimmerButton.OnLeftClick += ShimmerClicked;

			_sacrificeButton = new ItemIconButton(GetInfinitePowersIcon(), "Sacrifice Unresearched Items", sourceRect: InfinitePowersResearchGearFrame);
			_sacrificeButton.OnLeftClick += SacrificeClicked;
			_actionsContainer.Append(_sacrificeButton);

			_clearButton = new ItemIconButton(GetTrashIcon(), "Clear Fully-Researched Items");
			_clearButton.OnLeftClick += ClearClicked;
			_actionsContainer.Append(_clearButton);

			LayoutTrailingButtons(buttonSize, padding);
		}

		// _sacrificeButton/_clearButton's slot shifts up by one whenever the (conditionally shown)
		// _shimmerButton is absent, so the popup never leaves a gap where a hidden button would be.
		private void LayoutTrailingButtons(float buttonSize, float padding)
		{
			int slot = _shimmerButtonShown ? 3 : 2;
			SetSlotRectangle(_sacrificeButton, slot, buttonSize, padding);
			SetSlotRectangle(_clearButton, slot + 1, buttonSize, padding);

			int visibleSlots = slot + 2;
			_actionsContainer.Width.Set(buttonSize + padding * 2f, 0f);
			_actionsContainer.Height.Set(buttonSize * visibleSlots + padding * (visibleSlots + 1), 0f);
		}

		// Vertically stacked slots within _actionsContainer, slot 0 at the top - Top = padding*(slot+1) +
		// buttonSize*slot, matching the container's own height formula of buttonSize*n + padding*(n+1)
		// for n visible slots.
		private static void SetSlotRectangle(UIElement element, int slot, float buttonSize, float padding)
		{
			SetRectangle(element, padding, padding * (slot + 1) + buttonSize * slot, buttonSize, buttonSize);
		}

		public override void Update(GameTime gameTime)
		{
			if (!IsNormalInventoryOpen()) {
				if (_panelOpen) {
					_panelOpen = false;
					RemoveChild(_actionsContainer);
					_sacrificeGuard.Disarm();
					_clearGuard.Disarm();
				}

				return;
			}

			base.Update(gameTime);

			bool shimmerDiscovered = ResearchCascadeSystem.ShimmerDiscovered;
			if (shimmerDiscovered && !_shimmerButtonShown) {
				_actionsContainer.Append(_shimmerButton);
				_shimmerButtonShown = true;
				LayoutTrailingButtons(ToUIUnits(ActionButtonSizePx), ToUIUnits(PaddingPx));
			}
			else if (!shimmerDiscovered && _shimmerButtonShown) {
				_actionsContainer.RemoveChild(_shimmerButton);
				_shimmerButtonShown = false;
				LayoutTrailingButtons(ToUIUnits(ActionButtonSizePx), ToUIUnits(PaddingPx));
			}

			_sacrificeGuard.Update();
			_clearGuard.Update();
			ApplyConfirmState(_sacrificeButton, _sacrificeGuard, "Sacrifice Unresearched Items",
				"Click again to confirm - sacrifices all unresearched items in your inventory!");
			ApplyConfirmState(_clearButton, _clearGuard, "Clear Fully-Researched Items",
				"Click again to confirm - destroys all fully-researched items in your inventory!");
		}

		private static void ApplyConfirmState(ItemIconButton button, ConfirmGuard guard, string normalText, string armedText)
		{
			button.HoverText = guard.Armed ? armedText : normalText;
			button.IconTint = guard.Armed ? Color.OrangeRed : Color.White;
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (IsNormalInventoryOpen())
				base.Draw(spriteBatch);
		}

		private void ToggleClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			_panelOpen = !_panelOpen;

			if (_panelOpen) {
				Append(_actionsContainer);
			}
			else {
				RemoveChild(_actionsContainer);
				_sacrificeGuard.Disarm();
				_clearGuard.Disarm();
			}

			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		private static void HeldItemsClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			YarnResearchPlayer.ManualScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		private static void CascadeClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			ResearchCascadeSystem.ManualCascadeScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		private static void ShimmerClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			ResearchCascadeSystem.RunShimmerCatchupScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		// The vanilla research-slot sacrifice sound (SoundID.Research, variants 1-3, randomly picked per
		// play via LimitsArePerVariant - confirmed via SoundID.TML.cs; distinct from SoundID.ResearchComplete
		// which is variant 0, the "fully researched" fanfare FlushNotifications already plays).
		//
		// No named SoundID exists for the inventory trash-slot sound (that interaction is unmodified
		// vanilla code, not present in the public patch-only repo) - built directly from the underlying
		// asset path instead, matching the two extracted variants Custom/trash_item_0 and _1.
		private static readonly SoundStyle TrashSound = new("Terraria/Sounds/Custom/trash_item_", 0, 2) { LimitsArePerVariant = true };

		// Both bulk buttons are destructive/irreversible, so the first click only arms a short confirm
		// window (see ConfirmGuard) - the actual action only fires on a second click within that window.
		private void SacrificeClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			if (_sacrificeGuard.Click()) {
				if (YarnResearchPlayer.BulkSacrificeUnresearched())
					SoundEngine.PlaySound(SoundID.Research);
			}
			else {
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		private void ClearClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			if (_clearGuard.Click()) {
				if (YarnResearchPlayer.BulkClearResearched())
					SoundEngine.PlaySound(TrashSound);
			}
			else {
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		private static Asset<Texture2D> GetItemIcon(int itemType)
		{
			Main.instance.LoadItem(itemType);
			return TextureAssets.Item[itemType];
		}

		// The vanilla Journey Mode powers menu's own research-gear icon lives in a 21-frame, 36x36-per-frame
		// spritesheet at Content/Images/UI/Creative/Infinite_Powers.xnb, frame index 1 (confirmed by
		// extracting and visually inspecting the actual asset - an earlier guess, Research_GearA, looked
		// close but turned out to be a different UI element entirely).
		private static readonly Rectangle InfinitePowersResearchGearFrame = new(36, 0, 36, 36);

		private static Asset<Texture2D> GetInfinitePowersIcon()
		{
			return ModContent.Request<Texture2D>("Terraria/Images/UI/Creative/Infinite_Powers", AssetRequestMode.ImmediateLoad);
		}

		// The inventory trash-slot icon, at Content/Images/Trash.xnb - not to be confused with
		// UI/ButtonDelete.xnb, which is the (differently-sized) player/world select menu's delete icon.
		private static Asset<Texture2D> GetTrashIcon()
		{
			return ModContent.Request<Texture2D>("Terraria/Images/Trash", AssetRequestMode.ImmediateLoad);
		}

		// Optional custom asset (not yet created) for a hover border that hugs the Unlucky Yarn icon's
		// silhouette, matching vanilla's own icon border, instead of ItemIconButton's rectangular
		// placeholder. Add a transparent PNG at Assets/UI/ManualTriggers/YarnIconBorder.png (any
		// resolution, square aspect ratio) and it'll be picked up automatically - no code change needed.
		private static Asset<Texture2D> GetToggleHoverBorder()
		{
			ModContent.RequestIfExists("YarnResearch/Assets/UI/ManualTriggers/YarnIconBorder", out Asset<Texture2D> asset, AssetRequestMode.ImmediateLoad);
			return asset;
		}

		// Main.playerInventory alone is also true while a chest/other container, or an NPC shop, is open
		// alongside the inventory (the toggle was overlapping the first chest slot) - the vanilla Journey
		// Mode button only shows for a plain inventory-only view, so this adds those same exclusions.
		// Player.chest is the index into Main.chest for a real chest, or one of several negative sentinel
		// values for other storage (piggy bank, safe, Defender's Forge, void vault, etc.) - -1 means none
		// of those are open. Main.npcShop is 0 when no shop panel is open (shops are otherwise 1-indexed).
		// The shop half is untested live (no merchant NPC on the current test worlds yet) but included
		// anyway rather than left out, since the chest case already showed this exact failure mode.
		private static bool IsNormalInventoryOpen() => Main.playerInventory && Main.LocalPlayer.chest == -1 && Main.npcShop == 0;

		// Converts a screen-pixel measurement (e.g. taken from a screenshot) into the logical UI-layer
		// units this UIState's coordinates are in - the InterfaceScaleType.UI layer this draws into
		// multiplies those units by Main.UIScale at render time, same as vanilla's own icon layer.
		private static float ToUIUnits(float screenPixels) => screenPixels / Main.UIScale;

		private static void SetRectangle(UIElement element, float left, float top, float width, float height)
		{
			element.Left.Set(left, 0f);
			element.Top.Set(top, 0f);
			element.Width.Set(width, 0f);
			element.Height.Set(height, 0f);
		}

		// Two-click confirmation for a destructive button: the first click arms a short window (ticked
		// down once per UI Update, i.e. once per game update while the inventory is open) instead of
		// acting immediately; only a second click within that window returns true. Disarm() cancels an
		// armed state early, e.g. when the popup closes.
		private class ConfirmGuard
		{
			private const int ConfirmWindowTicks = 180; // ~3 seconds at 60 ticks/sec

			private int _ticksRemaining;

			public bool Armed => _ticksRemaining > 0;

			public bool Click()
			{
				if (_ticksRemaining > 0) {
					_ticksRemaining = 0;
					return true;
				}

				_ticksRemaining = ConfirmWindowTicks;
				return false;
			}

			public void Update()
			{
				if (_ticksRemaining > 0)
					_ticksRemaining--;
			}

			public void Disarm() => _ticksRemaining = 0;
		}
	}
}
