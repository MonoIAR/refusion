# Dedicated Server Network Layer Audit

Audit date: 2026-10-05

Scope: source inspection and initial UDP relay implementation in the Refusion fork at `E:\GithubProjects\refusion`.
The initial relay project builds successfully; client integration is not implemented yet.

## New Code Style

These rules apply to all new Refusion and dedicated-relay code from this point forward. Existing LabFusion code is not automatically reformatted.

- Variable names start with a lowercase letter.
- Local and private variables start with `_`.
- Method names use explicit action prefixes: `SetXxxxxx` for setters and `GetXxxxxx` for getters.
- Short, clear abbreviations are allowed, such as `Pel` for `Pelvis`.
- Do not add comments unless they are explicitly requested or required to prevent a serious misunderstanding.
- Keep code on one line when it remains readable; avoid splitting simple statements across multiple lines.
- Keep naming consistent across client, relay, protocol, and configuration code.

## Confirmed Direction

- Convert the existing Steam transport layer in place into a dedicated-server UDP layer; do not keep Steam as a parallel gameplay transport.
- Preserve Fusion's message handlers, player/entity synchronization, and server lifecycle where their contracts remain transport-independent.
- Replace Steam login, Steam Relay, Steam lobby/matchmaking, Steam identity assumptions, and RoomCode joining with the dedicated-server connection flow.
- The client and relay three-part versions must match exactly before admission. Keep a separate protocol version so wire-format compatibility is not conflated with product version.
- Refusion uses a persistent client-generated UUID as the player's identity for bans, operators, permissions, and reconnect recognition. The first version does not add a separate application-level SessionId.
- The relay uses its connection object and a monotonically increasing ClientId for current transport routing. ClientId and SmallID are session-scoped routing values and must never be persisted as player identity.
- Deleting the local identity file creates a new UUID and is an accepted first-version limitation. The system is intended to deter ordinary abuse, not provide tamper-proof identity.
- The client identity file is `refusion-identity.json` under Fusion persistent data. It stores a normalized lowercase UUID string.
- Fusion's existing `PlatformID` fields remain a compatibility boundary until the dedicated transport is integrated. The persistent UUID must be carried by the dedicated handshake and must not be truncated into a `ulong` as the authoritative identity.
- The first player is the simulated host/server operator; host status and administrator permission are separate concepts.
- `OWNER`/pseudo-host has no extra gameplay or management privileges by default. Unless that player is also an administrator, they must have the same permissions as an ordinary player for level changes, gamemode changes, server settings, kicks, bans, and similar controls. The pseudo-host only performs the client-side server runtime, networking, and required authoritative logic.
- A later voting system may expose level/gamemode controls to every player. Clicking such a control should create a server-authoritative vote rather than directly execute the operation; the default rule is majority approval, with quorum, timeout, tie, disconnect, and administrator override rules defined separately.
- The dedicated server should also provide a join MOTD and server command/chat support modeled on the old `LabFusionServer`, especially `/say`.

## Existing Layer Lifecycle

`NetworkLayer` is the transport contract. `NetworkLayerManager` logs a layer in, receives its logged-in event, installs it as the active layer, calls `OnInitializeLayer()`, and forwards update/late-update/user-join callbacks through `InternalLayerHelpers`.

When a transport starts the server, it calls `InternalServerHelpers.OnStartServer()`. Disconnect cleanup is similarly centralized in `InternalServerHelpers.OnDisconnect()`. These are important reuse points for the replacement layer.

The Steam implementation currently combines that reusable lifecycle with transport-specific behavior:

- Reusable layer responsibilities: `IsHost`/`IsClient`, Fusion server start/disconnect hooks, message send/broadcast entry points, voice manager lifecycle, player join/leave hooks, and `NetworkChannel` reliable/unreliable intent.
- Steam-specific responsibilities: Steam API initialization/shutdown and callbacks; Steam identity and friend/name lookup; Relay socket/connection creation; Steam connection-to-user routing; Steam lobby creation and metadata; Steam Matchmaking; and RoomCode generation/search.
- `SteamNetworkLayer.OnUpdateLayer()` runs Steam callbacks and receives socket/connection messages from Fusion's regular update callback. A new UDP implementation must explicitly define background socket IO versus main-thread dispatch; Unity/game objects must remain on the Unity thread.

Relevant files:

- `LabFusion/src/Network/Layers/NetworkLayer.cs`
- `LabFusion/src/Network/Layers/NetworkLayerManager.cs`
- `LabFusion/src/Network/Internal/InternalLayerHelpers.cs`
- `LabFusion/src/Network/Internal/InternalServerHelpers.cs`
- `LabFusion/src/Network/Layers/Steam/SteamNetworkLayer.cs`
- `LabFusion/src/Network/Layers/Steam/SteamSocketHandler.cs`
- `LabFusion/src/Network/Layers/Steam/SteamSocketManager.cs`
- `LabFusion/src/Network/Layers/Steam/SteamConnectionManager.cs`

## Registration And Platform Selection

Layers are discovered from the assembly by `NetworkLayer.RegisterLayersFromAssembly()`. `NetworkLayerDeterminer.GetDefaultLayer()` currently chooses `SteamVRNetworkLayer` on non-Android and `ProxySteamVRNetworkLayer` on Android. The setting stores a layer title, and invalid/unsupported layers fall back through `VerifyLayer()`.

Therefore, converting the desktop class alone is insufficient. The registered title/default selection and platform routing must be intentionally changed. The Android Proxy is a distinct path and currently depends on Steam identity; whether it remains supported in the dedicated-server design is unresolved.

Relevant files:

- `LabFusion/src/Network/Layers/NetworkLayerDeterminer.cs`
- `LabFusion/src/Network/Layers/Steam/SteamVRNetworkLayer.cs`
- `LabFusion/src/Network/Layers/Proxy/ProxySteamVRNetworkLayer.cs`
- `LabFusion/src/Menu/Pages/MenuLogIn.cs`
- `LabFusion/src/Preferences/Client/ClientSettings.cs` (configured layer title; inspect/update when implementation begins)

## Steam And RoomCode Consumers

Steam-backed lobby browsing is exposed through the generic `IMatchmaker` and `INetworkLobby` abstractions, but the current concrete join delegates cast the active layer back to `SteamNetworkLayer` or `ProxyNetworkLayer`. `MenuMatchmaking`, the sandbox browser, and the gamemode browser request lobby lists through `NetworkLayerManager.Layer.Matchmaker`.

