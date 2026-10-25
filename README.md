# Obenseuer-TimeTravel

BepInEx 6 mod for **Obenseuer** that lets you skip forward in game time (1 / 4 / 8 / 12 / 24 hours, configurable) **exactly like sleeping**, reusing the vanilla sleep, pass-out and danger mechanics.

## Features

- Cycle through configurable skip durations with one key, start the skip with another key.
- Fully reuses the vanilla sleep pipeline: `WaitingController` (`Type.Sleeping`), `TimeOfDayAzure` time acceleration, mental health drain, **sleep events** (`SleepEventController.CalculateEvent`), and the **arrest check** (`Crime.CheckCrimesForArrest`).
- By default the tiredness requirement is bypassed so you can skip time anywhere; flip `RequireTiredness` to keep the vanilla "too tired / not tired enough" restriction.

## How it works

Vanilla sleep: `Sleep.Use()` → `WaitingController.ShowTimeSetUI(...)` (configures quality / canBeArrested / sleepingEvents / usePassoutEvents / crimeArrest) → `Sleep(seconds)` → `StartSleep(...)` → `WaitingController.CheckEvent(sec)` advances `sleepingTimeSeconds` and triggers events, then `Wakeup()`.

This mod:

1. Press **F9** to cycle the selected duration through `TravelHours` (default `1,4,8,12,24`).
2. Press **F10** to start the skip. The mod writes the same private fields that `ShowTimeSetUI` would set (`quality=0`, `canBeArrested=<cfg>`, `sleepingEvents=null`, `usePassoutEvents=false`, `crimeArrest=0.1`, `hasAlarmClock=false`) and then either:
   - calls `WaitingController.Sleep(hours * 3600)` when `RequireTiredness=true` (keeps the tiredness gate), or
   - invokes the private `WaitingController.StartSleep(...)` directly when `RequireTiredness=false` (bypasses the tiredness gate, keeps all events/danger checks).

## Installation

1. Install [BepInEx 6](https://github.com/BepInEx/BepInEx) into the game folder.
2. Drop `Obenseuer-TimeTravel.dll` into `BepInEx/plugins`.
3. Launch the game.

## Configuration

| Config | Section | Default | Description |
| --- | --- | --- | --- |
| `SelectHoursKey` | General | `F9` | Cycle through the skip durations. |
| `StartTravelKey` | General | `F10` | Start skipping the selected hours. |
| `TravelHours` | General | `1,4,8,12,24` | Comma separated list of hours to choose from. |
| `CanBeArrested` | General | `true` | Allow the vanilla arrest check during the skip. |
| `RequireTiredness` | General | `false` | If true, only skip time when the player is tired enough to sleep. |

Config file: `BepInEx/config/JuanBerta.Obenseuer.TimeTravel.cfg`

## Build

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Obenseuer-TimeTravel.csproj /p:Configuration=Release /p:Platform=x64
```

Output: `bin\Release\Obenseuer-TimeTravel.dll`
