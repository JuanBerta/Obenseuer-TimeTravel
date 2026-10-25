using System;
using System.Collections;
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

		private void Awake()
		{
			SelectHoursKey = Config.Bind("General", "SelectHoursKey", new KeyboardShortcut(KeyCode.F9), "Cycle through the configurable skip durations.");
			StartTravelKey = Config.Bind("General", "StartTravelKey", new KeyboardShortcut(KeyCode.F10), "Start skipping the currently selected amount of hours.");
			TravelHours = Config.Bind("General", "TravelHours", "1,4,8,12,24", "Comma separated list of hours that can be skipped.");
			CanBeArrested = Config.Bind("General", "CanBeArrested", true, "Whether skipping time can trigger the vanilla arrest check like a normal sleep.");
			RequireTiredness = Config.Bind("General", "RequireTiredness", false, "If true, time travel only works when the player is tired enough to sleep (vanilla restriction).");

			ParseHours();
			Logger.LogInfo("Time Travel loaded. Options: " + TravelHours.Value);
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
				// Vanilla path: Sleep() still runs the tiredness check.
				if (!wc.Sleep(seconds))
				{
					Logger.LogWarning("Time Travel: player is not tired enough to sleep.");
				}
			}
			else
			{
				// Custom waiting function (2nd fix).
				//
				// Root cause of 013533a9 still sleeping until rested:
				// StartSleep does NOT overwrite alarmEnabled / currentSleepSetTime
				// (verified: neither field is assigned anywhere inside StartSleep,
				// L261-330 of the decompiled WaitingController.cs). The Traverse-set
				// alarm clock survives, but CheckEvent's alarm branch is never reached:
				// PlayerStatsTiredness.UpdateStats is the ONLY caller of CheckEvent
				// (PlayerStatsTiredness.cs L64/L79, driven by PlayerStats.DeltaSeconds
				// subscribing to TimeOfDayAzure.SecondsPassed). In its sleeping branch it
				// runs the "rested" check FIRST -
				//   currentStatus -= RestRate/60f;
				//   if (currentStatus <= 0f) { currentStatus = 0f; ... Wakeup(); }
				// - and only THEN calls CheckEvent(sec), which returns immediately at
				//   "if (waitingType == Type.None) return;" because Wakeup already reset
				//   waitingType. With RequireTiredness=false the player is usually not
				//   tired (currentStatus <= 10), so the natural wake-up fires within the
				//   first UpdateStats tick - hence "sleeps until rested" again.
				//
				// Fix: keep the vanilla alarm fields (StartSleep preserves them) and run
				// a frame-driven guard coroutine that clamps tiredness above 0. That stops
				// the natural wake-up from stealing the sleep, so CheckEvent's vanilla
				// countdown (currentSleepSetTime -= sec; <= 0 -> Wakeup(alarm:true)) runs
				// to completion and wakes the player exactly after the selected hours.
				// Sleep events / arrest checks / danger warnings are untouched (they are
				// set up by StartSleep and executed by CheckEvent's timeToEventInSeconds).
				t.Field("alarmEnabled").SetValue(true);
				t.Field("currentSleepSetTime").SetValue(seconds);
				PlayerStatsTiredness tiredness = PlayerStats.instance.tiredness;
				if (tiredness.currentStatus <= 0f)
				{
					// Clamp synchronously BEFORE StartSleep so the very first
					// UpdateStats tick cannot wake the player up.
					tiredness.currentStatus = 1f;
				}
				MethodInfo startSleep = AccessTools.DeclaredMethod(typeof(WaitingController), "StartSleep", new Type[] { typeof(float), typeof(bool), typeof(SleepEvent[]), typeof(bool), typeof(float) });
				if (startSleep == null)
				{
					Logger.LogError("Time Travel: could not find WaitingController.StartSleep.");
					return;
				}
				startSleep.Invoke(wc, new object[] { 0f, CanBeArrested.Value, null, false, 0.1f });
				StartCoroutine(SleepGuard(wc, tiredness));
			}
			Logger.LogInfo("Time Travel: skipping " + currentHours + " hour(s).");
		}

		// Frame-driven guard for the custom waiting session:
		// - Clamps tiredness above 0 so the vanilla "rested" natural wake-up never fires
		//   before CheckEvent's alarm countdown reaches zero.
		// - Fallback: if the alarm already expired but CheckEvent did not wake the
		//   player (e.g. an event locked waitingType), call the vanilla Wakeup directly.
		private IEnumerator SleepGuard(WaitingController wc, PlayerStatsTiredness tiredness)
		{
			while (true)
			{
				yield return null;
				if (wc.waitingType == WaitingController.Type.None)
				{
					yield break;
				}
				if (tiredness.currentStatus <= 0f)
				{
					tiredness.currentStatus = 1f;
				}
				int remaining = Traverse.Create(wc).Field("currentSleepSetTime").GetValue<int>();
				if (remaining <= 0)
				{
					wc.Wakeup(true);
					yield break;
				}
			}
		}
	}
}