RoomCode is not isolated to the Steam transport:

- `NetworkLayer` exposes `GetServerCode()`, `RefreshServerCode()`, and `JoinServerByCode()`.
- `NetworkHelper` forwards those calls.
- `MenuMatchmaking` accepts a code and invokes the join helper.
- `MenuLocation` displays/refreshes a host code in two UI configurations.
- Both Steam and Proxy layers generate a random 8-character code and resolve it through their matchmaker.

If IP:port is the connection address and RoomCode is being removed, these abstract APIs and UI entry points need a deliberate replacement/removal, not just deletion of Steam's implementation.

Relevant files:

- `LabFusion/src/Network/Lobbies/IMatchmaker.cs`
- `LabFusion/src/Network/Lobbies/INetworkLobby.cs`
- `LabFusion/src/Network/Layers/Steam/SteamLobby.cs`
- `LabFusion/src/Network/Layers/Steam/SteamMatchmaker.cs`
- `LabFusion/src/Network/Layers/Proxy/ProxyNetworkLobby.cs`
- `LabFusion/src/Network/Layers/Proxy/ProxyMatchmaker.cs`
- `LabFusion/src/Network/Helpers/NetworkHelper.cs`
- `LabFusion/src/Menu/Matchmaking/MenuMatchmaking.cs`
- `LabFusion/src/Menu/Matchmaking/MenuMatchmakingSandbox.cs`
- `LabFusion/src/Menu/Matchmaking/MenuMatchmakingGamemodes.cs`
- `LabFusion/src/Menu/Pages/MenuLocation.cs`

## Identity, Host Authority, And Permissions

`SteamNetworkLayer` initializes the local `PlayerIDManager` long ID from `SteamClient.SteamId`. `ProxyNetworkLayer` also receives a SteamID from its helper and requests the Steam username. It even uses Steam lobby IDs for joining proxy-hosted sessions. Replacing only the desktop Steam layer would leave these assumptions active on the Proxy path.

Fusion uses the generic-looking `PlatformID`/`ulong` as a key well beyond transport routing: player lookup, message sender validation, bans/global bans, trusted-player lists, permission lookup, voice speaker matching, metadata, and some gamemode state. The connection request contains a backup platform ID, while received messages may carry a platform ID supplied by the transport. The new authenticated identity must be established at the relay/connection boundary and then consistently mapped into this existing contract or replace it across consumers.

Host authority is currently coupled to `PlayerIDManager.HostSmallID == 0`; for example, `NetworkVerification.HasAuthorityOverPlayer()` grants authority to that player. This host/network authority must not be conflated with the administrator permission level or trusted admin role. A simulated host migration must preserve or deliberately transfer the network authority identity.

The target permission model is:

- `OWNER`/pseudo-host: runs the local server runtime, networking, and required authoritative simulation; no management privileges by virtue of ownership.
- `ADMIN`: receives management permissions according to the server's administrator system.
- `OWNER + ADMIN`: has both runtime authority and administrator permissions.
- Ordinary player: has neither pseudo-host runtime responsibility nor administrator permissions.

Every management action must be authorized by an explicit administrator/permission check, never by `NetworkInfo.IsHost`, `PlayerID.IsHost`, `HostSmallID`, or transport connection ownership alone. Server-side handling must repeat this check so disabling a UI control cannot be the only protection.

## Planned Voting System

The intended replacement for owner-only level/gamemode controls is a server-authoritative vote:

- Controls are visible and usable by all connected players.
- A player submits a requested action and its parameters, such as a target level or gamemode.
- The authoritative runtime validates the request, creates a unique vote, and broadcasts its state.
- Each eligible player votes once. The server counts votes and broadcasts the result.
- A majority approval executes the requested action exactly once. Clients must not directly change level, gamemode, or settings from the button callback.
- The UI is only an input/presentation layer; validation and execution remain server-side.

The vote protocol will need request, vote-cast, vote-state, vote-result, and vote-cancel messages, with request IDs, action parameters, duplicate-submission protection, and transition cleanup.

Rules still to decide:

- Whether majority means more than half of all connected players or more than half of votes cast.
- Minimum quorum and whether the proposer counts automatically.
- Vote duration and join/leave behavior during a vote.
- Tie, abstention, cooldown, and proposer-disconnect behavior.
- Which settings are votable and which remain administrator-only.
- Whether an administrator can approve, cancel, or bypass a vote.

## MOTD And Server Chat

The old `LabFusionServer` behavior was verified from the legacy source and server log:

- The relay stores a configurable `Motd` value in server configuration.
- When a player connects, the server sends the MOTD as a server notification. The old client delays displaying it until the player has completed room entry, then shows it with title `Server`.
- `/motd <text|none>` changes or clears the configured MOTD.
- `/say <text>` broadcasts a server chat/notice to everyone.
- `/say <playerName> <text>` sends a private whisper to one player.
- The old relay had a dedicated `Chat` relay message type, separate from raw Fusion forwarding.

Refusion currently has `Notifier` and `NetworkNotifications`, but no general text-chat message/UI or command parser. `Notifier` can display an MOTD/notice locally, but it is not a replacement for a persistent chat channel. The new design should therefore separate:

- MOTD: server-owned persisted setting, delivered once during successful join/room entry, with an optional admin/console update command.
- Public chat: authenticated sender ID and display name, message text, server timestamp/order, reliable broadcast.
- Private whisper: server-routed message to exactly one target, with target lookup by stable ID and name resolution performed server-side.
- Console commands: parsed by the relay/server console; permission-checked commands must never trust a client-provided admin flag.

The client will need a chat presentation and text-input path. The source audit has not found an existing text-chat UI in Refusion; the exact UI location and whether player-entered slash commands are supported remain open decisions. Server console commands can be implemented independently of the client UI.

Suggested initial command scope:

- `/help`
- `/motd <text|none>`
- `/say <text>`
- `/say <player> <text>` or a separate `/msg <player> <text>`
- `/list`
- `/status`

Administrative commands such as level change, gamemode change, kick, ban, and server settings must follow the administrator/vote rules already recorded. `OWNER` status alone must not authorize them.

## Legacy Command Inventory

The old `LabFusionServer` help output was checked directly. It exposed 35 commands:

