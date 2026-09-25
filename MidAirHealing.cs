using System.Collections.Generic;

using UnityEngine;

using Modding;
using HutongGames.PlayMaker;

namespace MidAirHealing
{
    public class MidAirHealing : Mod, ITogglableMod, IGlobalSettings<ModSettings>, IMenuMod
	{
		private bool unloadPending;
		private bool settingsPending;
		private bool hooksEnabled;

		private PlayMakerFSM patchedSpellControl;

		private readonly FocusOnlyButton focusOnlyButton;
		private readonly AirHealing airHealing;
		private readonly FastRecovery fastRecovery;
		private readonly CommittedHealing committedHealing;
		private readonly FsmDiagnostics diagnostics;

		public bool ToggleButtonInsideMenu => true;

		private ModSettings settings = new ModSettings();

		public void OnLoadGlobal(ModSettings loadedSettings)
		{
			settings = loadedSettings ?? new ModSettings();
		}

		public ModSettings OnSaveGlobal()
		{
			return settings;
		}

		public List<IMenuMod.MenuEntry> GetMenuData(
	IMenuMod.MenuEntry? toggleButtonEntry)
		{
			var entries = new List<IMenuMod.MenuEntry>
			{
				new IMenuMod.MenuEntry
				{
					Name = "MidAir Healing",
					Description =
						"Allows focusing and hovering in the air. "
						+ "Changes take effect after exiting healing.",
					Values = new[] { "Off", "On" },
					Loader = () => settings.AirHealingEnabled ? 1 : 0,

					Saver = index =>
					{
						settings.AirHealingEnabled = index == 1;
						RequestSettingsApply();
						SaveGlobalSettings();
					}
				},

				new IMenuMod.MenuEntry
				{
					Name = "Focus Only Button",
					Description =
						"General button immediately starts focusing. "
						+ "Use Quick Cast for spells.",
					Values = new[] { "Off", "On" },
					Loader = () => settings.FocusOnlyButtonEnabled ? 1 : 0,

					Saver = index =>
					{
						settings.FocusOnlyButtonEnabled = index == 1;
						RequestSettingsApply();
						SaveGlobalSettings();
					}
				},

				new IMenuMod.MenuEntry
				{
					Name = "Committed Healing",
					Description =
						"Releasing the button does not cancel the current cycle. "
						+ "Hold the button to repeat.",
					Values = new[] { "Off", "On" },
					Loader = () => settings.CommittedHealingEnabled ? 1 : 0,

					Saver = index =>
					{
						settings.CommittedHealingEnabled = index == 1;
						RequestSettingsApply();
						SaveGlobalSettings();
					}
				}
			};
			if (toggleButtonEntry.HasValue)
				entries.Insert(0, toggleButtonEntry.Value);

			return entries;
		}

		public MidAirHealing()
		{
			focusOnlyButton = new FocusOnlyButton(
				message => Log(message),
				message => LogError(message));
			airHealing = new AirHealing(
				message => Log(message),
				message => LogError(message));
			fastRecovery = new FastRecovery(
				message => Log(message),
				message => LogError(message));
			committedHealing = new CommittedHealing(
				message => Log(message),
				message => LogError(message));
			diagnostics = new FsmDiagnostics(message => Log(message));
		}

		public override string GetVersion() => "1.1.0";

		public override void Initialize()
		{
			if (unloadPending)
			{
				unloadPending = false;
				Log("Pending unload cancelled: mod enabled again.");
			}

			hooksEnabled = true;
			ApplyFeatureHooks();
			settingsPending = true;

			On.HeroController.Update -= OnHeroUpdate;
			On.HeroController.Update += OnHeroUpdate;

			Log("MidAirHealing: initialized");
		}

		public void Unload()
		{
			hooksEnabled = false;
			unloadPending = true;

			airHealing.Disable();
			committedHealing.Disable();

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

			focusOnlyButton.Restore();
			committedHealing.Restore();
			fastRecovery.Restore();

			patchedSpellControl = null;
			unloadPending = false;

			On.HeroController.Update -= OnHeroUpdate;

			Log("Unload completed.");
		}

		private void OnHeroUpdate(
			On.HeroController.orig_Update orig,
			HeroController self)
		{
			orig(self);

			if (unloadPending)
			{
				TryFinishUnload();
				return;
			}

			if (!hooksEnabled)
				return;

			bool inspect = UnityEngine.Input.GetKeyDown(KeyCode.F7);

			bool alreadyPatched =
				patchedSpellControl != null
				&& patchedSpellControl.gameObject == self.gameObject;

			if (alreadyPatched && !inspect && !settingsPending)
				return;

			foreach (var fsm in self.GetComponents<PlayMakerFSM>())
			{
				if (fsm.FsmName != "Spell Control")
					continue;

				if (inspect)
					diagnostics.DumpHealingStates(fsm);

				if (alreadyPatched && !settingsPending)
					return;

				if (fsm.Fsm.ActiveStateName != "Inactive")
					return;

				if (settingsPending)
				{
					focusOnlyButton.Restore();
					committedHealing.Restore();
					fastRecovery.Restore();

					ApplyFeatureHooks();
				}

				ApplySpellFeatures(fsm);

				patchedSpellControl = fsm;
				settingsPending = false;

				Log("Spell Control settings applied.");
				return;
			}
		}

		private void ApplyFeatureHooks()
		{
			airHealing.Disable();
			committedHealing.Disable();

			if (settings.AirHealingEnabled)
				airHealing.Enable();

			if (settings.CommittedHealingEnabled)
				committedHealing.Enable();
		}

		private void ApplySpellFeatures(PlayMakerFSM fsm)
		{
			fastRecovery.Apply(fsm);

			if (settings.FocusOnlyButtonEnabled)
				focusOnlyButton.Apply(fsm);

			if (settings.CommittedHealingEnabled)
				committedHealing.Apply(fsm);
		}

		private void RequestSettingsApply()
		{
			settingsPending = true;
		}
	}
}
