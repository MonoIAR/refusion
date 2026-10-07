# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project Overview

Refusion is a fork of [Lakatrazz/BONELAB-Fusion](https://github.com/Lakatrazz/BONELAB-Fusion) (LabFusion), a multiplayer mod for BONELAB built on MelonLoader. The fork replaced the Steam-based transport with a dedicated UDP relay server: the relay client (`DedicatedServerNetworkLayer`) is the only network layer on every platform, and all Steam networking code and dependencies have been removed.

- `origin` = `https://github.com/MonoIAR/refusion.git` (the fork)
- `upstream` = `https://github.com/Lakatrazz/BONELAB-Fusion.git` (original project)

## Solution Layout

Solution file: `LabFusion.sln` (4 projects)

| Project | Target | Purpose |
| --- | --- | --- |
| `LabFusion/` | net6.0 | Main mod (`MelonMod`). ~600 source files under `src/`. |
| `BonelabSupport/` | net6.0 | Dynamically loaded support module: Harmony patches, scene events, network messages, extenders for BONELAB content. References `LabFusion.csproj`; post-build copies its DLL to `LabFusion/SupportModules/`, which LabFusion embeds as a resource. |
| `LabFusionUpdater/` | net6.0 | `MelonPlugin` auto-updater that pulls LabFusion.dll from GitHub releases. |
| `RefusionRelay/` | net8.0 | Standalone console relay server (the fork's core new work). UDP port 28430, JSON packet protocol, version-gated admission, persistent server state JSON files, console commands. |

Other top-level directories:

- `LabFusion/FusionBundles/` — Unity project for AssetBundles/Pallets. Mostly excluded from compilation; only ~27 runtime scripts under `FusionBundles/Assets/FusionMarrow/Runtime/` are explicitly included. Do not reorganize without updating `LabFusion.csproj` includes/excludes.
- `LabFusion/dependencies/` — vendored source (LiteNetLib, GroovyCodecs) compiled directly into the mod.
- `docs/` — project docs. **Read `docs/dedicated-server-network-layer-audit.md` before any networking work**; it defines the architecture direction, identity/permission model, and new code style.
- `Staging/` — release packaging area (Thunderstore manifest, GitHub release assets).
- `.github/` — issue templates only. There is no CI.

## Build Requirements

1. **.NET SDK** — any recent SDK works for both net6.0 and net8.0 targets (verified with SDK 9.x).
2. **`BONELAB_DIR` environment variable** — must point to a BONELAB Steam install with MelonLoader installed and `MelonLoader/Il2CppAssemblies/` generated. Almost every game/Unity/Harmony DLL reference resolves through `$(BONELAB_DIR)\MelonLoader\net6\` and `$(BONELAB_DIR)\MelonLoader\Il2CppAssemblies\`. Without it, nothing builds.
3. NuGet packages: `LavaGang.MelonLoader 0.6.4`, `Il2CppInterop.* 1.4.5`, `Microsoft.CSharp`.

Build commands:

```sh
dotnet build LabFusion.sln -c Debug
dotnet build RefusionRelay/RefusionRelay.csproj -c Debug
```

There are no post-build copy steps for the main mod — deploying means manually copying `LabFusion.dll` (and `BonelabSupport.dll` via its own post-build) into the game's `Mods`/`Plugins` folders.

All four projects build clean (`BonelabSupport` relies on `InternalsVisibleTo` from LabFusion).

## Architecture Notes

- **Entry point:** `FusionMod : MelonMod` in `LabFusion/src/Mod.cs`; mod metadata in `LabFusion/src/AssemblyInfo.cs`.
- **Versioning:** `FusionVersion` in `src/Mod.cs` is the single source of truth; `AssemblyInfo.cs` derives `AssemblyVersion` from it. Change versions only there.
- **Network layer abstraction:** `src/Network/Layers/NetworkLayer.cs` is the transport contract. `NetworkLayerManager` owns the active layer; `NetworkLayerDeterminer` selects it by saved title. Built-in layers: `DedicatedServerNetworkLayer` (UDP relay client, `src/Network/Layers/DedicatedServer/`, used on all platforms including Quest) and an `EmptyNetworkLayer` fallback. Layers are discovered by reflection via `RegisterLayersFromAssembly()`.
- **Server lifecycle:** `InternalServerHelpers.OnStartServer()` / `OnDisconnect()` are the canonical hooks; the pseudo-host (first relay client, SmallID=0) must call them on Unity's main thread.
- **Modules system:** `src/SDK/Modules/Module.cs` + `ModuleManager` — modules are found by reflection and instantiated automatically; no manual registration.
- **Messages:** routed via `MessagePrefix` (route + channel + sender SmallID); handlers under `src/Network/Messages/`. `NativeMessageHandler` validates sender SmallID against the transport-provided identity — never trust client-supplied identity fields.
- **Identity model:** `PlatformID` (ulong) is used broadly (bans, permissions, voice). The fork adds a persistent client-generated UUID (`refusion-identity.json`) carried by the dedicated handshake. SmallID/ClientId are session-scoped routing values only — never persist or conflate them with identity.
- **Authority model:** host runtime authority (`SmallID=0`) is separate from administrator permissions. Management actions (kick, ban, map/gamemode change) require explicit admin checks or the server-authoritative vote system; `OWNER` status alone grants nothing.

## Code Style

Applies to **new** Refusion/relay code (defined in `docs/dedicated-server-network-layer-audit.md`); existing LabFusion code is not reformatted:

- Variable names start lowercase; local and private fields start with `_`.
- Methods use explicit action prefixes: `SetXxx` / `GetXxx`.
- Short abbreviations allowed (`Pel` for `Pelvis`).
- No comments unless requested or needed to prevent serious misunderstanding.
- Keep simple statements on one line.

Existing codebase conventions (keep consistent when touching old code):

- File-scoped namespaces matching directory layout (`LabFusion.Network.Layers`).
- 4-space indent; `using` groups ordered System / LabFusion / MelonLoader / Il2Cpp.
- Static manager classes (`XxxManager`) + static events (`OnXxxEvent`).
- `Nullable` is disabled; `AllowUnsafeBlocks` on; `#if DEBUG` guards debug-only code.

## Git Conventions

- Commit messages: short English imperative/past-tense phrases, no prefixes (e.g. "Fixed null usernames breaking menus").
- Some DLLs and `Staging/GitHub/` release assets are intentionally committed; do not "clean" them without asking.

## Testing

- No automated test suite. RefusionRelay can be run standalone for console/protocol checks.
- In-game flows (join, catch-up, scene load, voice) require two clients against BONELAB + MelonLoader.

## Hard Rules

1. Do not reintroduce Steam-based networking. The relay is the only transport; client Steam networking code, the Steamworks assembly reference, the vendored Facepunch.Steamworks source, the embedded `steam_api64.dll`, and the Steam-era master/trusted lists were removed deliberately (see the audit doc's Steam Removal notes).
2. Unity/game object APIs only on the Unity main thread; socket threads must queue work.
3. Never trust client-provided `PlatformID`, `SmallID`, route, or channel values at the relay boundary.
4. Read the migration checklist in `docs/dedicated-server-network-layer-audit.md` before changing transport, identity, permissions, or matchmaking code.
