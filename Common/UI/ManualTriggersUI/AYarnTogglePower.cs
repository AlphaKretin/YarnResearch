namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// A power that stays on until clicked again, rather than acting once. Vanilla's own toggles (FreezeTime
	// and friends) build a GroupOptionButton<bool> whose option is true, and drive its current option from
	// the toggle's state - a live dump of the real Time strip confirmed that shape, and that the "active"
	// look comes purely from the override picked colour/opacity fields rather than from
	// ShowHighlightWhenSelected (false on every real button in that strip).
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
