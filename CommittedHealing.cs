using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace MidAirHealing
{
	internal sealed class CommittedHealing
	{
		private readonly Action<string> Log;
		private readonly Action<string> LogError;

		private const string CommittedFinishEvent = "HK_COMMITTED_FINISH";

		private readonly Dictionary<FsmState, FsmTransition[]> committedOriginals =
			new Dictionary<FsmState, FsmTransition[]>();

		private readonly Dictionary<FsmState, FsmStateAction[]> committedActionOriginals =
			new Dictionary<FsmState, FsmStateAction[]>();

		private PlayMakerFSM spellControl;

		private sealed class CompleteHealingCycleAction : FsmStateAction
		{
			private readonly FsmFloat duration;
			private float remaining;
			private bool completed;

			public CompleteHealingCycleAction(FsmFloat duration)
			{
				this.duration = duration;
			}

			public override void OnEnter()
			{
				remaining = duration.Value;
				completed = false;

				if (remaining <= 0f)
					CompleteCycle();
			}

			public override void OnUpdate()
			{
				if (completed)
					return;

				remaining -= UnityEngine.Time.deltaTime;

				if (remaining <= 0f)
					CompleteCycle();
			}

			private void CompleteCycle()
			{
				completed = true;

				string nextEvent = IsFocusHeld()
					? "WAIT"
					: CommittedFinishEvent;

				Finish();
				Fsm.Event(nextEvent);
			}
		}

		public CommittedHealing(
			Action<string> log,
			Action<string> logError)
		{
			Log = log;
			LogError = logError;
		}

		public void Enable()
		{
			On.HeroController.CanFocus += CheckCommittedFocus;
		}

		public void Disable()
		{
			On.HeroController.CanFocus -= CheckCommittedFocus;
		}

		public void Restore()
		{
			RestoreCommittedTransitions();

			foreach (var entry in committedActionOriginals)
				entry.Key.Actions = entry.Value;

			committedActionOriginals.Clear();
			spellControl = null;
		}

		private bool ApplyCommittedTransitions(PlayMakerFSM component)
		{
			var expected = new Dictionary<string, string>
			{
				{ "Focus Start", "Focus Cancel" },
				{ "Focus", "Grace Check" },
				{ "Focus S", "Grace Check 2" },
				{ "Focus Left", "Grace Check 2" },
				{ "Focus Right", "Grace Check 2" }
			};

			var replacements = new Dictionary<FsmState, FsmTransition[]>();

			foreach (var pair in expected)
			{
				var state = component.Fsm.GetState(pair.Key);

				if (state == null)
				{
					LogError("Committed healing: missing state " + pair.Key);
					return false;
				}

				if (committedOriginals.ContainsKey(state))
					continue;

				var remaining = new List<FsmTransition>();
				int matches = 0;

				foreach (var transition in state.Transitions)
				{
					if (transition.EventName != "BUTTON UP")
					{
						remaining.Add(transition);
						continue;
					}

					if (transition.ToState != pair.Value)
					{
						LogError("Committed healing: unexpected exit in " + pair.Key);
						return false;
					}

					matches++;
				}

				if (matches != 1)
				{
					LogError("Committed healing: expected one BUTTON UP in " + pair.Key);
					return false;
				}

				replacements.Add(state, remaining.ToArray());
			}

			foreach (var pair in replacements)
			{
				committedOriginals.Add(pair.Key, pair.Key.Transitions);
				pair.Key.Transitions = pair.Value;
			}

			return true;
		}

		private void RestoreCommittedTransitions()
		{
			foreach (var pair in committedOriginals)
				pair.Key.Transitions = pair.Value;

			committedOriginals.Clear();
		}

		private static bool IsFocusHeld()
		{
			var manager = GameManager.instance;

			if (manager == null)
				return false;

			var input = manager.inputHandler;

			return input != null
				&& input.inputActions != null
				&& input.inputActions.cast.IsPressed;
		}

		private bool PrepareCycle(
			PlayMakerFSM component,
			string healName,
			string repeatName,
			string finishName,
			out FsmState state,
			out FsmStateAction[] actions,
			out FsmTransition[] transitions)
		{
			state = component.Fsm.GetState(healName);
			actions = null;
			transitions = null;

			var repeat = component.Fsm.GetState(repeatName);
			var finish = component.Fsm.GetState(finishName);

			if (state == null || repeat == null || finish == null)
			{
				LogError("Committed healing: required cycle states not found.");
				return false;
			}

			var originalActions = state.Actions;

			var wait = originalActions.Length > 0
				? originalActions[originalActions.Length - 1] as Wait
				: null;

			if (wait == null
				|| wait.finishEvent == null
				|| wait.finishEvent.Name != "WAIT"
				|| wait.time == null
				|| wait.realTime)
			{
				LogError("Committed healing: unexpected final Wait in " + healName);
				return false;
			}

			int repeatExits = 0;

			foreach (var transition in state.Transitions)
			{
				if (transition.EventName == CommittedFinishEvent)
				{
					LogError("Committed healing: finish event already exists.");
					return false;
				}

				if (transition.EventName != "WAIT")
					continue;

				if (transition.ToState != repeat.Name)
				{
					LogError("Committed healing: unexpected repeat target.");
					return false;
				}

				repeatExits++;
			}

			if (repeatExits != 1)
			{
				LogError("Committed healing: expected one WAIT transition.");
				return false;
			}

			actions = (FsmStateAction[])originalActions.Clone();

			actions[actions.Length - 1] =
				new CompleteHealingCycleAction(wait.time)
				{
					Enabled = wait.Enabled
				};

			var exits = new List<FsmTransition>(state.Transitions);

			exits.Add(new FsmTransition
			{
				FsmEvent = FsmEvent.GetFsmEvent(CommittedFinishEvent),
				ToState = finish.Name,
				ToFsmState = finish
			});

			transitions = exits.ToArray();
			return true;
		}

		public void Apply(PlayMakerFSM component)
		{
			var firstHeal = component.Fsm.GetState("Focus Heal");

			if (firstHeal != null && committedOriginals.ContainsKey(firstHeal))
				return;

			if (component.Fsm.Variables.FindFsmBool("Dream Focus") == null)
			{
				LogError("Committed healing: Dream Focus variable not found.");
				return;
			}

			foreach (var transition in component.Fsm.GlobalTransitions)
			{
				if (transition.EventName == "BUTTON UP")
				{
					LogError("Committed healing: global BUTTON UP transition found.");
					return;
				}
			}

			if (!PrepareCycle(
				component, "Focus Heal", "Full HP?", "Focus Get Finish",
				out var firstState, out var firstActions, out var firstTransitions))
				return;

			if (!PrepareCycle(
				component, "Focus Heal 2", "Full HP? 2", "Focus Get Finish 2",
				out var secondState, out var secondActions, out var secondTransitions))
				return;

			if (committedActionOriginals.ContainsKey(firstState)
				|| committedActionOriginals.ContainsKey(secondState)
				|| committedOriginals.ContainsKey(firstState)
				|| committedOriginals.ContainsKey(secondState))
			{
				LogError("Committed healing: cycle states already registered.");
				return;
			}

			if (!ApplyCommittedTransitions(component))
				return;

			committedActionOriginals.Add(firstState, firstState.Actions);
			committedActionOriginals.Add(secondState, secondState.Actions);

			committedOriginals.Add(firstState, firstState.Transitions);
			committedOriginals.Add(secondState, secondState.Transitions);

			firstState.Actions = firstActions;
			firstState.Transitions = firstTransitions;

			secondState.Actions = secondActions;
			secondState.Transitions = secondTransitions;

			spellControl = component;

			Log("Committed healing enabled.");
		}

		private bool CheckCommittedFocus(
			On.HeroController.orig_CanFocus orig,
			HeroController self)
		{
			bool allowed = orig(self);

			if (!allowed)
				return false;

			var component = spellControl;

			if (component == null || component.gameObject != self.gameObject)
				return allowed;

			var healState = component.Fsm.GetState("Focus Heal");

			if (healState == null || !committedOriginals.ContainsKey(healState))
				return allowed;

			if (component.Fsm.ActiveStateName != "Can Focus?")
				return allowed;

			var dreamFocus = component.Fsm.Variables.FindFsmBool("Dream Focus");

			if (dreamFocus == null || dreamFocus.Value)
				return allowed;

			var data = self.playerData;

			return data != null
				&& data.health > 0
				&& data.health < data.CurrentMaxHealth
				&& data.focusMP_amount > 0
				&& data.MPCharge >= data.focusMP_amount;
		}
	}
}