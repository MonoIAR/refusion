# Refusion

A multiplayer mod for BONELAB, but reimagined. **Refusion** is a fork of [BONELAB-Fusion](https://github.com/Lakatrazz/BONELAB-Fusion) that replaces Steam networking with a dedicated UDP relay server.

> Many parts of this project are AI-generated.
> Please DO NOT use or criticize it if you hate AI.

# Installation

Refusion replaces BONELAB-Fusion, so remove any previous version of BONELAB-Fusion from your mods folder before installing it.

For general setup instructions (MelonLoader, mod managers, Quest installation), refer to the [LabFusion wiki](https://github.com/Lakatrazz/BONELAB-Fusion/wiki#installation); the same process applies to Refusion.

# About

## How to Use the Mod
You can access the Fusion menu by clicking on the "Fusion" button in the Preferences menu.

If you did not already install the [Fusion Content](https://mod.io/g/bonelab/m/fusion-content), clicking this will install it.
It will only install correctly if you are logged into mod.io in VoidG114 or BONELAB Hub, so make sure this is the case.

## Networking
Refusion does not use Steam networking. Every player connects directly to a dedicated UDP relay server:

1. Run a relay server (or get the address of one), and share it with the players.
2. Open the Fusion menu and go to Matchmaking, then select "Enter Address".
3. Type the relay's address (`IP:port` or `domain:port`, e.g. `example.com:28430`) and click "Join".

There is no separate "create server" step — the first player to connect to the relay becomes the session host, so everybody simply joins the same address.

To host your own relay, the project includes the standalone `RefusionRelay` server; see the [project repository](https://github.com/MonoIAR/refusion) for details.

## Physical Interactions
Instead of players and synced props being kinematic, non-physics objects, all interactions are solved using physical forces.<br>
This means you can pick up your friends, throw them, stand on them, push them, and more.

## Campaign Support
The entire campaign has been ensured completable, with nearly every custom event synced, as well as optimizations made to make the experience run as smoothly as possible.

## Supported Platforms
- Steam PCVR
- Meta PCVR
- Meta Quest

## Crossplay Support
All platforms are able to crossplay together. Every platform connects directly to the same dedicated relay server by entering its address upon opening the menu.

## Compatible Content
Refusion is intended to be compatible with all existing Fusion modules and SDK features. If you find any compatibility issues, please report them in the [issue tracker](https://github.com/MonoIAR/refusion/issues) or the [MIAR STAFF Discord](https://discord.gg/TTevnjERxR).

# Additional Content
## Modules, Gamemodes, and the Bitmart
Fusion has integrated support to allow other mods to implement Fusion compatibility using "Modules". There is an SDK for Unity, allowing you to implement features while in multiplayer, as well as a code SDK in order to create unique synced features. You can find both of these [here](https://github.com/MonoIAR/refusion).

Besides that, Fusion also has integrated gamemodes. The current gamemodes are:
- Deathmatch, Free-for-all fighting!
- Team Deathmatch, Kill as many players on the opposite team!
- Entangled, Be constrained to a randomized partner!
- Hide and Seek, Seekers try to find hiders as quick as possible!
- Smash Bones, use dropped items to knock players off the stage and be the last one standing!
- Juggernaut, defeat the giant Juggernaut to gain its power, then kill as many survivors as possible!

Custom maps can implement these gamemodes, and customize them specifically for their map.

Finally, Fusion has built in cosmetics. Winning gamemodes will earn you Bits to use at the Bitmart. Here, you are able to equip/unequip as well as purchase items such as cosmetics. Other players will be able to see these cosmetics, and you can mix and match as you choose.

## Achievements
As an additional way to earn Bits, you can accomplish various tasks to complete Achievements. There are a large amount of Achievements, each with differing challenge, risk, and reward. Upon completing every achievement, you may even unlock some secret items!

# More
## Credits
- The foundation of this project: [LabFusion](https://github.com/Lakatrazz/BONELAB-Fusion)
- Main developers: [ChakerAt](https://github.com/cha-at), [CAitVR](https://github.com/RTX9999ti)

## Source
- Refusion: https://github.com/MonoIAR/refusion
- LabFusion (original project): https://github.com/Lakatrazz/BONELAB-Fusion

## License
Licensed under the GNU General Public License v3.0, with portions derived from LabFusion remaining under the MIT License. Full attributions are listed in the project README.
