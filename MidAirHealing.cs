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

		private readonly Dictionary<FsmState, FsmStateAction[]> recoveryOriginals =
			new Dictionary<FsmState, FsmStateAction[]>();

		private readonly Dictionary<
			FsmTransition,
			(string Name, FsmState State, FsmState Replacement)> focusButtonOriginals =
			new Dictionary<
				FsmTransition,
				(string Name, FsmState State, FsmState Replacement)>();

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
					DumpState(fsm, "Inactive");
					DumpState(fsm, "Button Down");
					DumpState(fsm, "Can Focus?");
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
	}
}
