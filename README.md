# Stardew HDT Physics

> **Zero hard requirements — plug and play.** Drop into your Mods folder and go.  
> All options are in `config.json`, or use the optional in-game menu with [Generic Mod Config Menu (GMCM)](https://www.nexusmods.com/stardewvalley/mods/5098).

---

## Features

| System | What it does |
|---|---|
| **Body physics** | Jiggle impulses for feminine (breast, butt, thigh, belly) and masculine (butt, groin, thigh, belly) characters triggered by movement, hits, and explosions |
| **Hair physics** | Flowing/bouncy hair motion — calmer indoors, fuller outdoors and in windy areas |
| **Idle motion** | Subtle body sway every ~3 seconds when standing still; automatically cancels when you use a tool |
| **Ragdoll knockback** | Configurable chance (default 50 %) to receive extra knockback force at ≤ 30 HP |
| **Auto profile detection** | Reads live sprite texture names to detect replacer mods; falls back to game gender data |
| **Manual overrides** | Per-NPC gender override in `config.json` — you always have the final say |
| **Preset system** | Soft / Default / High presets, selectable in GMCM or via `config.json` |

---

## Requirements

| Dependency | Required? |
|---|---|
| [SMAPI 4.0.0+](https://smapi.io) | **Yes** (installs with the game via Stardrop/MO2) |
| [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) | Optional — adds an in-game config UI |
| Content Patcher | Optional — detected automatically if present |

**No other mods are required.** The mod works fully without GMCM; GMCM just adds the in-game sliders.

---

## Installation

### Stardrop Mod Manager (recommended)

1. Open **Stardrop Mod Manager**.
2. Click **Add Mod** → select the downloaded `.zip`.
3. Enable the mod in your profile and click **Launch**.

### Mod Organizer 2 (MO2)

1. In MO2, press **Ctrl+M** (Install Mod) and select the `.zip`.
2. Activate the mod in the left panel.
3. Launch Stardew Valley through MO2.

### Manual Install

1. Extract the zip.
2. Copy the `StardewHdtPhysics` folder into your `Stardew Valley/Mods/` directory.  
   The folder must contain `manifest.json`.
3. Launch the game via SMAPI (`StardewModdingAPI.exe` / `StardewModdingAPI`).

---

## Configuration (without GMCM)

Edit `Mods/StardewHdtPhysics/config.json` after the first launch:

```jsonc
{
  "EnableBodyPhysics":      true,
  "EnableHairPhysics":      true,
  "EnableRagdollKnockback": true,
  "EnableIdleMotion":       true,

  "Preset": "Default",        // "Soft" | "Default" | "High"

  // Per-region strength  (0 = off, 1 = default, 2 = maximum)
  "FemaleBreastStrength": 0.55,
  "FemaleButtStrength":   0.50,
  "FemaleThighStrength":  0.40,
  "FemaleBellyStrength":  0.30,

  "MaleButtStrength":     0.45,
  "MaleGroinStrength":    0.45,
  "MaleThighStrength":    0.35,
  "MaleBellyStrength":    0.25,

  "HairStrength":               0.55,
  "RagdollChanceUnderLowHealth": 0.50,

  // Manual gender overrides — highest priority, overrides all auto-detection.
  // Keys are NPC or farmer display names (case-insensitive).
  // Values: "Feminine" | "Masculine" | "Androgynous"
  "GenderOverrides": {
    "Krobus": "Feminine"
  }
}
```

Changes take effect when you load a save.

---

## Compatibility

The mod is designed to work with **any sprite mod** by detecting live texture asset names. It has built-in keyword rules for:

| Mod category | Keywords detected |
|---|---|
| LewdEW | `lewdew`, `lewd_ew` |
| Valley Girls | `valleygirls`, `valley_girls` |
| XTardew Valley | `xtardew` |
| HornyFur / Furry | `hornyfur`, `horny_fur`, `furfur` |
| Anthro packs | `anthro` |

If your replacer uses a keyword not in the list, or if detection is wrong for a specific character, use `GenderOverrides` in `config.json`. This is also useful if you use a mod that turns a male NPC into a female — just add their name with `"Feminine"`.

### Known compatible mods

- Generic Mod Config Menu ✔
- Content Patcher ✔
- LewdEW ✔ (auto-detected)
- Valley Girls ✔ (auto-detected)
- XTardew Valley ✔ (auto-detected)
- HornyFur ✔ (auto-detected)
- All vanilla NPC and farmer sprites ✔
- Modded NPCs and humanoids ✔ (falls back to game gender data)

### No conflicts expected with

- Json Assets, Mail Framework Mod, SpaceCore, Expanded Storage
- Combat mods (CombatRedone, etc.) — physics is purely visual, no gameplay stats changed
- Any texture replacer — physics detects the active texture name at runtime

---

## Extending the profile list

Power users can edit `assets/spriteProfiles.json` inside the mod folder to add custom texture keyword rules:

```json
{ "CharacterName": "", "SpriteTextureContains": "mymod_female_", "ProfileType": "Feminine" }
```

For NPC-name-based rules:
```json
{ "CharacterName": "MyModNpc", "SpriteTextureContains": "", "ProfileType": "Feminine" }
```

---

## File Structure

```
Mods/
└── StardewHdtPhysics/
    ├── manifest.json
    ├── StardewHdtPhysics.dll
    ├── config.json              ← auto-generated on first run
    └── assets/
        ├── presets.json         ← Soft / Default / High presets
        └── spriteProfiles.json  ← auto-detection keyword rules
```

---

## Source & Issue Tracker

https://github.com/JosephsDeadish/Stardew-valley-hdt-hair-and-body-physics-
