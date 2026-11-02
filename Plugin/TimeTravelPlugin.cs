using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TimeTravelMod
{
	[BepInPlugin("JuanBerta.Obenseuer.TimeTravel", "Time Travel", "1.2.0")]
	public class TimeTravelPlugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> StartTravelKey;
		internal static ConfigEntry<bool> CanBeArrested;
		internal static ConfigEntry<bool> RequireTiredness;

		// True only while the mod-driven custom waiting session is running
		// (between the StartSleep invocation and the vanilla wake-up that clears
		// waitingType). The Harmony patches use this to decide when to suppress
		// the natural wake-up and to re-allow cancelling.
		internal static bool TravelActive;
		internal static float travelStartTime;

		// Set when F10 opens the vanilla sleep-selection UI with the tiredness
		// bypass enabled. Consumed by SleepBypassPatch on the first Sleep() call
		// that happens while that UI is actually visible, so beds / alarm clocks
		// used outside a mod-opened session keep the vanilla tiredness gate.
		internal static bool UiSleepArmed;

		// Static alias so nested patch classes can log (BaseUnityPlugin.Logger is protected).
		internal static ManualLogSource ModLogger;

		private void Awake()
		{
			StartTravelKey = Config.Bind("General", "StartTravelKey", new KeyboardShortcut(KeyCode.F10), "Open the vanilla sleep-time selection UI (like the alarm clock) to choose the hour you want to wake up at.");
			CanBeArrested = Config.Bind("General", "CanBeArrested", true, "Whether skipping time can trigger the vanilla arrest check like a normal sleep.");
			RequireTiredness = Config.Bind("General", "RequireTiredness", false, "If true, time travel only works when the player is tired enough to sleep (vanilla restriction).");

			Harmony harmony = new Harmony("JuanBerta.Obenseuer.TimeTravel");
			harmony.PatchAll();
			ModLogger = Logger;
			Logger.LogInfo("Time Travel loaded (wake-up-hour sleep selection, ESC to cancel; tiredness bypass " + (RequireTiredness.Value ? "disabled" : "enabled") + ").");
		}

		private void Update()
		{
			if (StartTravelKey.Value.IsDown())
			{
				OpenSleepSelection();
			}

			// ESC closes the sleep-time selection UI before any sleep starts.
			if (UiSleepArmed && Input.GetKeyDown(KeyCode.Escape))
			{
				WaitingUI ui = WaitingUI.instance;
				if (ui != null && ui.waitingUIisVisible)
				{
					UiSleepArmed = false;
					ui.CloseTimeSetUI();
					Logger.LogInfo("Time Travel: sleep selection cancelled by ESC.");
				}
			}

			// Session housekeeping for the custom waiting path.
			if (TravelActive)
			{
				WaitingController wc = WaitingController.instance;
				if (wc == null)
				{
					TravelActive = false;
					return;
				}

				// Manual cancel: ESC re-arms the vanilla wake-up path. The mod no
				// longer suppresses anything; if the vanilla cancel does not exist
				// for sleeping, the alarm clock still wakes the player on time.
				if (Input.GetKeyDown(KeyCode.Escape) && Time.unscaledTime - travelStartTime > 0.5f)
				{
					wc.alarmEnabled = false;
					Traverse.Create(wc).Field("currentSleepSetTime").SetValue(0);
					wc.Wakeup(false);
					TravelActive = false;
					Logger.LogInfo("Time Travel: sleep cancelled by ESC.");
					return;
				}

				// Normal finish: the alarm / cancel / event cleared waitingType.
				if (wc.waitingType == WaitingController.Type.None)
				{
					TravelActive = false;
					Logger.LogInfo("Time Travel: session finished, guard disarmed.");
					return;
				}

				// Fallback finish: alarm already expired but CheckEvent could not
				// wake the player (e.g. an event locked waitingType). Retry the
				// vanilla wake-up - WakeupInterceptPatch lets it through because
				// currentSleepSetTime <= 0.
				int remaining = Traverse.Create(wc).Field("currentSleepSetTime").GetValue<int>();
				if (remaining <= 0)
				{
					wc.Wakeup(true);
				}
			}
		}

		private void OpenSleepSelection()
		{
			WaitingController wc = WaitingController.instance;
			if (wc == null)
			{
				Logger.LogWarning("Time Travel: WaitingController not available.");
				return;
			}
			if (wc.waitingType != WaitingController.Type.None)
			{
				Logger.LogWarning("Time Travel: already waiting / sleeping / busy.");
				return;
			}
			if (!wc.CanStartWaitingStaus || WaitingController.WaitingBlocked)
			{
				Logger.LogWarning("Time Travel: waiting is blocked right now.");
				return;
			}
			if (RequireTiredness.Value && !PlayerStats.instance.tiredness.CheckTiredness())
			{
				Logger.LogWarning("Time Travel: player is not tired enough to sleep.");
				return;
			}

			// Mirror Sleep.Use() with hasAlarmClock=true: WaitingUI.SetTimeOpen
			// then shows the hour slider (setAlarm) and lets the player choose
			// how long to sleep, exactly like an alarm clock. ShowTimeSetUI
			// writes quality / canBeArrested / sleepingEvents / usePassoutEvents /
			// crimeArrest / hasAlarmClock, so the later StartSleep reuses them.
			UiSleepArmed = !RequireTiredness.Value;
			wc.ShowTimeSetUI(0f, CanBeArrested.Value, null, false, 0.1f, null, true);

			WaitingUI ui = WaitingUI.instance;
			if (UiSleepArmed && ui != null)
			{
				// Make sure the whole day is reachable on the vanilla slider so the
				// player can pick any wake-up hour.
				Traverse.Create(ui).Field("setAlarmSlider").GetValue<UnityEngine.UI.Slider>().maxValue = 86400f;
				// Default: wake up 8 hours from now (fine-tune with the slider).
				ui.ChangeSleepTimeAndSlider(8f * 3600f);
			}
			Logger.LogInfo("Time Travel: sleep-time selection opened.");
		}
	}

	// Root cure for the natural wake-up: runs at the very start of
	// PlayerStatsTiredness.UpdateStats, i.e. before the sleeping branch decides
	// whether currentStatus dropped to 0. Clamping there removes the
	// frame-vs-tick race entirely (no coroutine involved).
	[HarmonyPatch(typeof(PlayerStatsTiredness), "UpdateStats")]
	internal static class TirednessNaturalWakePatch
	{
		internal static void Prefix(PlayerStatsTiredness __instance)
		{
			if (TimeTravelPlugin.TravelActive && WaitingController.instance != null && WaitingController.instance.IsSleeping && __instance.currentStatus <= 0f)
			{
				__instance.currentStatus = 1f;
			}
		}
	}

	// Safety net that also covers need-based and event wake-ups: every vanilla
	// Wakeup() call is blocked while the mod alarm is still counting down
	// (alarmEnabled && currentSleepSetTime > 0), no matter where it comes from
	// (natural wake-up branch, CheckEvent need check, SleepEventController, ...).
	// Alarm expiry (currentSleepSetTime <= 0) and sessions without the mod flag
	// always pass through.
	[HarmonyPatch(typeof(WaitingController), "Wakeup")]
	internal static class WakeupInterceptPatch
	{
		internal static bool Prefix(WaitingController __instance)
		{
			if (TimeTravelPlugin.TravelActive && __instance.alarmEnabled)
			{
				int remaining = Traverse.Create(__instance).Field("currentSleepSetTime").GetValue<int>();
				if (remaining > 0)
				{
					return false;
				}
			}
			return true;
		}
	}

	// Replaces the vanilla "Sleeping: X:XX" / duration counter with the actual
	// wake-up clock time while the mod session is active, so the sleep-selection
	// slider reads as "which hour will I wake up" instead of "how many hours
	// will I sleep". During the sleep it keeps showing the fixed target time
	// (remaining time ticks down while the game clock advances).
	[HarmonyPatch(typeof(WaitingUI), "UpdateSleptTime")]
	internal static class WakeUpTimeDisplayPatch
	{
		internal static bool Prefix(WaitingUI __instance, float sleepingTimeSeconds)
		{
			if (!TimeTravelPlugin.UiSleepArmed && !TimeTravelPlugin.TravelActive)
			{
				return true;
			}
			TimeOfDayAzure tod = TimeOfDayAzure.instance;
			if (tod == null)
			{
				return true;
			}
			float nowSeconds = tod.currentTimeAndDay.hours * 3600f + tod.currentTimeAndDay.minutes * 60f;
			float wakeSeconds = nowSeconds + sleepingTimeSeconds;
			int daysAhead = (int)(wakeSeconds / 86400f);
			float dayTime = wakeSeconds - (float)daysAhead * 86400f;
			int hour = (int)(dayTime / 3600f);
			int minute = (int)((dayTime - (float)hour * 3600f) / 60f);
			string text;
			if (daysAhead <= 0)
			{
				text = "Wake up at ";
			}
			else if (daysAhead == 1)
			{
				text = "Wake up tomorrow at ";
			}
			else
			{
				text = "Wake up in " + daysAhead + " days at ";
			}
			text += hour.ToString("00") + ":" + minute.ToString("00");
			object label = Traverse.Create(__instance).Field("sleptTimeText").GetValue();
			if (label != null)
			{
				Traverse.Create(label).Property("text").SetValue(text);
			}
			return false;
		}
	}

	// Lets the mod-opened sleep selection UI start a sleep even when the player
	// is not tired (RequireTiredness=false). The vanilla Sleep() refuses to
	// start unless CheckTiredness() passes; we mirror its setup but call the
	// private StartSleep directly. Only fires when UiSleepArmed is set AND the
	// vanilla sleep UI is actually on screen, so beds / alarm clocks used by
	// the player outside a mod session keep the vanilla tiredness gate.
	[HarmonyPatch(typeof(WaitingController), "Sleep", new Type[] { typeof(int) })]
	internal static class SleepBypassPatch
	{
		internal static bool Prefix(WaitingController __instance, int currentSleepSetTime)
		{
			if (!TimeTravelPlugin.UiSleepArmed || TimeTravelPlugin.RequireTiredness.Value)
			{
				return true;
			}
			WaitingUI ui = WaitingUI.instance;
			if (ui == null || !ui.waitingUIisVisible)
			{
				return true;
			}
			TimeTravelPlugin.UiSleepArmed = false;
			if (WaitingController.WaitingBlocked)
			{
				return false;
			}

			__instance.alarmEnabled = currentSleepSetTime != -1;
			Traverse t = Traverse.Create(__instance);
			t.Field("currentSleepSetTime").SetValue(currentSleepSetTime);

			PlayerStatsTiredness tiredness = PlayerStats.instance.tiredness;
			if (tiredness.currentStatus <= 0f)
			{
				// Synchronous clamp before StartSleep so the very first tick
				// cannot wake the player up.
				tiredness.currentStatus = 1f;
			}

			MethodInfo startSleep = AccessTools.DeclaredMethod(typeof(WaitingController), "StartSleep", new Type[] { typeof(float), typeof(bool), typeof(SleepEvent[]), typeof(bool), typeof(float) });
			if (startSleep == null)
			{
				TimeTravelPlugin.ModLogger.LogError("Time Travel: could not find WaitingController.StartSleep.");
				return true;
			}

			// ShowTimeSetUI already wrote quality / canBeArrested / sleepingEvents /
			// usePassoutEvents / crimeArrest; reuse them so the skip behaves exactly
			// like a vanilla alarm-clock sleep.
			float quality = t.Field("quality").GetValue<float>();
			bool canBeArrested = t.Field("canBeArrested").GetValue<bool>();
			SleepEvent[] sleepingEvents = t.Field("sleepingEvents").GetValue<SleepEvent[]>();
			bool usePassoutEvents = t.Field("usePassoutEvents").GetValue<bool>();
			float crimeArrest = t.Field("crimeArrest").GetValue<float>();
			startSleep.Invoke(__instance, new object[] { quality, canBeArrested, sleepingEvents, usePassoutEvents, crimeArrest });

			TimeTravelPlugin.TravelActive = true;
			TimeTravelPlugin.travelStartTime = Time.unscaledTime;
			TimeTravelPlugin.ModLogger.LogInfo("Time Travel: sleeping " + currentSleepSetTime + " second(s) (wake-up hour chosen, tiredness bypassed).");
			return false;
		}
	}
}
