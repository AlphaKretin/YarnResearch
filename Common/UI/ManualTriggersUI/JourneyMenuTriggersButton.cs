using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Creative;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// Injects a YARN category button into the vanilla Journey Mode powers menu's main strip (via
	// On_UICreativePowersMenu.CreateMainPowerStrip), then opens a genuine second strip the same way
	// vanilla opens Time/Weather/Personal: by appending a PowerStripUIElement to _container when
	// _mainCategory.CurrentOption matches. There's no first-class category-registration API
	// (OpenMainSubCategory is a closed enum baked into UICreativePowersMenu, and CreativePowerManager has
	// no category concept), so RefreshElementsOrder is hooked to append our own strip since vanilla's own
	// switch has no case for our option.
	//
	// The category button is hand-built with GroupOptionButton<int>, matching sibling buttons' field
	// values via reflection (CreateSubcategoryButton<T> isn't a general factory for this - its generic
	// constraint, T : ICreativePower, IProvideSliderElement, IPowerSubcategoryElement, is a narrow helper
	// for hybrid slider/subcategory entries only).
	//
	// The strip's own 5 action buttons are real registered Creative Powers (AYarnActionPower implements
	// ICreativePower, registered via CreativePowerManager.Register<T>). Vanilla's own equivalent base
	// class, ASharedButtonPower, declares its abstract members internal to the game assembly, so a real
	// subclass of it isn't possible from a mod - AYarnActionPower recreates the same one-base/many-leaves
	// shape as a class this mod owns instead.
	public class JourneyMenuTriggersButton : ModSystem
	{
		// Any int outside OpenMainSubCategory's declared range (0-6) works - MenuTree<TEnum>.CurrentOption
		// and ToggleCategory operate on the underlying int, with no compiled case for this value.
		private const int YarnCategoryOption = 1000;

		// Fallback only if a real template button can't be found - real buttons are 40x40.
		private const int FallbackButtonSlotSize = 40;
		// Matches the real Time strip's own button spacing: buttons sit flush against the strip's left
		// edge (no horizontal centering), with a uniform 4px gap above/below/between them.
		private const float StripIconGap = 4f;
		private static int _buttonSlotSize = FallbackButtonSlotSize;

		private static MethodInfo _mainCategoryButtonClickMethod;
		private static FieldInfo _mainCategoryField;
		private static FieldInfo _currentOptionField;
		private static FieldInfo _buttonsField;
		private static FieldInfo _containerField;
		private static FieldInfo _timePowersStripField;
		private static FieldInfo _buttonCurrentOptionField;
		private static MethodInfo _createTimePowerStripMethod;
		private static FieldInfo _myOptionField;
		private static GroupOptionButton<int> _actionButtonTemplate;

		private static GroupOptionButton<int> _button;
		private static PowerStripUIElement _yarnStrip;
		private static UICreativePowersMenu _menu;

		private static HeldItemsActionPower _heldItemsPower;
		private static CascadeActionPower _cascadePower;
		private static ShimmerActionPower _shimmerPower;
		private static SacrificeActionPower _sacrificePower;
		private static ClearActionPower _clearPower;
		private static bool _shimmerButtonShown;

		public override void Load()
		{
			Type menuType = typeof(UICreativePowersMenu);
			_mainCategoryButtonClickMethod = menuType.GetMethod("MainCategoryButtonClick", BindingFlags.NonPublic | BindingFlags.Instance);
			_mainCategoryField = menuType.GetField("_mainCategory", BindingFlags.NonPublic | BindingFlags.Instance);
			_currentOptionField = _mainCategoryField.FieldType.GetField("CurrentOption");
			_buttonsField = _mainCategoryField.FieldType.GetField("Buttons");
			_containerField = menuType.GetField("_container", BindingFlags.NonPublic | BindingFlags.Instance);
			_timePowersStripField = menuType.GetField("_timePowersStrip", BindingFlags.NonPublic | BindingFlags.Instance);
			_buttonCurrentOptionField = typeof(GroupOptionButton<int>).GetField("_currentOption", BindingFlags.NonPublic | BindingFlags.Instance);
			_createTimePowerStripMethod = menuType.GetMethod("CreateTimePowerStrip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			_myOptionField = typeof(GroupOptionButton<int>).GetField("_myOption", BindingFlags.NonPublic | BindingFlags.Instance);

			On_UICreativePowersMenu.CreateMainPowerStrip += AppendButton;
			On_UICreativePowersMenu.RefreshElementsOrder += ShowYarnStrip;
			On_UICreativePowersMenu.Draw += DrawTooltip;
		}

		public override void SetStaticDefaults()
		{
			TooltipText = Mod.GetLocalization($"{nameof(JourneyMenuTriggersButton)}.Tooltip");
		}

		public override void Unload()
		{
			On_UICreativePowersMenu.CreateMainPowerStrip -= AppendButton;
			On_UICreativePowersMenu.RefreshElementsOrder -= ShowYarnStrip;
			On_UICreativePowersMenu.Draw -= DrawTooltip;
			_button = null;
			_yarnStrip = null;
			_menu = null;
			_heldItemsPower = null;
			_cascadePower = null;
			_shimmerPower = null;
			_sacrificePower = null;
			_clearPower = null;
		}

		private static IEnumerable<AYarnActionPower> AllPowers()
		{
			yield return _heldItemsPower;
			yield return _cascadePower;
			yield return _shimmerPower;
			yield return _sacrificePower;
			yield return _clearPower;
		}

		// This mod's ModSystem has no UIState of its own to hook an Update into, since the strip lives
		// inside vanilla's UICreativePowersMenu - drives each power's per-tick state (confirm guards, icon
		// tint) and the shimmer button's visibility instead.
		public override void PostUpdateEverything()
		{
			if (_yarnStrip == null || _menu == null)
				return;

			object mainCategory = _mainCategoryField.GetValue(_menu);
			if ((int)_currentOptionField.GetValue(mainCategory) != YarnCategoryOption) {
				foreach (AYarnActionPower power in AllPowers())
					power.OnClosed();
				return;
			}

			bool shimmerUnlocked = _shimmerPower.GetIsUnlocked();
			if (shimmerUnlocked != _shimmerButtonShown) {
				if (shimmerUnlocked)
					_yarnStrip.Append(_shimmerPower.Button);
				else
					_yarnStrip.RemoveChild(_shimmerPower.Button);

				_shimmerButtonShown = shimmerUnlocked;
				LayoutTrailingButtons();
			}

			foreach (AYarnActionPower power in AllPowers())
				power.PerTickUpdate();
		}

		// Shown by DrawTooltip while _button is hovered - not GroupOptionButton's own Description field,
		// since our button is built with description: null (see AppendButton).
		private static LocalizedText TooltipText;

		private static List<UIElement> AppendButton(On_UICreativePowersMenu.orig_CreateMainPowerStrip orig, UICreativePowersMenu self)
		{
			_menu = self;

			List<UIElement> elements = orig(self);
			UIElement lastVanillaButton = elements[^1];

			// title/description/iconTexturePath are all null - real vanilla category buttons have
			// _title/_iconTexture/Description null too; they append their own icon as a child element
			// instead (the pattern followed below) rather than using GroupOptionButton's built-in
			// icon/title rendering.
			var button = new GroupOptionButton<int>(
				option: YarnCategoryOption,
				title: null,
				description: null,
				textColor: Color.White,
				iconTexturePath: null,
				textSize: 1f,
				titleAlignmentX: 0.5f,
				titleWidthReduction: 0f);
			button.Left.Set(lastVanillaButton.Left.Pixels, lastVanillaButton.Left.Percent);
			button.Top.Set(lastVanillaButton.Top.Pixels + lastVanillaButton.Height.Pixels, lastVanillaButton.Top.Percent);
			button.Width.Set(lastVanillaButton.Width.Pixels, lastVanillaButton.Width.Percent);
			button.Height.Set(lastVanillaButton.Height.Pixels, lastVanillaButton.Height.Percent);

			Main.instance.LoadItem(ItemID.UnluckyYarn);
			var icon = new ItemIconButton(TextureAssets.Item[ItemID.UnluckyYarn], hoverText: null, drawBackground: false, drawDropShadow: true) {
				IgnoresMouseInteraction = true,
			};
			icon.Width.Set(0f, 1f);
			icon.Height.Set(0f, 1f);
			button.Append(icon);

			CopySelectionStateFields(button, lastVanillaButton);

			// Registering into _mainCategory.Buttons (the same dictionary CreateSubcategoryButton takes
			// as a parameter) is what lets vanilla's own per-frame logic keep this button's _currentOption
			// synced with the group's real selection, instead of the button staying self-matched forever
			// (permanently "picked") and eating clicks.
			object mainCategory = _mainCategoryField.GetValue(self);
			var buttons = (Dictionary<int, GroupOptionButton<int>>)_buttonsField.GetValue(mainCategory);
			buttons[YarnCategoryOption] = button;

			// GroupOptionButton self-matches _currentOption to its own value at construction (appears
			// permanently "selected" until synced) - registration above only gets that synced on the next
			// category click, so stamp the group's real current value on immediately instead of waiting.
			_buttonCurrentOptionField.SetValue(button, _currentOptionField.GetValue(mainCategory));

			// Wired to the real MainCategoryButtonClick, not ToggleMainCategory directly - the latter only
			// toggles _mainCategory.CurrentOption and skips syncing every registered button's own
			// _currentOption field, which MainCategoryButtonClick (real vanilla buttons' own click target)
			// also does.
			button.OnLeftClick += (evt, listeningElement) => _mainCategoryButtonClickMethod.Invoke(self, new object[] { evt, listeningElement });

			_button = button;
			elements.Add(button);
			return elements;
		}

		// GroupOptionButton isn't a class this mod defines, so there's no DrawSelf override to hook a
		// tooltip into directly - drawn after the whole menu instead. Uses Main.instance.MouseText (the
		// plain drop-shadowed hover text vanilla itself uses for item slots/etc.), not
		// UICommon.TooltipMouseText, which draws its own boxed background that doesn't match this menu's
		// own tooltip style.
		private static void DrawTooltip(On_UICreativePowersMenu.orig_Draw orig, UICreativePowersMenu self, SpriteBatch spriteBatch)
		{
			orig(self, spriteBatch);

			if (_button != null && _button.IsMouseHovering)
				Main.instance.MouseText(TooltipText.Value);

			if (_yarnStrip == null)
				return;

			foreach (AYarnActionPower power in AllPowers()) {
				if (power.Button != null && power.Button.IsMouseHovering)
					Main.instance.MouseText(power.HoverText.Value);
			}
		}

		// A real selected vanilla button has two fields beyond opacity that a fresh GroupOptionButton
		// doesn't: ShowHighlightWhenSelected (false) and _UseOverrideColors + the override picked/unpicked
		// colors and opacities (real values, not unused defaults) - both needed for the selection
		// highlight to cover the whole button instead of just part of it. Neither has a public setter.
		private static void CopySelectionStateFields(GroupOptionButton<int> ours, UIElement vanilla)
		{
			CopyField("ShowHighlightWhenSelected", ours, vanilla);
			CopyField("_UseOverrideColors", ours, vanilla);
			CopyField("_overrideUnpickedColor", ours, vanilla);
			CopyField("_overridePickedColor", ours, vanilla);
			CopyField("_overrideOpacityUnpicked", ours, vanilla);
			CopyField("_overrideOpacityPicked", ours, vanilla);
		}

		private static void CopyField(string fieldName, GroupOptionButton<int> ours, UIElement vanilla)
		{
			FieldInfo field = typeof(GroupOptionButton<int>).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
			field?.SetValue(ours, field.GetValue(vanilla));
		}

		private static void ShowYarnStrip(On_UICreativePowersMenu.orig_RefreshElementsOrder orig, UICreativePowersMenu self)
		{
			orig(self);

			object mainCategory = _mainCategoryField.GetValue(self);
			if ((int)_currentOptionField.GetValue(mainCategory) != YarnCategoryOption)
				return;

			if (_yarnStrip == null) {
				_yarnStrip = BuildYarnStrip();

				// PowerStripUIElement's constructor takes no position - real category strips must be
				// positioned by their caller. Time Powers is the closest sibling to our own strip's shape
				// (a plain single-column list of one-shot action buttons), so its Left/Top/VAlign is the
				// reference for where our strip should sit. VAlign matters separately from Top: every real
				// strip shares the same raw Top (0), but is vertically centered on the container via
				// VAlign, with the strip's own height (which varies by button count) determining its
				// actual on-screen position from there.
				var timeStrip = (UIElement)_timePowersStripField.GetValue(self);
				_yarnStrip.Left.Set(timeStrip.Left.Pixels, timeStrip.Left.Percent);
				_yarnStrip.Top.Set(timeStrip.Top.Pixels, timeStrip.Top.Percent);
				_yarnStrip.VAlign = timeStrip.VAlign;
			}

			var container = (UIElement)_containerField.GetValue(self);
			container.Append(_yarnStrip);
		}

		// A real vanilla instant-action button (StartDayImmediately, from the Time strip) to copy known
		// styling fields from onto our own action buttons - see CopyActionButtonStyleFields. Chosen over
		// the category-button sibling used for the YARN category button itself because these are one-shot
		// action buttons (not persistent-selection tabs), and StartDayImmediately is vanilla's own closest
		// example of that same kind of button.
		private static GroupOptionButton<int> GetActionButtonTemplate()
		{
			if (_actionButtonTemplate != null)
				return _actionButtonTemplate;

			var elements = (List<UIElement>)_createTimePowerStripMethod.Invoke(_menu, null);
			ushort targetId = CreativePowerManager.Instance.GetPowerId<CreativePowers.StartDayImmediately>();

			foreach (UIElement el in elements) {
				if (el is GroupOptionButton<int> button && (int)_myOptionField.GetValue(button) == targetId) {
					_actionButtonTemplate = button;
					break;
				}
			}

			return _actionButtonTemplate;
		}

		// Same missing fields as CopySelectionStateFields above, sourced from the real action-button
		// template instead of a category button.
		private static void CopyActionButtonStyleFields(GroupOptionButton<int> ours)
		{
			GroupOptionButton<int> template = GetActionButtonTemplate();
			if (template == null)
				return;

			CopyField("ShowHighlightWhenSelected", ours, template);
			CopyField("_UseOverrideColors", ours, template);
			CopyField("_overrideUnpickedColor", ours, template);
			CopyField("_overridePickedColor", ours, template);
			CopyField("_overrideOpacityUnpicked", ours, template);
			CopyField("_overrideOpacityPicked", ours, template);
		}

		private static T RegisterPower<T>(string configName) where T : ICreativePower, new()
		{
			CreativePowerManager.Instance.Register<T>(configName);
			return CreativePowerManager.Instance.GetPower<T>();
		}

		private static PowerStripUIElement BuildYarnStrip()
		{
			_heldItemsPower = RegisterPower<HeldItemsActionPower>("yarn_helditems");
			_cascadePower = RegisterPower<CascadeActionPower>("yarn_cascade");
			_shimmerPower = RegisterPower<ShimmerActionPower>("yarn_shimmer");
			_sacrificePower = RegisterPower<SacrificeActionPower>("yarn_sacrifice");
			_clearPower = RegisterPower<ClearActionPower>("yarn_clear");

			GroupOptionButton<int> template = GetActionButtonTemplate();
			_buttonSlotSize = template != null ? (int)template.Width.Pixels : FallbackButtonSlotSize;

			var info = new CreativePowerUIElementRequestInfo {
				PreferredButtonWidth = _buttonSlotSize,
				PreferredButtonHeight = _buttonSlotSize,
			};

			var elements = new List<UIElement>();
			_heldItemsPower.ProvidePowerButtons(info, elements);
			_cascadePower.ProvidePowerButtons(info, elements);

			_shimmerButtonShown = _shimmerPower.GetIsUnlocked();
			if (_shimmerButtonShown)
				_shimmerPower.ProvidePowerButtons(info, elements);
			else
				_shimmerPower.EnsureButtonBuilt(info);

			_sacrificePower.ProvidePowerButtons(info, elements);
			_clearPower.ProvidePowerButtons(info, elements);

			foreach (AYarnActionPower power in AllPowers())
				CopyActionButtonStyleFields(power.Button);

			_yarnStrip = new PowerStripUIElement("YarnPowers", elements);

			SetSlotRectangle(_heldItemsPower.Button, 0);
			SetSlotRectangle(_cascadePower.Button, 1);
			LayoutTrailingButtons();

			return _yarnStrip;
		}

		// _sacrificePower/_clearPower's slot shifts up by one whenever the (conditionally shown) shimmer
		// button is absent, so the strip never leaves a gap where a hidden button would be. Also resizes
		// the strip itself to fit its own visible content, matching how real strips size themselves.
		private static void LayoutTrailingButtons()
		{
			SetSlotRectangle(_shimmerPower.Button, 2);

			int slot = _shimmerButtonShown ? 3 : 2;
			SetSlotRectangle(_sacrificePower.Button, slot);
			SetSlotRectangle(_clearPower.Button, slot + 1);

			int visibleSlots = slot + 2;
			_yarnStrip.Width.Set(_buttonSlotSize + StripIconGap * 2f, 0f);
			_yarnStrip.Height.Set(visibleSlots * (_buttonSlotSize + StripIconGap) + StripIconGap, 0f);
		}

		// Every real category strip is a single-column list (fixed one-button width, height scaling with
		// button count), not a horizontal row. Vertical column of slots, slot 0 at the top.
		private static void SetSlotRectangle(UIElement element, int slot)
		{
			element.Left.Set(0f, 0f);
			element.Top.Set(StripIconGap + slot * (_buttonSlotSize + StripIconGap), 0f);
			element.Width.Set(_buttonSlotSize, 0f);
			element.Height.Set(_buttonSlotSize, 0f);
		}
	}
}
