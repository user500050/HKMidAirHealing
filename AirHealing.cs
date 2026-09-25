using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;

namespace MidAirHealing
{
	internal sealed class AirHealing
	{
		private readonly Action<string> Log;
		private readonly Action<string> LogError;

		private Rigidbody2D hoverBody;
		private float savedGravity;

		public AirHealing(
			Action<string> log,
			Action<string> logError)
		{
			Log = log;
			LogError = logError;
		}

		public void Enable()
		{
			IL.HeroController.CanFocus += AllowAirFocus;
			On.HeroController.FallCheck += BeforeFallCheck;
			On.HeroController.FixedUpdate += AfterFixedUpdate;
		}

		public void Disable()
		{
			IL.HeroController.CanFocus -= AllowAirFocus;
			On.HeroController.FallCheck -= BeforeFallCheck;
			On.HeroController.FixedUpdate -= AfterFixedUpdate;

			ReleaseHover();
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

				//Log("Hover started.");
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

				//Log("Hover ended.");
			}

			hoverBody = null;
		}
	}
}