`/?`, `/ban`, `/banlist`, `/clear`, `/code`, `/damage`, `/deop`, `/gamemode`, `/gamemodes`, `/god`, `/heal`, `/help`, `/kick`, `/list`, `/load`, `/map`, `/maphistory`, `/maxplayers`, `/motd`, `/op`, `/ops`, `/password`, `/ping`, `/reload`, `/restart`, `/save`, `/say`, `/snapshot`, `/status`, `/stop`, `/time`, `/tp`, `/unban`, `/version`, `/votecancel`, `/whitelist`.

The old descriptions and the intended Refusion disposition are:

| Command | Old purpose | Refusion direction |
| --- | --- | --- |
| `/?`, `/help` | Show commands | Keep; filter output by caller permissions |
| `/list` | List online players | Keep for players |
| `/status` | Show server status | Keep for players; omit sensitive data |
| `/version` | Show server version | Keep for players |
| `/gamemodes` | List gamemodes | Keep for players |
| `/maphistory` | Show recent maps | Keep if map history remains |
| `/ping <player>` | Show latency | Keep for players, with rate limit |
| `/say <text>` | Public broadcast | Keep; server validates and broadcasts |
| `/say <player> <text>` | Private whisper | Keep, or add `/msg` alias; resolve target server-side |
| `/motd <text\|none>` | Set join message | Administrator only; persist on relay |
| `/op <id\|name>` | Grant admin | Administrator/console policy required; never OWNER-only by default |
| `/deop <id\|name>` | Revoke admin | Administrator/console policy required; protect highest operator account |
| `/ops` | List admins | Keep with permission-aware output |
| `/kick <id\|name>` | Disconnect player | Administrator only |
| `/ban <id\|name> [reason]` | Ban player | Administrator only; server-side identity required |
| `/unban <id\|name>` | Remove ban | Administrator/console only |
| `/banlist` | List bans | Administrator/console only |
| `/whitelist on\|off\|add\|remove\|list` | Manage whitelist | Administrator/console only |
| `/map <barcode> [loadingBarcode]` | Change level | Vote by default; administrator override may be allowed |
| `/gamemode <name\|barcode\|none>` | Change gamemode | Vote by default; administrator override may be allowed |
| `/votecancel` | Cancel active vote | Administrator only, or a separately defined vote moderator role |
| `/clear` | Remove all spawned props | Administrator only or vote, because it changes all clients |
| `/time <scale>` | Change global time scale | Administrator only unless explicitly made votable |
| `/maxplayers <n>` | Change player limit | Administrator/console only |
| `/password <value\|none>` | Set server password | Administrator/console only |
| `/damage <player> <amount>` | Damage player | Administrator-only debug command |
| `/heal <player>` | Heal player | Administrator-only debug command |
| `/god <player>` | Toggle god mode | Administrator-only debug command |
| `/tp <playerA> <playerB>` | Teleport player | Administrator-only and governed by teleport permission |
| `/save` | Save world snapshot | Console/admin only if snapshots remain |
| `/load` | Restore world snapshot | Console/admin only; should require confirmation |
| `/snapshot on\|off\|save\|load\|info` | Manage snapshots | Console/admin only |
| `/reload` | Reload server configuration | Console only initially; admin exposure requires careful live-setting rules |
| `/restart` | Restart relay process | Console only |
| `/stop` | Shut down relay process | Console only |
| `/code [new]` | Show/regenerate RoomCode | Remove when IP:port is the join mechanism |

The old list contains overlapping aliases (`/?` and `/help`) and an overloaded `/say` syntax. The new command system should use a central registry with command name, aliases, usage, permission requirement, rate limit, and execution side-effect classification. The registry must be shared by console help and client help, while sensitive command details are hidden from unauthorized players.

`/op` and `/deop` require special treatment in the pseudo-host model:

- Being `OWNER` must not implicitly grant `/op`, `/deop`, or any other administrator command.
- The initial operator source must be defined independently, for example a relay configuration file, authenticated account, or console-only bootstrap.
- An administrator must not be able to silently promote arbitrary users without server-side authorization and audit logging.
- The last protected operator should not be removable without an explicit recovery path.
- Every promotion/demotion must be persisted by the relay and broadcast as an authoritative permission update.

## Loaded Melon Mod Policy

The Refusion source already references MelonLoader's registered plugin collection in `FusionMod.OnLateInitializeMelon()`:

- `MelonPlugin.RegisteredMelons` is used to detect the Fusion Updater plugin.
- MelonLoader also exposes registered mod instances through `MelonMod.RegisteredMelons`.
- Each loaded entry exposes `Info` metadata and a backing `MelonAssembly.Assembly`, so the client can collect mod/plugin name, version, author, assembly identity, and assembly location when available.

MelonLoader does not provide one universal authoritative hash for every mod. A practical fingerprint is a cryptographic digest of the loaded assembly bytes, preferably SHA-256. Hashing original file bytes is more stable than hashing `Assembly.FullName`; names and versions are metadata and may collide or be changed. Assemblies loaded from memory or without a usable file location need an explicit `unresolved` state rather than silently being treated as safe.

Proposed server policy:

1. During connection admission, the client sends a mod manifest containing each loaded MelonMod/MelonPlugin's normalized name, version, assembly identity, and SHA-256 where available.
2. The relay compares the manifest against a server-maintained denylist. Entries may match exact hash, publisher/name/version, or a policy category.
3. A match produces a specific denial reason and the client is rejected before Fusion player state is created.
4. The relay may maintain an allowlist for competitive modes, but the default should be denylist plus required-mod/version checks to avoid breaking unrelated community mods.
5. Manifest collection needs an explicit policy for Refusion, the updater, BoneLib, dependencies, and server-required gamemode modules.

Security limitation: a Melon mod running inside the client can potentially intercept or falsify its own manifest, hide assemblies, or alter the hash routine. This is a compatibility/policy filter, not a complete anti-cheat boundary. The relay should still validate format, size, duplicates, rate, and handshake timing, and record the reported manifest for audit.

This feature needs a dedicated handshake payload and policy version. Do not put the full manifest into ordinary gameplay messages or evaluate it every frame. Cache it per connection, recheck on reconnect or explicit policy refresh, and never load or execute received assemblies during validation.

## Relay Mod Policy Settings

The relay should expose configurable Mod-policy settings instead of one hard-coded blacklist. The project has two different policy domains:

