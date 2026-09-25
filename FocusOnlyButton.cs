using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace MidAirHealing
{
	internal sealed class FocusOnlyButton
	{
		private readonly Action<string> Log;
		private readonly Action<string> LogError;

		private readonly Dictionary<
			FsmTransition,
			(string Name, FsmState State, FsmState Replacement)> focusButtonOriginals =
			new Dictionary<
				FsmTransition,
				(string Name, FsmState State, FsmState Replacement)>();

		public FocusOnlyButton(
			Action<string> log,
			Action<string> logError)
		{
			Log = log;
			LogError = logError;
		}

		public void Apply(PlayMakerFSM component)
		{
			var inactive = component.Fsm.GetState("Inactive");
			var buttonDown = component.Fsm.GetState("Button Down");
			var focus = component.Fsm.GetState("Can Focus?");

			if (inactive == null || buttonDown == null || focus == null)
			{
				LogError("Focus-only: required states not found.");
				return;
			}

			FsmTransition pressed = null;

			foreach (var transition in inactive.Transitions)
			{
				if (transition.EventName != "BUTTON DOWN")
					continue;

				if (pressed != null)
				{
					LogError("Focus-only: duplicate BUTTON DOWN transition.");
					return;
				}

				pressed = transition;
			}

			if (pressed == null)
			{
				LogError("Focus-only: BUTTON DOWN transition not found.");
				return;
			}

			if (focusButtonOriginals.ContainsKey(pressed))
				return;

			if (pressed.ToState != buttonDown.Name)
			{
				LogError("Focus-only: unexpected BUTTON DOWN target.");
				return;
			}

			focusButtonOriginals.Add(
				pressed,
				(pressed.ToState, pressed.ToFsmState, focus));

			pressed.ToState = focus.Name;
			pressed.ToFsmState = focus;

			Log("Focus-only button enabled.");
		}

		public void Restore()
		{
			foreach (var entry in focusButtonOriginals)
			{
				var transition = entry.Key;
				var saved = entry.Value;

				if (transition.ToState == saved.Replacement.Name
					&& transition.ToFsmState == saved.Replacement)
				{
					transition.ToState = saved.Name;
					transition.ToFsmState = saved.State;
				}
			}

			focusButtonOriginals.Clear();
		}
	}
}