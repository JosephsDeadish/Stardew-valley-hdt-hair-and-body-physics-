# Stardew HDT Physics

> **Zero hard requirements — plug and play.** Drop into your Mods folder and go.  
> All options are in `config.json`, or use the optional in-game menu with [Generic Mod Config Menu (GMCM)](https://www.nexusmods.com/stardewvalley/mods/5098).

---

## Features

| System | What it does |
|---|---|
| **Body physics** | Jiggle impulses for feminine (breast, butt, thigh, belly) and masculine (butt, groin, thigh, belly) characters triggered by movement, hits, and explosions |
| **HDT Hair physics** | Flowing/bouncy motion on ALL hair types — vanilla, mod-added, and Fashion Sense hairs. Reacts to movement, wind, **rain** (wet/droopy), and **snow** (light flutter). Calmer indoors |
| **Idle motion** | Subtle body sway every ~3 seconds when standing still; automatically cancels when you use a tool |
| **Ragdoll knockback** | Configurable chance (default 50 %) to receive extra knockback force at ≤ 30 HP |
| **Monster archetype physics** | Every monster type gets appropriate physics: **Slime** = bouncy jello, **Bat/Ghost** = floppy wings, **Serpent/Grub** = squishy stretch, **Fly/Bug** = wing+leg vibration, **Wolf/Bear** = fur ripple, **Skeleton** = snappy bone clatter. All generic monsters also get physics |
| **Monster ragdoll** | Ragdoll-style knockback when monsters are struck; ragdolled monsters flatten grass they crash through |
| **Female monster overlay** | Beast girls, slime girls, funtari slimes and similar mods additionally get humanoid body jiggle on top of their archetype physics |
| **Farm animal physics** | Body jiggle for all farm animals: light (chicken, duck, rabbit) are bouncier; heavy (cow, sheep, pig) are more grounded. Sword/tool swings cause a cosmetic startle reaction — no damage |
| **Pet physics** | Cats and dogs included automatically in humanoid NPC physics |
| **NPC sword knockdown** | Sword swings near NPCs/pets apply a harmless cosmetic knockback — no damage, no anger |
| **Environmental physics** | Grass bends when you walk through it, flattens under ragdolled bodies, and is disturbed by tool swings |
| **Item collision physics** | Tool swings disturb grass/debris based on tool weight: pickaxe=heavy rock impact, scythe=wide light sweep, sword=lateral knock. Rock (heavy, round) rolls differently from stick (light, tumbles) |
| **Wind detection** | Reads game weather, season, and internal wind value. Boosts hair AND grass physics on windy/rainy/stormy days |
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
| [Fashion Sense](https://www.nexusmods.com/stardewvalley/mods/9969) | Optional — hair physics automatically apply to FS custom hairs |

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
  // ── System toggles ──────────────────────────────────────────────────────────
  "EnableBodyPhysics":          true,
  "EnableHairPhysics":          true,
  "EnableRagdollKnockback":     true,
  "EnableIdleMotion":           true,
  "EnableMonsterBodyPhysics":   true,   // archetype physics for ALL monsters
  "EnableMonsterRagdoll":       true,   // ragdoll knockback for all monsters
  "EnableNpcSwordKnockdown":    true,   // harmless cosmetic knockback near NPCs/pets
  "EnableFarmAnimalPhysics":    true,   // jiggle + tool collision for farm animals
  "EnableEnvironmentalPhysics": true,   // grass bend, ragdoll flattening
  "EnableItemCollisionPhysics": true,   // tool swings disturb grass by tool weight
  "EnableWindDetection":        true,   // boost hair+grass on windy/rainy/snowy days

  // ── Preset (overrides all individual strengths) ──────────────────────────────
  "Preset": "Default",               // "Soft" | "Default" | "High"

  // ── Feminine body strengths (0 = off, 2 = maximum) ──────────────────────────
  "FemaleBreastStrength": 0.55,
  "FemaleButtStrength":   0.50,
  "FemaleThighStrength":  0.40,
  "FemaleBellyStrength":  0.30,

  // ── Masculine body strengths ─────────────────────────────────────────────────
  "MaleButtStrength":     0.45,
  "MaleGroinStrength":    0.45,
  "MaleThighStrength":    0.35,
  "MaleBellyStrength":    0.25,

  // ── HDT Hair ─────────────────────────────────────────────────────────────────
  // Applies to ALL hair types: vanilla, mod-added, and Fashion Sense custom hairs.
  // Rain adds downward droop; snow adds light flutter; wind causes flow/trailing.
  "HairStrength":          0.55,
  "HairWindBoostOutdoors": 1.00,  // multiplier outdoors (higher = longer trail)
  "HairDampeningIndoors":  0.45,  // multiplier indoors (lower = calmer)

  // ── Ragdoll & knockback ──────────────────────────────────────────────────────
  "RagdollChanceUnderLowHealth": 0.50,  // 0 = never, 1 = always (at ≤30 HP)
  "RagdollKnockbackStrength":    1.50,  // push distance (1.5 = default, 4 = extreme)
  "NpcSwordKnockdownChance":     0.40,  // 0 = never, 1 = always (cosmetic only)

  // ── Monster physics ──────────────────────────────────────────────────────────
  // Archetype auto-detected from name: slime/jello/bat/worm/bug/furry/skeleton/generic
  "MonsterArchetypeStrength": 0.55,

  // ── Farm animal physics ──────────────────────────────────────────────────────
  // All vanilla + mod-added farm animals. Heavy (cow/pig) vs light (chicken/rabbit)
  "FarmAnimalPhysicsStrength": 0.45,

  // ── Environmental physics ────────────────────────────────────────────────────
  "EnvironmentalPhysicsStrength": 0.50, // grass/debris intensity (0 = off, 2 = max)

  // ── Manual gender overrides ──────────────────────────────────────────────────
  // Highest priority — overrides all auto-detection including live sprite names.
  // Values: "Feminine" | "Masculine" | "Androgynous"
  "GenderOverrides": {
    "Krobus": "Feminine"
  }
}
```

Changes take effect when you load a save.

---

## Compatibility

The mod detects sprite mods by reading live texture asset names at runtime. Built-in keyword rules:

| Mod category | Keywords detected | Profile |
|---|---|---|
| LewdEW | `lewdew`, `lewd_ew` | Feminine |
| Valley Girls | `valleygirls`, `valley_girls` | Feminine |
| XTardew Valley | `xtardew` | Feminine |
| HornyFur / Furry | `hornyfur`, `horny_fur`, `furfur` | Feminine |
| Anthro packs | `anthro` | Androgynous |
| Beast girl monsters | `beastgirl`, `beast_girl` | Feminine |
| Slime girl monsters | `slimegirl`, `slime_girl` | Feminine |
| Funtari slimes | `funtarislime`, `funtari_slime` | Feminine |
| Monster girl packs | `monstergirl`, `monster_girl` | Feminine |
| Creatures and Cuties | `creaturescuties`, `creatures_cuties`, `creaturecute` | Androgynous |
| Pokémon mods | `pokemon`, `stardew_pokemon` | Androgynous |

Monster archetype rules are in `assets/monsterArchetypes.json` (name keyword → physics model):

| Archetype | Detected by name containing | Physics model |
|---|---|---|
| Slime | Slime, Jelly, Sludge, Blob, Ooze | Bouncy jello — high impulse, slow decay, random wobble |
| Bat | Bat, Ghost, Haunted, Skull, Wisp | Floppy wing flutter — lateral oscillation, fast snap-back |
| Worm | Serpent, Grub, Duggy, Larva, Crawler | Squishy stretch — Y-axis dominant compression/extension |
| FlyingBug | Fly, Bug, Moth, Butterfly, Bee | Wing/leg vibration — rapid micro-oscillation |
| Furry | Wolf, Bear, Fox, Yeti | Fur ripple — gentle wave, slow decay |
| Skeleton | Skeleton, Bone, Mummy, Undead | Snappy bone clatter — high impulse, very fast decay |
| Generic | Everything else | Standard physics |

### Known compatible mods

- Generic Mod Config Menu ✔ (optional in-game sliders)
- Content Patcher ✔
- **Fashion Sense** ✔ — custom FS hairs automatically receive hair physics
- LewdEW ✔ (auto-detected)
- Valley Girls ✔ (auto-detected)
- XTardew Valley ✔ (auto-detected)
- HornyFur ✔ (auto-detected)
- Beast girl / slime girl / funtari slime / monster girl mods ✔ (auto-detected)
- **Creatures and Cuties** ✔ (auto-detected keyword + generic monster archetype physics)
- **Pokémon mods** ✔ (auto-detected keywords)
- All vanilla NPC and farmer sprites ✔
- All vanilla farm animals + mod-added animals in Coop/Barn ✔
- Cats and dogs (pets) ✔ (included in NPC humanoid physics)
- Modded NPCs and humanoids ✔ (falls back to game gender data)
- Modded monsters (any creature marked IsMonster) ✔ (archetype auto-detected or Generic)

### No conflicts expected with

- Json Assets, Mail Framework Mod, SpaceCore, Expanded Storage
- Combat overhaul mods — physics is purely cosmetic, no gameplay stats changed
- Any texture replacer — profile detection reads the active texture name at runtime
- Wind mods — weather and season heuristics + internal wind field reflection
- Farm animal mods — any animals in Farm/AnimalHouse locations are enumerated

---

## Extending the profile list

Power users can edit `assets/spriteProfiles.json` to add custom texture keyword rules:

```json
{ "CharacterName": "", "SpriteTextureContains": "mymod_female_", "ProfileType": "Feminine" }
```

For NPC-name-based rules:
```json
{ "CharacterName": "MyModNpc", "SpriteTextureContains": "", "ProfileType": "Feminine" }
```

To add or change monster archetype detection, edit `assets/monsterArchetypes.json`:
```json
{ "NameContains": "MyCustomMonster", "Archetype": "Slime" }
```

Valid archetypes: `Generic`, `Slime`, `Bat`, `Worm`, `FlyingBug`, `Furry`, `Skeleton`.

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