- MelonLoader runtime code: `MelonMod` and `MelonPlugin`.
- Officially supported BONELAB/Fusion content Mods: loaded pallets/assets recognized by the AssetWarehouse and ModIO/manifest system.

These must not be confused with `LabFusion.SDK.Modules.Module`, which is Fusion's internal code-module API and is not what is meant by an SDK content Mod.

### MelonLoader policy

- `MelonModBlacklist`: deny exact Mod assembly SHA-256 values.
- `MelonPluginBlacklist`: deny exact Plugin assembly SHA-256 values.
- `MelonNameBlacklist`: deny normalized Mod/Plugin names or publisher/name pairs.
- `MelonVersionBlacklist`: deny specific name/version combinations.
- `MelonBlacklistMode`: `Disabled`, `ReportOnly`, or `Reject`.
- `RequireManifest`: reject clients that cannot provide a complete, parseable manifest.

### Official SDK content-Mod policy

Refusion identifies supported content Mods through their official content Barcodes. For this policy, the relay does not need ModIO IDs, file hashes, pallet hashes, or assembly identity. The existing LabFusion SDK Mod blacklist behavior is the target behavior and must be preserved as-is.

`Author.PalletName.ItemType.ItemName`

The number of supplied segments determines the match scope:

- `Author` — all pallets and all item types/items from this author.
- `Author.PalletName` — the specified pallet from that author.
- `Author.PalletName.ItemType` — all items of that type in that pallet.
- `Author.PalletName.ItemType.ItemName` — one exact content item.

Refusion already has a content Barcode blacklist implementation that should be reused and extended:

- `LabFusion/src/Data/Files/ModBlacklist.cs` loads the local `mod_blacklist.txt`.
- `LabFusion/src/Safety/Lists/GlobalModBlacklistManager.cs` loads `globalModBlacklist.json`.
- `SpawnRequestMessage` blocks blacklisted server spawn requests.
- `SpawnResponseMessage` blocks blacklisted client-side creation.
- `PlayerRepAvatarMessage` replaces a blacklisted Avatar with the calibration Avatar.
- `ModIODownloader` checks blacklisted ModIO name IDs and numeric ModIO IDs before downloading.

The current implementation is considered correct for the project and is the reference behavior for the new relay. Do not replace it with a new generic prefix matcher or reinterpret the four Barcode scopes independently. The relay should reuse the same blacklist semantics and data format, adding only server-owned configuration/synchronization and connection-admission enforcement where needed. Denied Barcodes must remain blocked before spawn/avatar application, and denied content downloads must remain blocked locally.

The relay policy can therefore include:

- `SdkBarcodeWhitelist`: allowed Barcode entries using the existing LabFusion semantics.
- `SdkBarcodeBlacklist`: denied Barcode entries using the existing LabFusion semantics.
- `RequiredSdkBarcodes`: content Barcode entries required for a server, map, gamemode, or competitive mode.
- `SdkBarcodePolicyMode`: `Disabled`, `ReportOnly`, `Whitelist`, or `Blacklist`.

Policy evaluation should call the same centralized LabFusion blacklist logic used by local content handling. A Barcode identifies supported content, but the blacklist remains a content-policy mechanism rather than proof that a client has unmodified files.

`LabFusion.SDK.Modules.Module` and registered gamemode classes are separate code/runtime registrations. They can have their own compatibility checks, but they should not be called SDK content Mods in the relay configuration.

### Policy precedence

1. Malformed or unverifiable required manifest: reject when `RequireManifest` is enabled.
2. Exact hash denylist: reject.
3. Explicit name/publisher/version denylist: reject.
4. Explicit allowlist exception: override a broad name rule only when the exact hash is trusted and configuration permits exceptions.
5. Required SDK module or gamemode missing: reject when the relevant whitelist/required policy is enabled.
6. Otherwise accept and record the manifest.

The client must not choose its own policy mode. The relay owns the policy, sends the active policy version in the handshake response, and logs the matched rule and denial reason. Policy updates apply to new connections and may optionally trigger controlled revalidation; they should not scan or eject players every frame.

### Suggested relay configuration shape

```json
{
  "ModPolicy": {
    "RequireManifest": true,
    "MelonBlacklistMode": "Reject",
    "MelonModBlacklist": [],
    "MelonPluginBlacklist": [],
    "MelonNameBlacklist": [],
    "MelonVersionBlacklist": [],
    "SdkBarcodePolicyMode": "Whitelist",
    "SdkBarcodeWhitelist": [],
    "SdkBarcodeBlacklist": [],
    "RequiredSdkBarcodes": [],
    "GamemodeWhitelist": [],
    "GamemodeBlacklist": [],
    "AllowTrustedHashExceptions": false,
    "PolicyVersion": 1
  }
}
```

Configuration changes can later be exposed through commands such as `/modpolicy`, `/modblacklist`, `/modwhitelist`, `/sdkmodule`, and `/gamemodeallow`, with administrator/console authorization and audit logging. `OWNER` status alone must not modify these policies.

## Additional Commands Supported By Refusion Architecture

The current Refusion source exposes capabilities beyond the legacy command list. These are architectural capabilities, not proof that a command already exists:

### Player-facing or vote-capable

- `/requestmap <barcode|name>`: submit a level-change request. Refusion already has `LevelRequestMessage`, a 10-second request cooldown, and a host-side approval notification.
- `/requestgamemode <name|barcode|none>`: submit a gamemode request. Gamemode selection/deselection already propagates through gamemode metadata and the manager.
- `/vote <yes|no>` and `/vote status`: proposed interface for the server-authoritative vote protocol.
- `/slowmo`: toggle/decrease time scale where the server's `TimeScaleMode` allows it. Existing modes are disabled, low-gravity, host-only, everyone, and client-side; permissions must be re-authorized for the new OWNER/admin model.
- `/players` as an alias of `/list`.
- `/where <player>` or `/position <player>`: possible diagnostic command if position data is exposed by the authoritative representation. Exact positions should not be exposed by default in public servers.

### Administrator or moderator

