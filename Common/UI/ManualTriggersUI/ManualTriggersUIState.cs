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
		private bool _panelOpen;
		private bool _shimmerButtonShown;

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
			SetRectangle(_actionsContainer, containerLeft, popupTop, containerWidth, buttonSize * 3f + padding * 4f);

			var heldItemsButton = new ItemIconButton(GetResearchGearIcon(), "Research Held Items");
			SetRectangle(heldItemsButton, padding, padding, buttonSize, buttonSize);
			heldItemsButton.OnLeftClick += HeldItemsClicked;
			_actionsContainer.Append(heldItemsButton);

			var cascadeButton = new ItemIconButton(GetItemIcon(ItemID.WorkBench), "Research Craftable Recipes");
			SetRectangle(cascadeButton, padding, padding * 2f + buttonSize, buttonSize, buttonSize);
			cascadeButton.OnLeftClick += CascadeClicked;
			_actionsContainer.Append(cascadeButton);

			_shimmerButton = new ItemIconButton(GetItemIcon(ItemID.BottomlessShimmerBucket), "Research Shimmered Items");
			SetRectangle(_shimmerButton, padding, padding * 3f + buttonSize * 2f, buttonSize, buttonSize);
			_shimmerButton.OnLeftClick += ShimmerClicked;
		}

		public override void Update(GameTime gameTime)
		{
			if (!Main.playerInventory) {
				if (_panelOpen) {
					_panelOpen = false;
					RemoveChild(_actionsContainer);
				}

				return;
			}

			base.Update(gameTime);

			bool shimmerDiscovered = ResearchCascadeSystem.ShimmerDiscovered;
			if (shimmerDiscovered && !_shimmerButtonShown) {
				_actionsContainer.Append(_shimmerButton);
				_shimmerButtonShown = true;
			}
			else if (!shimmerDiscovered && _shimmerButtonShown) {
				_actionsContainer.RemoveChild(_shimmerButton);
				_shimmerButtonShown = false;
			}
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (Main.playerInventory)
				base.Draw(spriteBatch);
		}

		private void ToggleClicked(UIMouseEvent evt, UIElement listeningElement)
		{
			_panelOpen = !_panelOpen;

			if (_panelOpen)
				Append(_actionsContainer);
			else
				RemoveChild(_actionsContainer);

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

		private static Asset<Texture2D> GetItemIcon(int itemType)
		{
			Main.instance.LoadItem(itemType);
			return TextureAssets.Item[itemType];
		}

		// The vanilla Journey Mode powers menu's own research-toggle icon, at Content/Images/UI/Creative/
		// Research_GearA.xnb - confirmed live in-game. B and C are alternate variants, not fallbacks.
		private static Asset<Texture2D> GetResearchGearIcon()
		{
			return ModContent.Request<Texture2D>("Terraria/Images/UI/Creative/Research_GearA", AssetRequestMode.ImmediateLoad);
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
	}
}
