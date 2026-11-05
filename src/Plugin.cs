using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace QuestMapNames;

// Quests marked "Any location" whose objectives can only be done on some maps show those maps instead, in the task
// list and in the task details. A quest that works on every map keeps "Any location".
//
// The maps come from the objectives: a "on location" condition lists them directly; a quest zone (place an item,
// mark something, visit a spot) does not say its map, so the zones found in each raid are remembered in
// learned-zones.txt, and until a map has been visited a zone named after its map ("..._woods_...") is used.
[BepInPlugin("com.boba.questmapnames", "Quest Map Names", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
	private static ManualLogSource _log;
	private static ConfigEntry<bool> _enabled;
	private static ConfigEntry<int> _exceptFrom;
	private static string _zoneFile;
	private static readonly Dictionary<string, HashSet<string>> Zones = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
	private static bool _namedMap;
	// Extracts are remembered next to the zones, under this prefix.
	private const string ExitKey = "exit:";
	private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

	// The maps a quest has to cover to count as "every map". Labs and the Labyrinth are extra on top.
	private static readonly string[] Regular = { "bigmap", "factory4_day", "Interchange", "Lighthouse", "RezervBase", "Sandbox", "Shoreline", "TarkovStreets", "Woods" };
	private static readonly string[] Order = { "bigmap", "factory4_day", "Interchange", "Lighthouse", "RezervBase", "Sandbox", "Shoreline", "TarkovStreets", "Woods", "laboratory", "Labyrinth" };

	private static readonly (string hint, string map)[] Hints =
	{
		("labyrinth", "Labyrinth"), ("customs", "bigmap"), ("bigmap", "bigmap"), ("factory", "factory4_day"),
		("interchange", "Interchange"), ("lighthouse", "Lighthouse"), ("rezerv", "RezervBase"), ("reserve", "RezervBase"),
		("sandbox", "Sandbox"), ("shoreline", "Shoreline"), ("streets", "TarkovStreets"), ("woods", "Woods"),
		("_wood_", "Woods"), ("(lab)", "laboratory"), ("laboratory", "laboratory")
	};

	private void Awake()
	{
		_log = Logger;
		_enabled = Config.Bind("General", "Enabled", true, "Show the maps of \"Any location\" quests whose objectives need specific maps.");
		_exceptFrom = Config.Bind("General", "Any Except From (maps)", 6, new ConfigDescription("When a quest covers at least this many of the 9 regular maps, it shows \"Any except ...\" with the missing ones instead of a long list.", new AcceptableValueRange<int>(2, 9)));
		_enabled.SettingChanged += (_, _) => Cache.Clear();
		_exceptFrom.SettingChanged += (_, _) => Cache.Clear();
		_zoneFile = Path.Combine(Path.GetDirectoryName(Info.Location), "learned-zones.txt");
		LoadZones();
		Harmony harmony = new Harmony("com.boba.questmapnames");
		harmony.Patch(AccessTools.Method(typeof(NotesTask), nameof(NotesTask.Show)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterTaskRow)));
		harmony.Patch(AccessTools.Method(typeof(NotesTaskDescription), nameof(NotesTaskDescription.Show)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterTaskDetails)));
		harmony.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRaidStart)));
		Logger.LogInfo("Quest Map Names 1.0.0 ready, " + Zones.Count + " quest zones known.");
	}

	private static void AfterTaskRow(Quest quest, TextMeshProUGUI ____locationLabel)
	{
		Relabel(quest, ____locationLabel);
	}

	private static void AfterTaskDetails(Quest quest, TMP_Text ____location)
	{
		Relabel(quest, ____location);
	}

	private static void Relabel(Quest quest, TMP_Text label)
	{
		try
		{
			if (!_enabled.Value || quest?.Template == null || label == null || !string.Equals(quest.Template.LocationId, "any", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			string text = MapsFor(quest.Template);
			if (text != null)
			{
				label.text = text;
			}
		}
		catch (Exception ex)
		{
			_log.LogError("Quest map label failed: " + ex.Message);
		}
	}

	private static string MapsFor(QuestTemplate template)
	{
		if (Cache.TryGetValue(template.Id, out string cached))
		{
			return cached;
		}
		// Each objective on its own: one that can be done on every map does not count, so the objectives tied to
		// certain maps decide what is shown.
		HashSet<string> maps = new HashSet<string>();
		bool unknownZone = false;
		if (template.Conditions != null && template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out ConditionCollection finish) && finish != null)
		{
			foreach (Condition objective in finish)
			{
				HashSet<string> own = new HashSet<string>();
				bool ownUnknown = false;
				_namedMap = false;
				Walk(objective, own, ref ownUnknown);
				// An objective that names its maps has its zones on those maps too.
				ownUnknown &= !_namedMap;
				if (own.Count == 0 || CoversAll(own) && !ownUnknown)
				{
					continue;
				}
				maps.UnionWith(own);
				unknownZone |= ownUnknown;
			}
		}
		string result = Describe(maps, unknownZone);
		Cache[template.Id] = result;
		return result;
	}

	private static bool CoversAll(HashSet<string> maps)
	{
		return Regular.All(maps.Contains);
	}

	private static void Walk(ConditionCollection conditions, HashSet<string> maps, ref bool unknownZone)
	{
		if (conditions == null)
		{
			return;
		}
		foreach (Condition condition in conditions)
		{
			Walk(condition, maps, ref unknownZone);
		}
	}

	private static void Walk(Condition condition, HashSet<string> maps, ref bool unknownZone)
	{
		{
			switch (condition)
			{
				case ConditionCounterCreator counter:
					Walk(counter._templateConditions?.Conditions, maps, ref unknownZone);
					break;
				case ConditionLocation location when location.target != null:
					_namedMap = true;
					foreach (string target in location.target)
					{
						string map = Canonical(target);
						if (map != null)
						{
							maps.Add(map);
						}
					}
					break;
				case ConditionInZone inZone when inZone.zoneIds != null:
					foreach (string zone in inZone.zoneIds)
					{
						AddZone(zone, maps, ref unknownZone);
					}
					break;
				case ConditionZone zoned:
					AddZone(zoned.zoneId, maps, ref unknownZone);
					break;
				case ConditionVisitPlace visit:
					AddZone(visit.target, maps, ref unknownZone);
					break;
				case ConditionExitName exit when !string.IsNullOrWhiteSpace(exit.exitName):
					AddZone(ExitKey + exit.exitName.Trim(), maps, ref unknownZone);
					break;
			}
		}
	}

	private static void AddZone(string zone, HashSet<string> maps, ref bool unknownZone)
	{
		if (string.IsNullOrWhiteSpace(zone))
		{
			return;
		}
		zone = zone.Trim();
		if (Zones.TryGetValue(zone, out HashSet<string> learned))
		{
			maps.UnionWith(learned);
			return;
		}
		string lower = zone.ToLowerInvariant();
		// Extract names such as "Factory Gate" do not say their map, so only learned ones count.
		if (lower.StartsWith(ExitKey))
		{
			unknownZone = true;
			return;
		}
		foreach ((string hint, string map) in Hints)
		{
			if (lower.Contains(hint))
			{
				maps.Add(map);
				return;
			}
		}
		unknownZone = true;
	}

	// Night Factory counts as Factory and Ground Zero's high-level version as Ground Zero.
	private static string Canonical(string id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return null;
		}
		string lower = id.Trim().ToLowerInvariant();
		switch (lower)
		{
			case "any":
				return null;
			case "factory4_night":
				return "factory4_day";
			case "sandbox_high":
				return "Sandbox";
		}
		foreach (string map in Order)
		{
			if (map.ToLowerInvariant() == lower)
			{
				return map;
			}
		}
		return id.Trim();
	}

	private static string Describe(HashSet<string> maps, bool unknownZone)
	{
		if (maps.Count == 0)
		{
			return null;
		}
		string[] missing = Regular.Where(m => !maps.Contains(m)).ToArray();
		if (missing.Length == 0 && !unknownZone)
		{
			return null;
		}
		string more = unknownZone ? ", ?" : "";
		if (!unknownZone && Regular.Length - missing.Length >= _exceptFrom.Value)
		{
			return "Any except " +string.Join(", ", missing.Select(Name).ToArray());
		}
		List<string> sorted = maps.OrderBy(m => { int i = Array.IndexOf(Order, m); return i < 0 ? 99 : i; }).ToList();
		return string.Join(", ", sorted.Select(Name).ToArray()) + more;
	}

	private static string Name(string map)
	{
		string name = map.Localized();
		return string.IsNullOrEmpty(name) ? map : name;
	}

	private static void AfterRaidStart(GameWorld __instance)
	{
		try
		{
			string map = Canonical(__instance.LocationId);
			if (map == null || Array.IndexOf(Order, map) < 0)
			{
				return;
			}
			int added = 0;
			foreach (TriggerWithId trigger in Resources.FindObjectsOfTypeAll<TriggerWithId>())
			{
				if (trigger == null || !trigger.gameObject.scene.IsValid() || string.IsNullOrWhiteSpace(trigger.Id))
				{
					continue;
				}
				string id = trigger.Id.Trim();
				if (!Zones.TryGetValue(id, out HashSet<string> set))
				{
					set = new HashSet<string>();
					Zones[id] = set;
				}
				if (set.Add(map))
				{
					added++;
				}
			}
			foreach (ExfiltrationPoint point in Resources.FindObjectsOfTypeAll<ExfiltrationPoint>())
			{
				string name = (point == null || !point.gameObject.scene.IsValid()) ? null : point.Settings?.Name;
				if (string.IsNullOrWhiteSpace(name))
				{
					continue;
				}
				string id = ExitKey + name.Trim();
				if (!Zones.TryGetValue(id, out HashSet<string> set))
				{
					set = new HashSet<string>();
					Zones[id] = set;
				}
				if (set.Add(map))
				{
					added++;
				}
			}
			if (added > 0)
			{
				SaveZones();
				Cache.Clear();
				_log.LogInfo("Learned " + added + " quest zones on " + map + ".");
			}
		}
		catch (Exception ex)
		{
			_log.LogError("Quest zone scan failed: " + ex.Message);
		}
	}

	private static void LoadZones()
	{
		try
		{
			if (!File.Exists(_zoneFile))
			{
				return;
			}
			foreach (string line in File.ReadAllLines(_zoneFile))
			{
				string[] parts = line.Split('\t');
				if (parts.Length < 2 || parts[0].Length == 0)
				{
					continue;
				}
				if (!Zones.TryGetValue(parts[0], out HashSet<string> set))
				{
					set = new HashSet<string>();
					Zones[parts[0]] = set;
				}
				for (int i = 1; i < parts.Length; i++)
				{
					if (parts[i].Length > 0)
					{
						set.Add(parts[i]);
					}
				}
			}
		}
		catch (Exception ex)
		{
			_log.LogError("Could not read learned-zones.txt: " + ex.Message);
		}
	}

	private static void SaveZones()
	{
		List<string> lines = Zones.OrderBy(z => z.Key, StringComparer.OrdinalIgnoreCase).Select(z => z.Key + "\t" + string.Join("\t", z.Value.OrderBy(m => m).ToArray())).ToList();
		File.WriteAllLines(_zoneFile, lines.ToArray());
	}
}