- `/set <setting> <value>`: change a server setting represented by `SavedServerSettings`/`LobbyInfo,` including name tags, voice chat, player constraining, mortality, friendly fire, knockout, knockout length, maximum avatar height, maximum players, privacy, and slow-motion mode.
- `/permission <player> <level>`: generalized replacement for `/op` and `/deop`, based on `GUEST`, `DEFAULT`, and `OPERATOR`, with the current automatic `OWNER` privilege removed.
- `/despawn <entityId>`: remove one network entity. The transport message exists, but sender permission and entity ownership checks must be added before exposing this command.
- `/clear`: clear spawned entities. This needs an authoritative enumeration/clear operation; existing despawn requests are per entity.
- `/spawn <barcode> [position] [rotation]`: use the existing network asset-spawn path, with blacklist, single-player-only tag, crate availability, scene, and rate-limit validation.
- `/avatar <player> <barcode>`: only after a server-authoritative avatar policy is implemented; current avatar replication is not an admin command.
- `/freeze <player>`, `/mute <player>`, `/spectate <player>`: require new authoritative state/protocol; they are not currently generic Refusion commands.
- `/modules`: list registered Fusion modules and gamemode modules.
- `/mods` or `/modinfo`: possible compatibility diagnostics using existing mod-info request/response messages.
- `/entities`: admin/console diagnostic entity counts or ownership list; avoid unbounded enumeration in normal gameplay.
- `/resync <player>`: possible future operation using catchup/entity-data/dynamics assignment pieces, but there is no generic resync command today.
- `/reloadsettings`: only after the relay has a defined persistent authoritative settings store. Melon preferences are local storage, not a dedicated-server configuration API.

### Do not expose as arbitrary commands

- Refusion has typed RPC messages, but `/rpc <method>` must not invoke arbitrary module methods. Only explicitly registered, authenticated, allowlisted operations may be exposed.
- Gamemode metadata APIs are module APIs; do not allow arbitrary client-supplied metadata keys as a general server command.
- The current `DespawnRequestMessage` forwards a response without an explicit administrator check. Treating it as a safe administrative command requires new authorization.
- `SpawnRequestMessage` validates blacklists and single-player-only tags, but command permission and rate limiting remain separate requirements.
- `PermissionCommandRequestMessage` currently handles kick, ban, and teleport requests only. It does not implement `/op`, `/deop`, settings changes, or vote execution.
- `FusionPermissions` currently automatically marks the local network host as `OWNER`, which conflicts with the target pseudo-host model and must be redesigned before adding permission commands.

Friend-only privacy also depends on `NetworkLayer.IsFriend()`. Steam uses the Steam friends list; Proxy receives friend IDs from its helper. This policy needs an explicit dedicated-server definition if Steam identity/friends are removed.

Relevant files:

- `LabFusion/src/Player/PlayerIDManager.cs`
- `LabFusion/src/Player/PlayerID.cs`
- `LabFusion/src/Network/Helpers/NetworkVerification.cs`
- `LabFusion/src/Network/Messages/Server/ConnectionRequestMessage.cs`
- `LabFusion/src/Network/Messages/NativeMessageHandler.cs`
- `LabFusion/src/Data/Lobbies/PlatformInfo.cs`
- `LabFusion/src/Data/Files/BanManager.cs`
- `LabFusion/src/Representation/FusionPermissions.cs`
- `LabFusion/src/Voice/Unity/UnityVoiceSpeaker.cs`

## Version Validation

`NetworkVerification.CompareVersion()` explicitly zeroes the patch component and compares only major/minor. `ConnectionRequestMessage` uses this check after receiving a connection request. Steam matchmaking and Proxy lobby requests also filter only major/minor.

The requested exact three-part client/relay version gate therefore does not exist today. The relay handshake needs to reject a non-exact product version before joining gameplay; Fusion's connection validation should also be updated consistently. A separate protocol version should be checked independently. The exact string/tuple format, prerelease handling, and who owns the canonical relay version remain design decisions.

Relevant files:

- `LabFusion/src/Network/Helpers/NetworkVerification.cs`
- `LabFusion/src/Network/Messages/Server/ConnectionRequestMessage.cs`
- `LabFusion/src/Network/Layers/Steam/SteamMatchmaker.cs`
- `LabFusion/src/Network/Layers/Proxy/ProxyMatchmaker.cs`

## Build And Steam Dependency Surface

The main `LabFusion.csproj` has a direct reference to `Il2CppFacepunch.Steamworks.Win64.dll`, and `AssemblyInfo.cs` declares it as an optional Melon dependency. The source audit found Steam API usage in the Steam transport, plus Steam types in the Proxy layer and Steam-specific trusted-list branching. `BonelabSupport.csproj` also references the Steamworks assembly independently.

The vendored `LabFusion/dependencies/Facepunch.Steamworks` source is broad and its presence alone does not establish which portions can be removed. Do not remove the main assembly reference or vendored dependency until a source/build-reference audit proves no remaining runtime or support-module dependency. The separate `BonelabSupport` reference must be evaluated independently.

## Migration Checklist

1. Decide whether Android/Proxy remains in scope. If retained, replace its SteamID-based identity, lobby-ID join route, friend service, and username lookup too.
2. Define the relay handshake: protocol version, exact client product version, session/server identifier, authentication/identity, denial codes, keepalive, and reconnect/disconnect behavior.
3. Replace layer discovery/default selection and the login UI path with direct relay endpoint configuration and connection status.
4. Keep Fusion's `NetworkLayer` message APIs and lifecycle hooks where suitable; implement UDP ingress/egress and per-client routing behind them.
5. Specify a bounded thread-safe receive queue and main-thread dispatch. Specify send queue/backpressure and shutdown ordering; do not call Unity APIs from the socket thread.
6. Decide whether generic lobby metadata and `IMatchmaker` remain for a relay-backed server directory. Remove RoomCode abstractions/UI if IP:port is the chosen join mechanism.
7. Define stable player identity and its relationship to `PlatformID`, bans, admins, permissions, trusted lists, voice, and host migration.
8. Separate simulated-host runtime authority from administrator authorization. Ensure level changes, gamemode changes, server settings, kicks, bans, and similar management actions require administrator permission, not `OWNER` status. Specify how runtime authority and state transfer when the simulated host leaves.
9. Design the authoritative voting protocol for player-requested level/gamemode changes and any settings allowed to be voted on.
10. Define MOTD persistence/delivery, chat/whisper messages, client chat UI, and server console command permissions.
11. Enforce exact three-part client/relay version equality and an independent protocol version check at the relay handshake and Fusion admission boundary.
12. Audit/remove Steam-specific project references only after all source and support-module usages are accounted for.
13. Test two-client join, simultaneous joins, disconnect, simulated-host migration, packet loss/reordering, scene transition during join, version mismatch, vote lifecycle, MOTD delivery, public chat, whisper routing, and server shutdown before packaging.

