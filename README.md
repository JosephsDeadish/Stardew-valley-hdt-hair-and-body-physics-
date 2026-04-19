# SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More

> **v0.4.0 — Zero hard requirements — plug and play.**  
> Drop the mod folder into `Stardew Valley/Mods/` and launch via SMAPI.  
> All options are in `config.json`, or use the optional in-game menu with  
> [Generic Mod Config Menu (GMCM)](https://www.nexusmods.com/stardewvalley/mods/5098).

---

## Requirements

| Dependency | Required? |
|---|---|
| [SMAPI 4.0.0+](https://smapi.io) | **Yes** — installs automatically via Stardrop |
| Stardew Valley 1.5.6 – 1.6.x | **Yes** — works on all versions in this range |
| [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) | Optional — adds in-game sliders |
| [Fashion Sense](https://www.nexusmods.com/stardewvalley/mods/9969) | Optional — auto-detected for FS custom hairs |

---

## Installation

### Option A — Stardrop Mod Manager (easiest)

1. Open **[Stardrop](https://github.com/Floogen/Stardrop)**.
2. Click **Add Mod** → select the downloaded `.zip`.
3. Enable the mod in your profile and click **Launch**.

Stardrop automatically handles SMAPI and places the mod in the correct folder.

### Option B — Mod Organizer 2 (MO2)

1. Press **Ctrl+M** (Install Mod) and select the `.zip`.
2. In the file-mapping dialog MO2 shows a tree; confirm  
   `SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More\` maps to `Mods\`.
3. Activate the mod in the left panel and launch Stardew Valley through MO2.

### Option C — Manual drag-and-drop

1. Extract the `.zip`.
2. You will see a folder named exactly:  
   **`SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More`**
3. Copy that **entire folder** (not just its contents) into your Mods directory:

   | Platform | Mods directory |
   |---|---|
   | Windows (Steam) | `C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\` |
   | Windows (GOG) | `C:\Games\Stardew Valley\Mods\` |
   | macOS (Steam) | `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Mods/` |
   | Linux (Steam) | `~/.local/share/Steam/steamapps/common/Stardew Valley/Mods/` |

4. The result should look like:

   ```
   Stardew Valley/
   └── Mods/
       └── SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More/
           ├── manifest.json           ← SMAPI reads this
           ├── StardewHdtPhysics.dll
           └── assets/
   ```

5. Launch Stardew Valley through SMAPI (`StardewModdingAPI.exe` / `StardewModdingAPI`).

---

## Features

| System | What it does |
|---|---|
| **Body physics** | Jiggle impulses for feminine (breast, butt, thigh, belly) and masculine (butt, groin, thigh, belly). Directional hit impulse from nearest attacker. |
| **HDT Hair physics** | Flowing/bouncy motion on ALL hair types — vanilla, mod-added, Fashion Sense. Reacts to movement, wind, rain (wet/droopy), snow (flutter), water (buoyant float). |
| **Clothing flow physics** | Auto-detects flowy (dress/robe/skirt/cape/gown) vs tight (tights/shorts/armor) fabrics. Flowy clothes billow, trail, and flutter; tight clothes cling with minimal lag. Wind, rain, water, and speed all affect cloth. No body clipping. |
| **Per-profile cycling idles** | Feminine: 8-step cycle (hip rolls, chest pop, sashay pair, curtsy, arm reach, weight shift). Masculine: 8-step cycle (chest puff, shoulder rolls, wide bounce, stomp, flex, stretch). NPCs cycle through a mixed set. Each character has a unique starting offset. |
| **Monster archetype idles** | Slime = pulsing compression, Bat = wing-fold twitch, Worm = peristaltic wave, Bug = constant wing-buzz, Furry = tail wag, Skeleton = bone rattle, Dragon = deep breathing + wing-flutter, Elemental = sinusoidal energy pulse, Generic = ambient sway. |
| **Farm animal idles** | Chicken/Duck = head peck, Rabbit = ear twitch, Cow/Goat/Sheep = tail swish + head bob, Pig = sniff-bob, Ostrich = neck sway. All idle when standing still. |
| **Run-step impulses** | Profile-specific step-rhythm body+hair+clothing impulse at every running stride so physics are visible even during movement. Feminine = hip-led; Masculine = heel-strike dominant. |
| **Ragdoll knockback** | Configurable chance (default 50 %) to receive extra knockback force at ≤ 30 HP. |
| **Monster archetype physics** | Slime = bouncy jello, Bat/Ghost = floppy wings, Serpent/Grub = squishy stretch, Fly/Bug = wing+leg vibration, Wolf/Bear = fur ripple, Skeleton = snappy bone clatter, Dragon = wingbeat+tail thrash, Elemental = magical energy fluctuation. |
| **Farm animal physics** | Body jiggle for all farm animals; tool/sword near them causes cosmetic startle — no damage. |
| **NPC sword knockdown** | Sword swings near NPCs/pets apply a harmless cosmetic knockback — no damage, no anger. |
| **Environmental physics** | Grass bends when you walk through it, flattens under ragdolled bodies, disturbed by tools. |
| **Warp-step impulse** | Body bounce + hair toss + clothing resettlement every time you step through a door or warp. |
| **Eating/drinking bounce** | Chin-dip body lean + hair swing when the farmer starts eating or drinking. |
| **Lightning flinch** | Sharp random-direction flinch + electric hair whip + clothing billow + screen flash on lightning strikes. |
| **Skill level-up bounce** | Joyful body bounce when any skill levels up. |
| **Weather mod integration** | Detects 12+ weather mods and boosts wind/rain/snow physics accordingly. |
| **Wind detection** | Reads `Game1.wind`, season, debris-weather flag, and weather mod boosts. Drives hair, grass, and clothing physics. |
| **Hitstop** | Brief visual freeze on significant hits for impact feedback. |
| **Horse rider physics** | Extra vertical bounce while riding a horse. |
| **Magic cast physics** | Body+hair impulse when using magic-type tools (SpaceCore/Magic mod detected). |
| **Auto profile detection** | Reads live sprite texture names to detect gender profile for any character including mod-added ones. |
| **Manual overrides** | Per-NPC gender override in `config.json` — `"GenderOverrides": { "Krobus": "Feminine" }`. |
| **Preset system** | Soft / Default / High / ExtraBouncy presets in GMCM or `config.json`. |

---

## Weather Mod Integration

The following mods are auto-detected at startup. When active they boost physics
intensity beyond vanilla weather levels:

| Mod | Effect |
|---|---|
| More Rain | Boosts rain strength on rainy days |
| Climate of Ferngill / Climate Control | Boosts rain strength (more extreme weather) |
| Wind Effects | Reads mod's wind strength value directly |
| Cloudy Skies | Detected; custom weather types respected |
| Stardew Valley Expanded | Thundersnow (snow+lightning) → max storm physics |
| Extreme Weather / Weathervane | Detected; storm flags amplified |
| Ridgeside Village | Detected; custom area weather supported |
| Vanilla debris weather | Wind-day flag → strong wind boost |
| Vanilla lightning storm | Max rain + wind intensity |
| Green rain (1.6+) | Magical downpour + gentle wind |
| Thundersnow (SVE) | Snow + max wind |

---

## Compatibility

### Sprite mods (auto-detected by texture name)

| Mod | Keywords | Profile |
|---|---|---|
| LewdEW | `lewdew`, `lewd_ew` | Feminine |
| Valley Girls | `valleygirls`, `valley_girls` | Feminine |
| XTardew Valley | `xtardew` | Feminine |
| HornyFur / Furry | `hornyfur`, `horny_fur`, `furfur` | Feminine |
| Anthro packs | `anthro` | Androgynous |
| Beast girl / slime girl / monster girl | keywords | Feminine |
| Funtari slimes | `funtarislime`, `funtari_slime` | Feminine |
| Creatures and Cuties | `creaturescuties`, `creaturecute` | Androgynous |
| Pokémon mods | `pokemon`, `stardew_pokemon` | Androgynous |
| Fashion Sense | detected + custom hairs get physics | — |
| Ridgeside Village NPCs | texture + named profiles | Androgynous |
| East Scarp NPCs | texture + named profiles | Mixed |
| Stardew Valley Expanded NPCs | texture + named profiles | Mixed |

### Known compatible mods

- Generic Mod Config Menu ✔
- Content Patcher ✔
- Fashion Sense ✔ (custom FS hairs automatically get hair physics)
- LewdEW, Valley Girls, XTardew Valley, HornyFur ✔ (auto-detected)
- All vanilla NPCs, farmer, pets ✔
- All vanilla farm animals + mod-added animals in Coop/Barn ✔
- Modded NPCs / humanoids ✔ (falls back to game gender field)
- Modded monsters (any `IsMonster`) ✔ (archetype auto-detected or Generic)
- All weather mods listed above ✔

---

## Building from Source

```powershell
# Windows
.\build-package.ps1
# ↑ auto-detects Steam install, builds, copies DLL, creates dist\SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More.zip
```

```bash
# Linux / macOS
./build-package.sh
# Same steps as above
```

Both scripts:
1. Find your Stardew Valley installation (or accept `--game-path` / `-GamePath`)
2. Build `src/StardewHdtPhysics/StardewHdtPhysics.csproj` with `dotnet build`
3. Copy the DLL into `mod/SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More/`
4. Create a ready-to-install zip in `dist/`

**Requirements for building:**
- [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)
- Stardew Valley installed (provides game DLLs for compilation)

---

## File Structure

```
Repository
├── mod/
│   └── SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More/    ← drag this folder into Mods/
│       ├── manifest.json
│       ├── StardewHdtPhysics.dll                  ← added by build-package script
│       ├── README.txt
│       └── assets/
│           ├── presets.json
│           ├── spriteProfiles.json
│           ├── monsterArchetypes.json
│           └── debrisPhysics.json
│
├── src/
│   └── StardewHdtPhysics/                         ← C# source code
│       ├── StardewHdtPhysics.csproj
│       ├── manifest.json
│       ├── ModEntry.cs
│       ├── ModConfig.cs
│       ├── BodyProfile.cs
│       ├── SpriteProfileDetector.cs
│       ├── PhysicsPreset.cs
│       ├── GenericModConfigMenuApi.cs
│       └── assets/  (same files as mod/assets/)
│
├── build-package.ps1                              ← Windows build + package
├── build-package.sh                               ← Linux/macOS build + package
└── .github/workflows/validate.yml                ← CI: validates JSON + folder structure
```

---

## Configuration

Edit `Mods/SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More/config.json` after the first launch:

```jsonc
{
  // ── System toggles ────────────────────────────────────────────────────────
  "EnableBodyPhysics":           true,
  "EnableHairPhysics":           true,
  "EnableRagdollKnockback":      true,
  "EnableIdleMotion":            true,
  "EnableMonsterBodyPhysics":    true,
  "EnableMonsterRagdoll":        true,
  "EnableFarmAnimalPhysics":     true,
  "EnableEnvironmentalPhysics":  true,
  "EnableWindDetection":         true,
  "EnableClothingFlowPhysics":   true,

  // ── Preset (overrides all individual strengths) ───────────────────────────
  "Preset": "Default",   // "Soft" | "Default" | "High" | "ExtraBouncy"

  // ── Feminine body strengths (0 = off, 2 = maximum) ───────────────────────
  "FemaleBreastStrength": 0.75,
  "FemaleButtStrength":   0.50,
  "FemaleThighStrength":  0.40,
  "FemaleBellyStrength":  0.30,

  // ── Masculine body strengths ──────────────────────────────────────────────
  "MaleButtStrength":     0.45,
  "MaleGroinStrength":    0.45,
  "MaleThighStrength":    0.35,
  "MaleBellyStrength":    0.25,

  // ── Hair ─────────────────────────────────────────────────────────────────
  "HairStrength":          0.55,
  "HairWindBoostOutdoors": 1.00,
  "HairDampeningIndoors":  0.45,

  // ── Clothing flow ─────────────────────────────────────────────────────────
  "ClothingFlowStrength":  1.00,
  "IdleMotionStrength":    1.00,

  // ── Manual gender overrides (highest priority) ────────────────────────────
  "GenderOverrides": {
    "Krobus": "Feminine"
  }
}
```

Changes take effect when you load a save.

---

## Source & Issues

<https://github.com/JosephsDeadish/Stardew-valley-hdt-hair-and-body-physics->

