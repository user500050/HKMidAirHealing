using System.Reflection;
using System.Collections.Generic;

using Modding;
using Mono.Cecil.Cil;
using MonoMod.Cil;

using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

using UnityEngine;

namespace MidAirHealing
{
    public class MidAirHealing : Mod, ITogglableMod
    {
		private bool unloadPending;

		private const string CommittedFinishEvent = "HK_COMMITTED_FINISH";

		private readonly Dictionary<FsmState, FsmStateAction[]> recoveryOriginals =
			new Dictionary<FsmState, FsmStateAction[]>();

		private readonly Dictionary<
			FsmTransition,
			(string Name, FsmState State, FsmState Replacement)> focusButtonOriginals =
			new Dictionary<
				FsmTransition,
				(string Name, FsmState State, FsmState Replacement)>();

		private readonly Dictionary<FsmState, FsmTransition[]> committedOriginals =
			new Dictionary<FsmState, FsmTransition[]>();

		private PlayMakerFSM patchedSpellControl;

		private Rigidbody2D hoverBody;
		private float savedGravity;

		public override string GetVersion() => "1.0.0";

		public override void Initialize()
		{
			if (unloadPending)
			{
				unloadPending = false;
				Log("Pending unload cancelled: mod enabled again.");
			}

			IL.HeroController.CanFocus += AllowAirFocus;
			On.HeroController.CanFocus += CheckCommittedFocus;
			On.HeroController.FallCheck += BeforeFallCheck;
			On.HeroController.FixedUpdate += AfterFixedUpdate;

			On.HeroController.Update -= InspectHealing;
			On.HeroController.Update += InspectHealing;

			Log("MidAirHealing: initialized");
		}

		public void Unload()
		{
			unloadPending = true;

			IL.HeroController.CanFocus -= AllowAirFocus;
			On.HeroController.CanFocus -= CheckCommittedFocus;

			On.HeroController.FallCheck -= BeforeFallCheck;
			On.HeroController.FixedUpdate -= AfterFixedUpdate;

			ReleaseHover();

			Log("Unload requested.");

			TryFinishUnload();

			if (unloadPending)
				Log("Waiting for Spell Control to become Inactive.");
		}

		private void TryFinishUnload()
		{
			if (!unloadPending)
				return;

			if (patchedSpellControl != null
				&& patchedSpellControl.Fsm.ActiveStateName != "Inactive")
				return;

			RestoreFocusOnlyButton();
			RestoreCommittedTransitions();

			foreach (var entry in recoveryOriginals)
				entry.Key.Actions = entry.Value;

			recoveryOriginals.Clear();
			patchedSpellControl = null;
			unloadPending = false;

			On.HeroController.Update -= InspectHealing;

			Log("Unload completed.");
		}

		private sealed class FinishRecoveryAction : FsmStateAction
		{
			public override void OnEnter()
			{
				Fsm.Event("ANIM END");
				Finish();
			}
		}

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

