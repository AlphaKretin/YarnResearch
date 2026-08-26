namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// Two-click confirmation for a destructive button: the first click arms a short window (ticked
	// down once per Update tick) instead of acting immediately; only a second click within that window
	// returns true. Disarm() cancels an armed state early, e.g. when the popup/strip closes.
	internal class ConfirmGuard
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
