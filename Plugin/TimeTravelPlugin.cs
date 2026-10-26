using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TimeTravelMod
{
	[BepInPlugin("JuanBerta.Obenseuer.TimeTravel", "Time Travel", "1.0.0")]
	public class TimeTravelPlugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> SelectHoursKey;
		internal static ConfigEntry<KeyboardShortcut> StartTravelKey;
		internal static ConfigEntry<string> TravelHours;
		internal static ConfigEntry<bool> CanBeArrested;
		internal static ConfigEntry<bool> RequireTiredness;

		private int[] hoursOptions = new int[] { 1, 4, 8, 12, 24 };
		private int selectedIndex;
		private int currentHours = 1;
		private float lastSelectTime = -10f;

		// True only while the mod-driven custom waiting session is running
		// (between the StartSleep invocation and the vanilla wake-up that clears
		// waitingType). The Harmony patches use this to decide when to suppress
		// the natural wake-up and to re-allow cancelling.
		internal static bool TravelActive;
		private float travelStartTime;

		private void Awake()
		{
			SelectHoursKey = Config.Bind("General", "SelectHoursKey", new KeyboardShortcut(KeyCode.F9), "Cycle through the configurable skip durations.");
			StartTravelKey = Config.Bind("General", "StartTravelKey", new KeyboardShortcut(KeyCode.F10), "Start skipping the currently selected amount of hours.");
			TravelHours = Config.Bind("General", "TravelHours", "1,4,8,12,24", "Comma separated list of hours that can be skipped.");
			CanBeArrested = Config.Bind("General", "CanBeArrested", true, "Whether skipping time can trigger the vanilla arrest check like a normal sleep.");
			RequireTiredness = Config.Bind("General", "RequireTiredness", false, "If true, time travel only works when the player is tired enough to sleep (vanilla restriction).");

			ParseHours();

			Harmony harmony = new Harmony("JuanBerta.Obenseuer.TimeTravel");
			harmony.PatchAll();
			Logger.LogInfo("Time Travel loaded (Harmony natural wake-up interception active). Options: " + TravelHours.Value);
		}

		private void ParseHours()
		{
			try
			{
				string[] parts = TravelHours.Value.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
				{
					return;
				}
				int[] parsed = new int[parts.Length];
				int count = 0;
				foreach (string part in parts)
				{
					int v;
					if (int.TryParse(part.Trim(), out v) && v > 0 && v <= 168)
					{
						parsed[count++] = v;
					}
				}
				if (count == 0)
				{
					return;
				}
				hoursOptions = new int[count];
				Array.Copy(parsed, hoursOptions, count);
				if (selectedIndex >= count)
				{
					selectedIndex = 0;
				}
				currentHours = hoursOptions[selectedIndex];
			}
			catch (Exception ex)
			{
				Logger.LogError("Failed to parse TravelHours: " + ex.Message);
			}
		}

		private void Update()
		{
			if (SelectHoursKey.Value.IsDown())
			{
				selectedIndex = (selectedIndex + 1) % hoursOptions.Length;
				currentHours = hoursOptions[selectedIndex];
				lastSelectTime = Time.unscaledTime;
			}
			if (StartTravelKey.Value.IsDown())
			{
				StartTimeTravel();
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
					TravelActive = false;
					Logger.LogInfo("Time Travel: manually cancelled by ESC, vanilla wake-up re-enabled.");
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

		private void OnGUI()
		{
			if (Time.unscaledTime - lastSelectTime < 5f)
			{
				GUI.Label(new Rect(20f, 20f, 500f, 30f), "Time Travel: skip " + currentHours + " hour(s) - press " + StartTravelKey.Value.ToString() + " to start");
			}
		}

		private void StartTimeTravel()
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

			// Reuse the vanilla sleep parameters that ShowTimeSetUI would normally configure.
			Traverse t = Traverse.Create(wc);
			t.Field("quality").SetValue(0f);
			t.Field("canBeArrested").SetValue(CanBeArrested.Value);
			t.Field("sleepingEvents").SetValue(null);
			t.Field("usePassoutEvents").SetValue(false);
			t.Field("crimeArrest").SetValue(0.1f);
			t.Field("hasAlarmClock").SetValue(false);
			t.Field("onWakeup").SetValue(null);

			int seconds = currentHours * 3600;
			if (RequireTiredness.Value)
			{
				// Vanilla path: Sleep() still runs the tiredness check and sets up
				// the alarm clock itself. The player is guaranteed tired enough
				// (currentStatus > 10), so the vanilla alarm wins over the natural
				// wake-up - no interception needed.
				if (!wc.Sleep(seconds))
				{
					Logger.LogWarning("Time Travel: player is not tired enough to sleep.");
				}
			}
			else
			{
				// Custom waiting path (3rd fix, Harmony-based root cure).
				//
				// Why the coroutine guard (a6c35b6) failed:
				// - The natural wake-up lives in PlayerStatsTiredness.UpdateStats
				//   (L76-82): sleeping branch does currentStatus -= RestRate/60f and
				//   if (currentStatus <= 0f) calls WaitingController.Wakeup(). That
				//   tick is driven by PlayerStats.DeltaSeconds which subscribes to
				//   TimeOfDayAzure.SecondsPassed (PlayerStats.cs L227/L306-323).
				//   With SleepTimeSpeed = 150 one game hour = 0.4 real seconds, so
				//   every real frame advances ~150 game seconds > 59 and UpdateStats
				//   fires ONCE PER FRAME.
				// - A frame-driven coroutine (yield return null resumes after the
				//   next frame's Update) therefore always clamps AFTER the tick's
				//   decision - it can never prevent the natural wake-up check in
				//   the same frame, and any hitch / exception / StartCoroutine
				//   timing issue silently kills the guard.
				// - Worse: even a perfectly running clamp only blocks the
				//   tiredness branch. The NEED-BASED wake-up in CheckEvent
				//   (WaitingController.cs L183-185: reason != None &&
				//   sleepingTimeSeconds > 3600 -> Wakeup()) calls Wakeup() directly
				//   without consulting currentStatus - the guard never saw it, so a
				//   hungry / thirsty / bleeding player woke up early with
				//   currentStatus still ~0 (looks exactly like "slept until rested").
				//
				// Fix: intercept at the source with Harmony, no frame timing.
				// - TirednessNaturalWakePatch clamps currentStatus > 0 in a Prefix
				//   of UpdateStats, i.e. BEFORE the sleeping-branch decision runs
				//   every tick - no coroutine, no frame race.
				// - WakeupInterceptPatch blocks every vanilla Wakeup() call while the
				//   mod alarm is still counting down (alarmEnabled && currentSleepSetTime
				//   > 0), covering natural wake-up AND need-based / event wake-ups.
				//   Once currentSleepSetTime <= 0 (alarm expired) it lets the vanilla
				//   Wakeup(alarm:true) through and the session ends.
				// - The mod's Update() re-enables vanilla behaviour on ESC (manual
				//   cancel) or when waitingType becomes None, and retries Wakeup(true)
				//   if the alarm expired but the player is still asleep.
				// StartSleep still keeps the vanilla alarm fields, so CheckEvent's
				// vanilla countdown (currentSleepSetTime -= sec; <= 0 -> Wakeup(true))
				// wakes the player exactly after the selected hours. Sleep events /
				// arrest checks / danger warnings are untouched (they are set up by
				// StartSleep and executed by CheckEvent's timeToEventInSeconds).
				t.Field("alarmEnabled").SetValue(true);
				t.Field("currentSleepSetTime").SetValue(seconds);
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
					Logger.LogError("Time Travel: could not find WaitingController.StartSleep.");
					return;
				}
				startSleep.Invoke(wc, new object[] { 0f, CanBeArrested.Value, null, false, 0.1f });
				TravelActive = true;
				travelStartTime = Time.unscaledTime;
			}
			Logger.LogInfo("Time Travel: skipping " + currentHours + " hour(s).");
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
}