		private void InspectHealing(
			On.HeroController.orig_Update orig,
			HeroController self)
		{
			orig(self);

			if (unloadPending)
			{
				TryFinishUnload();
				return;
			}

			bool inspect = UnityEngine.Input.GetKeyDown(KeyCode.F7);

			bool alreadyPatched =
				patchedSpellControl != null
				&& patchedSpellControl.gameObject == self.gameObject;

			if (alreadyPatched && !inspect)
				return;

			foreach (var fsm in self.GetComponents<PlayMakerFSM>())
			{
				if (fsm.FsmName != "Spell Control")
					continue;

				if (inspect)
				{
					DumpState(fsm, "Focus Start");
					DumpState(fsm, "Focus");
					DumpState(fsm, "Focus S");
					DumpState(fsm, "Focus Left");
					DumpState(fsm, "Focus Right");
				}

				if (alreadyPatched)
					return;

				if (fsm.Fsm.ActiveStateName != "Inactive")
					return;

				ShortenRecovery(fsm, "Focus Get Finish");
				ShortenRecovery(fsm, "Focus Get Finish 2");
				ShortenRecovery(fsm, "Focus Cancel", expectedWaits: 0);
				ShortenRecovery(fsm, "Focus Cancel 2", expectedWaits: 0);

				ApplyFocusOnlyButton(fsm);

				ApplyCommittedHealing(fsm);

				patchedSpellControl = fsm;

				Log("Automatic recovery setup finished.");
				return;
			}
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

		private void AllowAirFocus(ILContext il)
		{
			var cursor = new ILCursor(il);

			bool found = cursor.TryGotoNext(
				MoveType.After,
				instruction => instruction.MatchLdfld<HeroControllerStates>(
					nameof(HeroControllerStates.onGround))
			);

			if (!found)
			{
				LogError("CanFocus: onGround check not found; patch skipped.");
				return;
			}

			cursor.Emit(OpCodes.Pop);
			cursor.Emit(OpCodes.Ldc_I4_1);

			Log("CanFocus: ground requirement removed.");
		}

		private void BeforeFallCheck(
			On.HeroController.orig_FallCheck orig,
			HeroController self)
		{
			StopFocusMotion(self);
			orig(self);
		}

		private void AfterFixedUpdate(
			On.HeroController.orig_FixedUpdate orig,
			HeroController self)
		{
			orig(self);
			StopFocusMotion(self);
		}

		private void StopFocusMotion(HeroController hero)
		{
			var state = hero.cState;

			bool shouldHover =
				state.focusing
				&& !state.onGround
				&& !state.dead
				&& !state.hazardDeath
				&& !state.hazardRespawning
				&& !state.transitioning
				&& !state.recoiling
				&& !state.recoilFrozen
				&& !state.dashing
				&& !state.backDashing
				&& !state.casting
				&& !state.superDashing
				&& !state.swimming;

			if (!shouldHover)
			{
				ReleaseHover();
				return;
			}

			var body = hero.GetComponent<Rigidbody2D>();

			if (body == null)
			{
				ReleaseHover();
				return;
			}

			if (hoverBody != body)
			{
				ReleaseHover();

				hoverBody = body;
				savedGravity = body.gravityScale;

				Log("Hover started.");
			}

			body.gravityScale = 0f;
			body.velocity = Vector2.zero;
		}

		private void ReleaseHover()
		{
			if (hoverBody != null)
			{
				if (hoverBody.gravityScale == 0f)
					hoverBody.gravityScale = savedGravity;

				Log("Hover ended.");
			}

			hoverBody = null;
		}

		private void ApplyFocusOnlyButton(PlayMakerFSM component)
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

		private void RestoreFocusOnlyButton()
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

		private void ApplyCommittedHealing(PlayMakerFSM component)
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

			if (recoveryOriginals.ContainsKey(firstState)
				|| recoveryOriginals.ContainsKey(secondState)
				|| committedOriginals.ContainsKey(firstState)
				|| committedOriginals.ContainsKey(secondState))
			{
				LogError("Committed healing: cycle states already registered.");
				return;
			}

			if (!ApplyCommittedTransitions(component))
				return;

			recoveryOriginals.Add(firstState, firstState.Actions);
			recoveryOriginals.Add(secondState, secondState.Actions);

			committedOriginals.Add(firstState, firstState.Transitions);
			committedOriginals.Add(secondState, secondState.Transitions);

			firstState.Actions = firstActions;
			firstState.Transitions = firstTransitions;

			secondState.Actions = secondActions;
			secondState.Transitions = secondTransitions;

			Log("Committed healing enabled.");
		}

		private bool CheckCommittedFocus(
			On.HeroController.orig_CanFocus orig,
			HeroController self)
		{
			bool allowed = orig(self);

			if (!allowed)
				return false;

			var component = patchedSpellControl;

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