## Open Decisions

- Does the Android Proxy path remain, or does every supported platform connect directly to the relay?
- Does the relay host only forward UDP packets, or also own authoritative server/session state and settings?
- Is there still a server directory/browser? If yes, which relay service supplies it, and is lobby metadata retained?
- What identity is authoritative: relay-issued client ID, persistent account ID, or both? How are bans/admins migrated?
- What exact conditions trigger pseudo-host migration, and what Fusion state is transferable versus recreated?
- How does relay authentication prevent a client from spoofing another player's identity or version?
- What packet size limits, fragmentation policy, congestion/backpressure behavior, encryption, and abuse/rate limits are required?

## Evidence Limits

This is a source-level dependency audit only. It does not establish runtime threading behavior inside the underlying Steamworks library, relay capabilities, or whether all listed flows are used in every supported build. Those points need implementation inspection and runtime tests when work begins.

## Refusion Runtime Flow

### Layer selection and activation

1. `FusionMod.OnInitializeMelon()` discovers all `NetworkLayer` implementations and registers them by `Title`.
2. `NetworkLayerDeterminer.LoadLayer()` resolves the saved layer title, checks platform support/validation, and falls back if necessary.
3. The login menu calls `NetworkLayerManager.LogIn(layer)`.
4. The layer invokes `NetworkLayer.InvokeLoggedInEvent()`. `NetworkLayerManager` assigns the active layer, calls `OnInitializeLayer()`, and marks login complete.
5. The user starts or joins a session through the active layer.

`IsHost` has gameplay meaning throughout Refusion; it is not merely a UI label. `NetworkInfo.IsHost` controls local server message handling, ownership authority, settings publication, level loading, gamemode controls, permissions, and catch-up. A dedicated-server adaptation that represents the first game client as the pseudo-host must preserve this behavior, even though the relay itself is a separate process.

### Pseudo-host startup

The source's host startup contract is `InternalServerHelpers.OnStartServer()`:

1. Apply initial local metadata.
2. Create and insert a `PlayerID` with `SmallID=0`.
3. Apply the local PlayerID and create the local network player.
4. Invoke `OnStartedServer` hooks and publish the started notification.
5. Reload the scene.

The pseudo-host must call this once, on Unity's main thread, only after it has been accepted as the session owner. It must not call it again for duplicate `Welcome`, retry, or ordinary player-join messages. A client becoming pseudo-host after a previous session cannot safely inherit the previous `PlayerID`/entity state just by changing `IsHost`.

### Normal client admission

The existing Fusion admission sequence is:

1. Establish transport connection.
2. Send `ConnectionRequest` with Fusion version, avatar barcode/stats, metadata, equipped items, and backup platform ID.
3. Host-side `ConnectionRequestMessage` validates capacity, loading state, privacy, version, bans, global bans, and dynamic connection hooks.
4. Host reserves a free `SmallID`.
5. Host broadcasts a `ConnectionResponse` for the new player.
6. Host sends each already-registered player's ID/avatar state to the joining client.
7. Host sends the current scene, dynamics assignment, and current server settings to the joining client.
8. Each client processes `ConnectionResponse`: inserts the `PlayerID`, creates a local or remote player, and invokes join hooks. On the host, the response also triggers player catch-up.

The order is significant: ordinary PlayerID/player creation must be processed before scene/entity catch-up data that references that player. These handlers are Unity/game-state operations and should be dispatched from the game update thread, not directly from the socket receive thread.

In a dedicated adaptation, the UDP relay handshake is a transport admission stage, not a replacement for Fusion's `ConnectionRequest` admission. A relay `Welcome` must not by itself be treated as a fully joined Fusion player. It should establish the authenticated relay identity and permit the Fusion request; Fusion player state should only be created by the existing connection response path after host validation.

### Message routing contract

Fusion's `MessagePrefix` contains a message tag, `MessageRoute`, and optional sender `SmallID`. `MessageRoute` carries the relay intent and channel:

| Fusion route | Existing P2P behavior | Dedicated pseudo-host behavior |
| --- | --- | --- |
| `None` | Reach host/server without an established sender ID | Relay only to the pseudo-host as an unhandled Fusion request |
| `ToServer` | Reach host/server with sender identity | Relay to pseudo-host; mark server-handled and attach relay-authenticated PlatformID |
| `ToClients` | Client request reaches host; host broadcasts to all | Host validates/handles, then relay broadcasts the resulting message to every game client, including host only where the existing message contract expects loopback |
| `ToOtherClients` | Client request reaches host; host excludes sender | Host validates/handles, then relay broadcasts to all except the original sender |
| `ToTarget` / `ToTargets` | Client request reaches host; host selects recipients | Host validates/handles, then relay resolves target `SmallID` values and sends only to those registered clients |

`NativeMessageHandler.ReadMessage()` validates sender `SmallID` against the transport-provided `PlatformID` for server-handled relay messages. Therefore the relay must bind the packet's source endpoint/session token to its authenticated platform identity and must never trust a client-provided `PlatformID`, `SmallID`, sender byte, route, or channel. The pseudo-host then hands client-originated messages to `NativeMessageHandler` with `IsServerHandled=true`; server-originated messages delivered to clients must preserve the expected non-server-handled path.

Reliable and unreliable are Fusion-level delivery requirements, not decoration. Reliable ordering must be maintained at least per peer/channel for connection, scene, spawn/despawn, ownership, constraints, settings, and catch-up. High-rate pose/voice paths can use bounded unreliable delivery where the owning message actually specifies it.

### Player, entity, and ownership synchronization

