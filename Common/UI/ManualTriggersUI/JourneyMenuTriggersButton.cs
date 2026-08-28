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
	// The strip's own buttons are real registered Creative Powers (AYarnPower implements ICreativePower,
	// registered via CreativePowerManager.Register<T>) - five one-shot actions plus the free-crafting
	// toggle. Vanilla's own equivalent base class, ASharedButtonPower, declares its abstract members
	// internal to the game assembly, so a real subclass of it isn't possible from a mod - AYarnPower
	// recreates the same one-base/many-leaves shape as a class this mod owns instead.
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
		private static UIElement _actionButtonTemplate;
		private static UIElement _toggleButtonTemplate;

		private static GroupOptionButton<int> _button;
		private static PowerStripUIElement _yarnStrip;
		private static UICreativePowersMenu _menu;

		// Shown by DrawTooltip while _button is hovered - not GroupOptionButton's own Description field,
		// since our button is built with description: null (see AppendButton).
		private static LocalizedText _tooltipText;

		private static HeldItemsActionPower _heldItemsPower;
		private static CascadeActionPower _cascadePower;
		private static MiscCascadeActionPower _miscCascadePower;
		private static FreeCraftingTogglePower _freeCraftingPower;
		private static ShimmerActionPower _shimmerPower;
		private static SacrificeActionPower _sacrificePower;
		private static ClearActionPower _clearPower;
		// The powers whose buttons are currently in the strip - a power can be conditionally hidden (see
		// GetIsUnlocked), and this is what layout counts slots from.
		private static readonly HashSet<AYarnPower> _shownPowers = new();

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
			_tooltipText = Mod.GetLocalization($"{nameof(JourneyMenuTriggersButton)}.Tooltip");
		}

		public override void Unload()
		{
			On_UICreativePowersMenu.CreateMainPowerStrip -= AppendButton;
			On_UICreativePowersMenu.RefreshElementsOrder -= ShowYarnStrip;
			On_UICreativePowersMenu.Draw -= DrawTooltip;
			_button = null;
			_yarnStrip = null;
			_menu = null;
			_actionButtonTemplate = null;
			_toggleButtonTemplate = null;
			_heldItemsPower = null;
			_cascadePower = null;
			_miscCascadePower = null;
			_freeCraftingPower = null;
			_shimmerPower = null;
			_sacrificePower = null;
			_clearPower = null;
			_shownPowers.Clear();
		}

		// Strip order, top to bottom - the single source of truth for layout, so a new power only has to be
		// placed here. Free crafting sits above the auto-research buttons rather than between them, since it
		// isn't part of that sequence, and the catch-all misc scan comes after the specific ones.
		private static IEnumerable<AYarnPower> AllPowers()
		{
			yield return _freeCraftingPower;
			yield return _heldItemsPower;
			yield return _cascadePower;
			yield return _shimmerPower;
			yield return _miscCascadePower;
			yield return _sacrificePower;
			yield return _clearPower;
		}

		// This mod's ModSystem has no UIState of its own to hook an Update into, since the strip lives
		// inside vanilla's UICreativePowersMenu - drives each power's per-tick state (confirm guards, icon
		// tint, a toggle's picked state) and the shimmer button's visibility instead.
		//
		// UpdateUI, not PostUpdateEverything: this is UI state that has to keep updating during Journey
		// Mode's autopause, which skips the update path PostUpdateEverything runs on. A toggle's button
		// otherwise keeps its old picked state until the inventory is closed and reopened.
		public override void UpdateUI(GameTime gameTime)
		{
			if (_yarnStrip == null || _menu == null)
				return;

			// Vanilla's menu only claims the mouse over its own known strips, so without this a click that
			// lands on the YARN strip is left for the world to consume (swinging a tool, placing a block)
			// instead of counting as UI interaction.
			if (_yarnStrip.GetDimensions().ToRectangle().Contains(Main.mouseX, Main.mouseY))
				Main.LocalPlayer.mouseInterface = true;

			object mainCategory = _mainCategoryField.GetValue(_menu);
			if ((int)_currentOptionField.GetValue(mainCategory) != YarnCategoryOption) {
				foreach (AYarnPower power in AllPowers())
					power.OnClosed();
				return;
			}

			bool visibilityChanged = false;
			foreach (AYarnPower power in AllPowers()) {
				bool unlocked = power.GetIsUnlocked();
				if (unlocked == _shownPowers.Contains(power))
					continue;

				if (unlocked) {
					_yarnStrip.Append(power.ButtonElement);
					_shownPowers.Add(power);
				}
				else {
					_yarnStrip.RemoveChild(power.ButtonElement);
					_shownPowers.Remove(power);
				}

				visibilityChanged = true;
			}

			if (visibilityChanged)
				LayoutButtons();

			foreach (AYarnPower power in AllPowers())
				power.PerTickUpdate();
		}

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
			var icon = new ItemIconButton(TextureAssets.Item[ItemID.UnluckyYarn], drawDropShadow: true) {
				IgnoresMouseInteraction = true,
			};
			icon.Width.Set(0f, 1f);
			icon.Height.Set(0f, 1f);
			button.Append(icon);

			CopyStyleFrom(lastVanillaButton, button);

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
				Main.instance.MouseText(_tooltipText.Value);

			if (_yarnStrip == null)
				return;

			foreach (AYarnPower power in AllPowers()) {
				if (power.ButtonElement != null && power.ButtonElement.IsMouseHovering)
					Main.instance.MouseText(power.HoverText.Value);
			}
		}

		// What a fresh GroupOptionButton is missing compared to a real vanilla one: the highlight flag, plus
		// the override colour/opacity set (real values, not the unused defaults). All of them are needed for
		// the selection highlight to cover the whole button rather than part of it, and none has a public
		// setter.
		private static readonly string[] SelectionStyleFields = {
			"ShowHighlightWhenSelected",
			"_UseOverrideColors",
			"_overrideUnpickedColor",
			"_overridePickedColor",
			"_overrideOpacityUnpicked",
			"_overrideOpacityPicked",
		};

		// The template must be a real button of the matching kind: a category button for the category
		// button, an instant-action or toggle button for a strip button.
		private static void CopyStyleFrom(UIElement template, UIElement ours)
		{
			// Each field is resolved separately on each side: a toggle's GroupOptionButton<bool> and an
			// action button's GroupOptionButton<int> are distinct runtime types, so a FieldInfo from one
			// can't read or write the other. Every field here holds a bool, Color, or float, none of which
			// depend on the option type.
			const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

			foreach (string fieldName in SelectionStyleFields) {
				FieldInfo target = ours.GetType().GetField(fieldName, flags);
				FieldInfo source = template.GetType().GetField(fieldName, flags);

				if (target != null && source != null)
					target.SetValue(ours, source.GetValue(template));
			}
		}

		private static void ShowYarnStrip(On_UICreativePowersMenu.orig_RefreshElementsOrder orig, UICreativePowersMenu self)
		{
			orig(self);

			object mainCategory = _mainCategoryField.GetValue(self);
			if ((int)_currentOptionField.GetValue(mainCategory) != YarnCategoryOption)
				return;

			if (_yarnStrip == null) {
				BuildYarnStrip();

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

		// A real vanilla instant-action button (StartDayImmediately, from the Time strip), the closest
		// vanilla equivalent of this mod's one-shot action buttons - unlike the category button, which is a
		// persistent-selection tab.
		private static UIElement GetActionButtonTemplate() =>
			_actionButtonTemplate ??= FindTimeStripButton(button =>
				button is GroupOptionButton<int> intButton &&
				(int)_myOptionField.GetValue(intButton) == CreativePowerManager.Instance.GetPowerId<CreativePowers.StartDayImmediately>());

		// A toggle stays rendered as picked while it's on, which an instant-action button never does, so it
		// takes its colours from vanilla's own toggle instead. Vanilla's toggles are GroupOptionButton<bool>
		// and can't be found by power id the way an int button can - _myOption is just true on every one of
		// them - but the Time strip's first bool button is the freeze-time toggle, the only one there whose
		// picked colour is the yellow active tint rather than the action buttons' blue.
		private static UIElement GetToggleButtonTemplate() =>
			_toggleButtonTemplate ??= FindTimeStripButton(button => button is GroupOptionButton<bool>);

		private static UIElement FindTimeStripButton(Func<UIElement, bool> match)
		{
			var elements = (List<UIElement>)_createTimePowerStripMethod.Invoke(_menu, null);
			return elements.Find(element => match(element));
		}

		private static void CopyActionButtonStyleFields(AYarnPower power)
		{
			UIElement template = power is AYarnTogglePower ? GetToggleButtonTemplate() : GetActionButtonTemplate();
			if (template != null)
				CopyStyleFrom(template, power.ButtonElement);
		}

		private static T RegisterPower<T>(string configName) where T : ICreativePower, new()
		{
			CreativePowerManager.Instance.Register<T>(configName);
			return CreativePowerManager.Instance.GetPower<T>();
		}

		// Registers every power, builds its button, and assembles them into _yarnStrip.
		private static void BuildYarnStrip()
		{
			_heldItemsPower = RegisterPower<HeldItemsActionPower>("yarn_helditems");
			_cascadePower = RegisterPower<CascadeActionPower>("yarn_cascade");
			_miscCascadePower = RegisterPower<MiscCascadeActionPower>("yarn_misccascade");
			_freeCraftingPower = RegisterPower<FreeCraftingTogglePower>("yarn_freecrafting");
			_shimmerPower = RegisterPower<ShimmerActionPower>("yarn_shimmer");
			_sacrificePower = RegisterPower<SacrificeActionPower>("yarn_sacrifice");
			_clearPower = RegisterPower<ClearActionPower>("yarn_clear");

			UIElement template = GetActionButtonTemplate();
			_buttonSlotSize = template != null ? (int)template.Width.Pixels : FallbackButtonSlotSize;

			var info = new CreativePowerUIElementRequestInfo {
				PreferredButtonWidth = _buttonSlotSize,
				PreferredButtonHeight = _buttonSlotSize,
			};

			var elements = new List<UIElement>();
			_shownPowers.Clear();

			foreach (AYarnPower power in AllPowers()) {
				if (power.GetIsUnlocked()) {
					power.ProvidePowerButtons(info, elements);
					_shownPowers.Add(power);
				}
				else {
					// Built but withheld, ready to insert the moment it unlocks.
					power.EnsureButtonBuilt(info);
				}

				CopyActionButtonStyleFields(power);
			}

			_yarnStrip = new PowerStripUIElement("YarnPowers", elements);
			LayoutButtons();
		}

		// Slots are assigned by walking AllPowers and skipping whatever is currently hidden, so no button
		// carries a hardcoded index and the strip never leaves a gap where a hidden button would be. Also
		// resizes the strip itself to fit its own visible content, matching how real strips size themselves.
		private static void LayoutButtons()
		{
			int visibleSlots = 0;

			foreach (AYarnPower power in AllPowers()) {
				if (_shownPowers.Contains(power))
					SetSlotRectangle(power.ButtonElement, visibleSlots++);
			}

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
