namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// A power that stays on until clicked again, rather than acting once. Vanilla's own toggles (FreezeTime
	// and friends) build a GroupOptionButton<bool> whose option is true and drive its current option from
	// the toggle's state; the "active" look comes purely from the override picked colour/opacity fields,
	// not from ShowHighlightWhenSelected, which is false on every real button in that strip.
	public abstract class AYarnTogglePower : AYarnPower<bool>
	{
		protected override bool MyOption => true;
		protected override bool UnpickedOption => false;

		protected abstract bool IsOn { get; }

		public override void PerTickUpdate()
		{
			Button?.SetCurrentOption(IsOn);
		}
	}
}