- Player identity is indexed by both `SmallID` and `PlatformID`; `SmallID` is embedded in message prefixes and entity ownership. `0` is permanently the host ID.
- Player join/leave is driven by `ConnectionResponse` and `InternalServerHelpers.OnPlayerLeft()`, not by the relay's transport-only `PlayerJoined` event.
- Network entities use `ushort` entity IDs and owner `PlayerID`s. Pose/cull handlers reject unregistered entities, missing owners, and senders that do not match the current entity owner.
- Ownership requests are sent to the server and answered by a reliable broadcast; because the current request handler does not show a separate generic admin permission check, the transport must not treat host status as permission for unrelated management actions.
- Player departure invokes entity cleanup/ownership callbacks. The relay must first deliver one authoritative leave event, then remove the identity mapping; duplicated transport timeout and explicit leave events must be idempotent.
- Join catch-up is incremental and yields across Unity frames. It stops while the scene is loading or the joining PlayerID becomes invalid. This behavior should be preserved; dumping every entity in one large UDP burst would change timing and create frame/network spikes.

### Scene and settings synchronization

- The host announces scene changes through `SceneLoad` and tracks load state in `FusionSceneManager`.
- Clients wait for the delayed load window, download a missing level when permitted, then load the target scene. Entity handlers can be marked `SkipHandleWhileLoading`.
- A joining player is denied by the current connection handler while the host is loading. A dedicated adaptation should retain this gate or define an explicit queued-admission state; it must not admit catch-up against a changing scene.
- Saved server settings are produced by `LobbyInfoManager` and sent reliably to other clients and joining clients. Client settings use a separate `PlayerSettings` message path.
- `LobbyInfoManager` is tied to lobby metadata/browser concepts today. The state serialization can be reused, but the lobby object/directory API should only remain if the dedicated service intentionally provides a server browser.
- `LevelRequestMessage` is a host-side request/approval flow with a 10-second global cooldown; it is not an already-authorized client map change.

## Current Prototype Findings

These are source-inspection findings about the newly added `DedicatedServerNetworkLayer` and `RefusionRelay`; they are blockers to gameplay testing, not merely future polish:

1. **No heartbeat is sent.** The relay expires a client after 15 seconds without receiving any packet, while the client currently sends no periodic Ping. An otherwise healthy idle client will be removed.
2. **The Fusion channel is ignored by the relay.** `Channel` is serialized by the client but the relay does not use it to select reliable versus unreliable delivery. All game traffic currently goes through ordinary UDP datagrams.
3. **No application-level fragmentation/reassembly exists.** A complete Fusion message is Base64-encoded into one JSON UDP datagram. Datagrams larger than the path MTU are IP-fragmented and become fragile under loss; sufficiently large payloads can exceed UDP's maximum datagram size. Catch-up and spawn payloads can therefore be silently lost or fail to send.
4. **There is no retransmission or ordering layer.** Required reliable Fusion messages can be dropped, duplicated, or reordered. Connection responses, scene loads, ownership, and spawn state cannot be considered correct over this transport yet.
5. **The Hello identity is client-asserted.** The relay accepts the client's `PlatformId` and echoes it. There is no authentication or endpoint-independent identity proof; bans/admin persistence and anti-spoofing are not safe.
6. **The current generated PlatformID is session-random.** It does not provide stable identity for bans, administrators, trusted players, or reconnect recovery.
7. **Receive work is queued to the main thread, but the queue is unbounded.** A burst or hostile sender can grow memory and consume a long frame when `OnUpdateLayer()` drains it. There is also no explicit queue budget/backpressure.
8. **Sending is synchronous from the game thread.** `UdpClient.Send()` is called directly by message send methods. Replace it with bounded asynchronous send processing before high-rate physics traffic.
9. **Transport join/leave packets are not a substitute for Fusion join state.** The `PlayerJoined` transport notification currently only populates a name map; actual player creation depends on the Fusion `ConnectionResponse`. This separation is correct in principle and must remain explicit.
10. **Host departure resets the whole session.** The relay intentionally does not remap existing PlayerIDs to `SmallID=0`. This avoids corrupting the current session's `SmallID` dictionaries but is not seamless pseudo-host migration. A real migration requires disconnect/rejoin plus state reconstruction or a designed identity remap protocol.
11. **The initial pseudo-host is selected at UDP Hello time.** It becomes host before Fusion scene/runtime readiness or Fusion-level policy checks. The relay needs an explicit session-ready/host-registration state and join gating before exposing a usable server.
12. **Targeted disconnect is incomplete.** `DisconnectUser()` currently sends an empty targeted raw-forward packet; the relay does not interpret it as a transport disconnect, so it does not reliably kick the target.
13. **The IP join UI is not implemented.** The layer has callable join methods, but there is no user-facing endpoint entry/validation/status workflow. It is also not selected as default.
14. **The current layer's `JoinServerByCode()` treats the code as an address.** That API should be removed/replaced with an explicit endpoint API rather than left as a misleading RoomCode-compatible override.
15. **Owner/admin separation is not achieved by transport changes alone.** Existing `FusionPermissions` grants `OWNER` based on `NetworkInfo.IsHost`; pseudo-host administrative rights need a separate policy and explicit server-side authorization.

## Dedicated Adaptation Boundary

The adaptation should be split into three authorities with explicit contracts:

1. **UDP relay authority:** endpoint/session authentication, exact product/protocol version gate, client ID and unique SmallID allocation, heartbeat/timeout, packet framing/reassembly, reliable delivery, bounded per-client queues, source identity stamping, target routing, rate limits, and transport leave.
2. **Pseudo-host runtime authority:** Fusion's existing game-thread message handlers, scene state, entity simulation/ownership, spawn validation, catch-up generation, game-mode state, and migration snapshot/recovery. It is runtime authority only, not automatic admin.
3. **Fusion client state:** local/remote `PlayerID` and player creation, avatar application, entity/physics interpolation and ownership rules, settings/UI, loading, and voice. It only changes state through validated Fusion messages.

The relay should not directly mutate Unity/Fusion objects. It can persist server configuration and policy, but changes that affect a live game must be delivered to the pseudo-host as authenticated control requests and applied by the pseudo-host on the Unity thread, unless/until a true headless Fusion simulation exists.

## Recommended Implementation Order

