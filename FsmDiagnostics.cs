using System;
using System.Reflection;
using HutongGames.PlayMaker;

namespace MidAirHealing
{
	internal sealed class FsmDiagnostics
	{
		private readonly Action<string> Log;

		public FsmDiagnostics(Action<string> log)
		{
			Log = log;
		}

		public void DumpHealingStates(PlayMakerFSM component)
		{
			DumpState(component, "Focus Start");
			DumpState(component, "Focus");
			DumpState(component, "Focus S");
			DumpState(component, "Focus Left");
			DumpState(component, "Focus Right");
		}

		private void DumpState(PlayMakerFSM component, string stateName)
		{
			var state = component.Fsm.GetState(stateName);

			if (state == null)
			{
				Log("State not found: " + stateName);
				return;
			}

			Log("=== " + stateName + " ===");

			for (int i = 0; i < state.Actions.Length; i++)
			{
				var action = state.Actions[i];

				Log("Action " + i + ": " + action.GetType().FullName
					+ "; enabled = " + action.Enabled);

				var fields = action.GetType().GetFields(
	BindingFlags.Public | BindingFlags.Instance);

				foreach (var field in fields)
				{
					object value = field.GetValue(action);

					string text;

					if (value == null)
						text = "<null>";
					else if (value is FsmEvent fsmEvent)
						text = fsmEvent.Name;
					else
						text = value.ToString();

					Log("    " + field.Name + " = " + text);
				}
			}

			foreach (var transition in state.Transitions)
			{
				Log("Transition: " + transition.EventName
					+ " -> " + transition.ToState);
			}
		}
	}
}