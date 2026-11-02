# Obenseuer-TimeTravel

BepInEx 6 mod for **Obenseuer** that lets you skip forward in game time **exactly like an alarm clock**, reusing the vanilla sleep, pass-out and danger mechanics.

## Features

- Press **F10** to open the vanilla sleep-time selection UI (the same one the alarm clock item uses) and pick the exact hour you want to wake up at - the slider shows "Wake up at HH:MM" instead of a sleep duration.
- Press **ESC** while the sleep is running to wake up immediately (it also closes the selection UI before confirming).
- Fully reuses the vanilla sleep pipeline: `WaitingController` (`Type.Sleeping`), `TimeOfDayAzure` time acceleration, mental health drain, **sleep events** (`SleepEventController.CalculateEvent`), and the **arrest check** (`Crime.CheckCrimesForArrest`).
- By default the tiredness requirement is bypassed so you can skip time anywhere; flip `RequireTiredness` to keep the vanilla "too tired / not tired enough" restriction.

## How it works

Vanilla sleep: `Sleep.Use()` → `WaitingController.ShowTimeSetUI(...)` (configures quality / canBeArrested / sleepingEvents / usePassoutEvents / crimeArrest) → `Sleep(seconds)` → `StartSleep(...)` → `WaitingController.CheckEvent(sec)` advances `sleepingTimeSeconds` and triggers events, then `Wakeup()`.

This mod:

1. Press **F10**. The mod calls `WaitingController.ShowTimeSetUI(0f, canBeArrested, null, false, 0.1f, null, hasAlarmClock: true)`, so `WaitingUI.SetTimeOpen` shows the hour slider (`setAlarm`) exactly like an alarm clock.
2. Pick the wake-up hour in the vanilla UI and confirm. A Harmony `Prefix` on `WaitingUI.UpdateSleptTime` makes the label read "Wake up at HH:MM" (target clock time) instead of the vanilla duration, while the slider still fine-tunes the exact hour. The player's choice flows through the vanilla `WaitingUI.StartSleep` → `WaitingController.Sleep(currentSleepSetTime)`.
3. With `RequireTiredness=false`, a Harmony `Prefix` on `Sleep(int)` (`SleepBypassPatch`) mirrors the vanilla setup but invokes the private `StartSleep(...)` directly, bypassing the tiredness gate while keeping all events/danger checks. The existing wake-up interception (`TirednessNaturalWakePatch` + `WakeupInterceptPatch`) keeps the session running for the full selected duration.
4. With `RequireTiredness=true`, the tiredness check runs first and the vanilla `Sleep` path is used unchanged.

## Installation

1. Install [BepInEx 6](https://github.com/BepInEx/BepInEx) into the game folder.
2. Drop `Obenseuer-TimeTravel.dll` into `BepInEx/plugins`.
3. Launch the game.

## Configuration

| Config | Section | Default | Description |
| --- | --- | --- | --- |
| `StartTravelKey` | General | `F10` | Open the vanilla sleep-time selection UI to choose the wake-up hour (like the alarm clock). |
| `CanBeArrested` | General | `true` | Allow the vanilla arrest check during the skip. |
| `RequireTiredness` | General | `false` | If true, only skip time when the player is tired enough to sleep. |

Config file: `BepInEx/config/JuanBerta.Obenseuer.TimeTravel.cfg`

## Build

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Obenseuer-TimeTravel.csproj /p:Configuration=Release /p:Platform=x64
```

Output: `bin\Release\Obenseuer-TimeTravel.dll`
