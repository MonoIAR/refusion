# Default MelonMod Blacklist

Status: default hard-coded rules for Refusion.
Anticheat is disabled by default. These rules are checked only after the relay's server configuration enables anticheat.

The original LabFusion host-side content blacklist is disabled by Refusion. The host no longer uses `mod_blacklist.txt` or `globalModBlacklist.json` to reject network spawn requests. Client-local download and content safety checks remain separate and do not control server admission.

## Enforcement

1. The relay owns the `AnticheatEnabled` server setting and sends its authoritative value to connected clients.
2. When disabled, clients do not scan loaded MelonMods for this blacklist and do not reject players for matching entries.
3. When enabled, the joining client reports a manifest of its loaded MelonMods after Fusion has accepted the player into the server.
4. The host-side Refusion runtime checks the manifest against these built-in rules and disconnects a matching player with an English reason.
5. The relay setting controls whether the check runs. The blacklist entries themselves are built into Refusion and are not editable by clients.

Client-reported manifests are not proof against a modified client. A client can falsify its manifest or patch out local checks. This mechanism is intended to enforce the server's policy against ordinary clients, not to provide tamper-proof anti-cheat.

## Matching Rules

- SHA-256 is the primary exact-file identifier. It is independent of the DLL filename and assembly name.
- Assembly simple name is a secondary identifier only when the entry has no known collision.
- Melon display name and author are useful diagnostic metadata, but must not be the only matching fields.
- File name is recorded for operator readability and must not be used as the sole enforcement key.
- No stable common MelonMod GUID was identified for the supplied files.
- Keep each supplied SHA-256 as its own rule. Do not merge entries just because their display or assembly names look related.

## Built-In Entries

| Supplied file | Assembly name | Assembly version | SHA-256 | Rule notes |
|---|---|---:|---|---|
| `ClimbAnything.dll` | `ClimbAnything` | `1.0.0.0` | `072EE41944C801BAC9954B227DE0E22B17559EDDCC71CD88C5E6236718E6DB02` | Exact hash and assembly name |
| `FlappyLab.dll` | `FlappyLab` | `1.0.0.0` | `D03672AF4BE36284C2751C90B6B39FECFCE73CC14603057F467E9F1DEF6723D0` | Exact hash and assembly name |
| `DynamicFlight.dll` | `DynamicFlight` | `1.0.0.0` | `D56496EB77F941D3C929A71AEBD52EE3CBF522C9DA88A2231D15C73EC7627A70` | Exact hash and assembly name |
| `BlinkBone.dll` | `BlinkBone` | `0.0.0.0` | `DBCBEAD9D43319A5A651BEA932B7425B4A8D6C77DAAE9F38728B64993B859E3C` | Exact hash and assembly name |
| `DoubleJump.dll` | `DoubleJump` | `1.0.0.0` | `209AF166DD0FA242412EC859EED31EB658974FC68A75FACA6F4F6E1EA7533788` | Exact hash and assembly name |
| `WallWalk.dll` | `WallWalk` | `1.0.0.0` | `D1CDE8FA103F9CE636DADD0C984DBBD6950C7B1603DD74899CB1CFF42ED132DD` | Exact hash and assembly name |
| `Stat_Changer__Simple_Edition_.dll` | `Stat_Changer__Simple_Edition_` | `1.0.0.0` | `BF1C21D4C5579C695BD7B53BFB985F73D7DB37D88FD08BB66A64D49812CA55CE` | Exact hash and assembly name |
| `Stat_Changer__Final_Version_.dll` | `Stat_Changer__Final_Version_` | `1.0.0.0` | `1F7FAD26A1CDA03582F36CB304DFB80764BA6EF2E113454011A397685C699630` | Exact hash and assembly name |
| `QuantityOfLab.dll` | `QuantityOfLab` | `1.0.0.0` | `6812F4B5B7B017969C073F8BA83B3934760FCEEE7811912495DAA86E30A64702` | Exact hash and assembly name |
| `DynamicFlightFixed.dll` | `QuantityOfLab` | `1.0.0.0` | `D52CF677B2AC6895B15F40860610B5DE9BC87A21BC475E3E0DF065CC90CD53A5` | Exact hash only; assembly name collides with `QuantityOfLab.dll` |
| `ClaudeFlyV2.dll` | `ClaudeFlyV2` | `1.0.0.0` | `FFD9EF3E9D492DBAD10D1A6693D1FA860EEE7603B3EDAFF8A15EE9087899A250` | Exact hash and assembly name |
| `ToggleableGodMode.dll` | `ToggleableGodMode` | `1.0.0.0` | `FB165009D0EE43166583E0E23104C71771374D8916CB89720CAA83666C489421` | Exact hash and assembly name |
| `GodMode.dll` | `GodMode` | `1.0.0.0` | `191892F9662EFE3EE51FAEE39C02C603171E116A6443AFA7641718E57731BC04` | Exact hash and assembly name |

All supplied assemblies have a null public-key token; none is strong-name signed.

## Future File-Based Rules

The intended future operator workflow is a relay-side `melon-mod-blacklist` directory containing blocked DLLs. The relay would hash each file and publish a generated manifest containing its SHA-256 and inspected assembly metadata. The relay must never load or execute DLLs from this directory.

Before replacing the built-in list, the file-based implementation must define duplicate handling, unreadable/corrupt DLL behavior, atomic reload, audit logging, and synchronization of rule updates to the pseudo-host. Keep built-in hashes as a fallback until that workflow is implemented and tested.
