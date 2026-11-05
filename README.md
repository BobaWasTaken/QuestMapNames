# Quest Map Names

A small SPT 4.1 client plugin. Quests that say **Any location** but can only be done on some maps show those maps instead, in the task list and in the task details.

| Quest | Before | After |
|---|---|---|
| Bad Rep Evidence | Any location | Customs |
| Inevitable Response | Any location | Reserve, Woods |
| Illegal Logging | Any location | Customs, Lighthouse, Shoreline, Woods |
| The Punisher - Part 6 | Any location | Any except Factory |
| The Guide | Any location | Any location (it works on every map) |

## How it finds the maps

Each objective is checked on its own. An objective that can be done anywhere (find, hand over, kill anywhere) does not count, so the objectives tied to certain maps decide what is shown. A quest only keeps **Any location** when all of its objectives work on every map.

- **Objectives that list their maps** ("survive on", "kill on") are read straight from the quest.
- **Quest zones** (place an item, mark something, visit a spot) and **specific extracts** do not say their map in the quest data. The plugin notes which zones and extracts exist on each map whenever a raid loads, and keeps them in `learned-zones.txt` next to the DLL. Until a map has been played, a zone named after its map (for example `kill_in_forest_woods`) is used. A label ends in `, ?` while some of its zones are still unknown.
- Night Factory counts as Factory, and the high-level Ground Zero as Ground Zero.
- Labels only ever show map names. The zones are used behind the scenes to work out the map.
- When a quest covers 6 or more of the 9 regular maps it shows **Any except ...** instead of a long list. The number can be changed in F12.

### Play each map once

For quests with zone or extract objectives, the plugin needs to have seen each map once. One raid on each map is enough, in any order, and you can leave right away. Loading into a map records **every** quest zone and extract on it, including those of quests you do not have yet or have not unlocked. So playing every map once before you get a quest works: its maps show as soon as it appears. Zones added only by a temporary event (such as Halloween) are recorded only during that event.

Example, a quest with spots on Customs and Woods:

| Maps played | Label |
|---|---|
| Neither | Any location |
| Customs only | Customs, ? |
| Both | Customs, Woods |

Quests whose objectives list their maps directly need none of this; they show their maps right away.

## Install

Extract the release zip into your SPT folder (the one with `BepInEx`). It adds `BepInEx/plugins/QuestMapNames/QuestMapNames.dll`. Client only; nothing on the server changes.

## Settings (F12 -> Quest Map Names)

- **Enabled**
- **Any Except From (maps)**: how many of the 9 regular maps a quest must cover before it shows "Any except ..." (default 6).

## Building

`dotnet build -c Release` in `src`. The project references the game's and BepInEx's DLLs from `F:\Spt`; change the paths in `QuestMapNames.csproj` to your SPT folder.

## License

MIT
