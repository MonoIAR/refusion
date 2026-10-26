# Refusion 
by MIAR STAFF

A multiplayer mod for BONELAB, but reimagined. Connect through UDP relay server, while reworking parts of the original Fusion experience.
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue)](LICENSE)

> [!TIP]
> Many parts of the code are **AI-GENERATED**. IF YOU HATE AI, THIS IS YOUR WARNING.

## Documentation
For SDK documentation, see the [LabFusion wiki](https://github.com/Lakatrazz/BONELAB-Fusion/wiki).

## Networking
Refusion uses dedicated UDP relay server for multiplayer traffic. Players connect using the relay server's address and port, without requiring Steam Networking.
A server is required for self-hosting, but you can also use your PC as relay server.

## Compatibility
Refusion is *supposed to be* compatible with all Fusion modules and SDK features. But if you find any compatibility issues, please report in the issue tracker, or [MIAR STAFF Discord](https://discord.gg/TTevnjERxR).

## Credits
Refusion is based on the work of the [LabFusion](https://github.com/Lakatrazz/BONELAB-Fusion) project. We are grateful to its original developers and contributors.

Refusion developer: [ChakerAt](https://github.com/cha-at), [CAitVR](https://github.com/RTX9999ti)

## Licensing
Refusion is licensed under the [GNU General Public License v3.0](LICENSE). The following exceptions and attributions apply:

- Portions derived from [LabFusion](https://github.com/Lakatrazz/BONELAB-Fusion) remain licensed under the MIT License (Copyright (c) 2023 Lakatrazz). The full license text can be found in [LICENSE-MIT](LICENSE-MIT).
- The G711 A-law codec source (`LabFusion/dependencies/GroovyCodecs`) is licensed under the GNU Lesser General Public License v2.1 or later (Copyright 2011, Red Hat, Inc. and individual contributors, modified by Mobius Software Ltd.). See the file headers for the full notice.
- `LabFusionUpdater` is adapted from the [BoneLib](https://github.com/yowchap/BoneLib) auto updater, which is licensed under the GNU General Public License v3.0.
- The source code of [LiteNetLib](https://github.com/RevenantX/LiteNetLib) is included under the MIT License. The full license can be found [here](https://github.com/RevenantX/LiteNetLib/blob/master/LICENSE.txt).
- This project builds against, but does not redistribute, MelonLoader (Apache-2.0), Il2CppInterop (LGPL-3.0), Harmony (MIT) and Newtonsoft.Json (MIT).
- Third-party assets under `LabFusion/FusionBundles/Assets` (e.g. shaders and textures) are excluded from the licenses above and remain the property of their respective owners.

## Disclaimer
THIS PROJECT IS NOT AFFILIATED WITH OR SUPPORTED BY ANY OTHER MULTIPLAYER MODS OR STRESS LEVEL ZERO.
