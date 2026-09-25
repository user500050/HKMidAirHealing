using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace MidAirHealing
{
	internal sealed class FastRecovery
	{
		private readonly Action<string> Log;
		private readonly Action<string> LogError;

		private readonly Dictionary<FsmState, FsmStateAction[]> recoveryOriginals =
			new Dictionary<FsmState, FsmStateAction[]>();

		private sealed class FinishRecoveryAction : FsmStateAction
		{
			public override void OnEnter()
			{
				Fsm.Event("ANIM END");
				Finish();
			}
		}

		public FastRecovery(
			Action<string> log,
			Action<string> logError)
		{
			Log = log;
			LogError = logError;
		}

		public void Apply(PlayMakerFSM component)
		{
			ShortenRecovery(component, "Focus Get Finish");
			ShortenRecovery(component, "Focus Get Finish 2");
			ShortenRecovery(component, "Focus Cancel", expectedWaits: 0);
			ShortenRecovery(component, "Focus Cancel 2", expectedWaits: 0);
		}

		public void Restore()
		{
			foreach (var entry in recoveryOriginals)
				entry.Key.Actions = entry.Value;

			recoveryOriginals.Clear();
		}

		private void ShortenRecovery(
			PlayMakerFSM component,
			string stateName,
			int expectedWaits = 1)
		{
			var state = component.Fsm.GetState(stateName);

			if (state == null || recoveryOriginals.ContainsKey(state))
				return;

			bool hasRecoveryExit = false;

			foreach (var transition in state.Transitions)
			{
				if (transition.EventName == "ANIM END"
					&& transition.ToState == "Regain Control")
				{
					hasRecoveryExit = true;
					break;
				}
			}

			if (!hasRecoveryExit)
			{
				LogError(stateName + ": expected recovery transition not found.");
				return;
			}

			var replacement = new List<FsmStateAction>();

			int waits = 0;
			int animations = 0;
			int moves = 0;

			foreach (var action in state.Actions)
			{
				if (action is Wait wait)
				{
					if (wait.finishEvent == null
						|| wait.finishEvent.Name != "ANIM END")
					{
						LogError(stateName + ": unexpected Wait event.");
						return;
					}

					waits++;
					continue;
				}

				if (action is Tk2dPlayAnimationWithEvents)
				{
					animations++;
					continue;
				}

				if (action is iTweenMoveTo move)
				{
					if (move.vectorPosition == null || move.vectorPosition.IsNone
						|| (move.transformPosition != null
							&& !move.transformPosition.IsNone
							&& move.transformPosition.Value != null)
						|| (move.transforms != null && move.transforms.Length > 0)
						|| (move.vectors != null && move.vectors.Length > 0))
					{
						LogError(stateName + ": unexpected movement target.");
						return;
					}

					var setPosition = new SetPosition();
					setPosition.Reset();

					setPosition.gameObject = move.gameObject;
					setPosition.vector = move.vectorPosition;
					setPosition.space = move.space;
					setPosition.Enabled = move.Enabled;

					replacement.Add(setPosition);
					moves++;
					continue;
				}

				replacement.Add(action);
			}

			if (waits != expectedWaits || animations != 1 || moves != 1)
			{
				LogError(stateName + ": unexpected action structure.");
				return;
			}

			replacement.Add(new FinishRecoveryAction { Enabled = true });

			recoveryOriginals.Add(state, state.Actions);
			state.Actions = replacement.ToArray();

			Log(stateName + ": recovery shortened.");
		}
	}
}