1. Replace the JSON/Base64 game packet envelope with a bounded binary frame and a proven UDP reliability/fragmentation implementation already compatible with the repository/runtime. Keep control-plane handshake packets distinct from Fusion data packets.
2. Define authenticated session identity and admission states: `Discovered -> Handshaking -> TransportAccepted -> FusionPending -> FusionJoined`; reject or expire invalid transitions, and make retries idempotent.
3. Implement bounded receive/send queues, main-thread dispatch budgets, heartbeat/timeout, duplicate suppression, and explicit disconnect/kick semantics.
4. Add protocol tests for simultaneous joins, duplicate identity, ID exhaustion/reuse, owner departure, timeout-vs-leave races, message route/channel preservation, large-message fragmentation, loss/reordering, and queue bounds.
5. Add the endpoint UI and make layer selection explicit. Do not switch the default until relay and client versions interoperate in a two-client Fusion connection test.
6. Verify real game flows in order: player join/catch-up, scene transition, prop spawn/pose/ownership, constraints/seats, avatar change, settings/gamemode, voice, leave, and pseudo-host departure.
7. Only after the transport and session lifecycle are stable, add relay-persisted settings, admin policy, votes, MOTD/chat/commands, and mod admission rules.

## Follow-Up Fixes (2026-10-06)

Hardening round applied to `RefusionRelay/Program.cs` and the client layer:

- Prototype finding 1 (no heartbeat): resolved — the client sends `Ping` every 5 seconds (`LabFusion/src/Network/Layers/Steam/SteamNetworkLayer.cs`).
- Prototype finding 12 (targeted disconnect): resolved — the client sends a `Kick` relay request and the relay removes the target.
- Prototype finding 5 (client-asserted `PlatformId`): resolved — the relay ignores the client-supplied `PlatformId` and always derives it from the persistent UUID; duplicate-session detection is UUID-only.
- Session token (protocol version 2): `Welcome` carries a random `SessionToken`; every post-Hello packet must echo it or it is dropped silently. `Hello` and `Query` stay token-free.
- Relay data files are anchored next to the relay executable (`refusion-server-state.json`, `refusion-server-settings.json`, `refusion-server-access.json`) instead of the process working directory.
- Per-client packet budget: 500 packets/s sustained, 1500 burst, sized from the measured worst-case client traffic (~400 packets/s).
- Relay stability: the receive loop and shutdown path no longer die on `SocketException` (including the Windows `SIO_UDP_CONNRESET` behavior), console/cleanup tasks contain per-iteration exceptions, and all JSON persistence uses atomic temp-file writes.
- Console input is read on a background task. Previously `Console.In.ReadLineAsync` blocked inline when stdin was a redirected pipe (service/nohup deployments), which stalled `RunAsync` before the receive loop ever started — the relay accepted no packets at all in that configuration.
- Deliberately unchanged: the relay's OWNER kick/ban bypass (all moderation packets are emitted by the pseudo-host after its own `FusionPermissions` check; removing the owner branch would break in-game moderation until the admin/vote redesign), stale-session reconnect rejection, and `PersistentPlayerId` in `PlayerJoined` broadcasts (the client uses it for unban).

## Steam Removal (2026-10-06)

Team decision: all Steam networking is removed; the relay is the only transport on every platform, including Quest, which now connects directly to the relay instead of the Steam-based Proxy stack. A full usage audit (source, support module, and build surface) preceded the removal, satisfying the former hard rule from "Build And Steam Dependency Surface".

Removed client surface:

- Steam transport remnants: `SteamSocketManager`, `SteamConnectionManager`, `SteamLobby`, `SteamMatchmaker`, and the dead Steam members of the converted class (`GameHasSteamworks`, `ShutdownGameClient`, `SteamId`, `SteamSocket`/`SteamConnection`, `_localLobby`, `ApplicationID`, `JoinServer(SteamId)`).
- The entire `Layers/Proxy` stack (8 files) and the Android special case in `NetworkLayerDeterminer`. `DedicatedServerNetworkLayer` is now concrete (absorbed `SteamVRNetworkLayer`, Title "Dedicated Server") and supports every platform; the layer lives in `src/Network/Layers/DedicatedServer/`.
- `SteamSocketHandler`'s live dispatch entry was kept and renamed to `RelayMessageDispatcher`; its Steam send helpers were deleted.
- `SteamAPILoader` and the embedded `steam_api64.dll` (the game ships its own copy; a runtime sanity check is recommended).
- Steam-era master/trusted lists (`TrustedListManager`, `MasterPermissionsManager`) and the "(FAKE)" display-name tagging; relay operators are the only privileged identity source.
- `ServerPrivacy.FRIENDS_ONLY` (under the relay's self-only `IsFriend` it blocked all joins) and the `ProxyPort` setting.
- Build surface: `Il2CppFacepunch.Steamworks.Win64` references (LabFusion and BonelabSupport), the vendored `dependencies/Facepunch.Steamworks` source (145 files), the `MelonOptionalDependencies` declaration, and the stale `LabFusion - Backup.csproj`.

RoomCode (`GetServerCode`/`RefreshServerCode`/`JoinServerByCode`) was deliberately kept in the Steam removal round and then retired in the follow-up menu pass: the relay address is the join key, so the server page now shows the address with a "Copy Address" button, `JoinServerByCode` was renamed to `JoinServerByAddress`, and the random code generation/wiring (`GetServerCode`/`RefreshServerCode` and the Welcome-time generation call) was removed. `RandomCodeGenerator` remains as an unused generic utility.

Relay addresses accept hostnames and `localhost`, not just IP literals. On join, the client resolves the host off the main thread (`Dns.GetHostAddressesAsync`) and re-enters the connect flow on the main thread through a queued action; the server page and lobby metadata keep the address as typed. The server-browser query path resolves hostnames synchronously (it already blocks on its UDP receive). Malformed addresses and failed resolutions now raise in-game error notifications instead of only logging.

### Two-Client Test Fixes (2026-10-07)

The first live two-client test exposed a missing host loopback. Upstream hosts received their own broadcasts through a self-connection, and several handlers depend on it: `ConnectionResponseMessage` inserts the joining player's `PlayerID` on the host, and the spawn flow creates the object in `SpawnResponseMessage`. Fixes:

- Relay `ServerBroadcast` now echoes to the pseudo-host as well (previously broadcast to every client except the host), restoring upstream loopback parity.
- The relay layer's `SendFromServer(ulong)` resolves targets through the relay identity map (`_relaySmallIds`) when the target has no `PlayerID` inserted yet, matching upstream's transport-level resolution (`ConnectedSteamIDs`). Without it the join catch-up (existing player state, level load, dynamics assignment, lobby info) was silently dropped, because the host sends those to the joiner before the joiner's `PlayerID` exists on the host.
- Expected shutdown no longer logs `Cleanup task failed: The operation was canceled.`
