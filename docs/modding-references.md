# Public interoperability references

Reviewed 6 October 2026. No third-party mod source was incorporated.

The public [xiaodong2008/BepInEx_ThiefSimulator2 repository](https://github.com/xiaodong2008/BepInEx_ThiefSimulator2) declares an MIT license. Its [project file](https://github.com/xiaodong2008/BepInEx_ThiefSimulator2/blob/main/BepInEx_ThiefSimulator2.csproj) targets net46, references BepInEx 5, Unity modules 2020.3.33, Assembly-CSharp and modular Unity DLLs. This is useful historical compatibility evidence; its local paths and package wildcards were not adopted. Our installed-version evidence and successful runtime plugin load independently confirm the selected diagnostic target.

Its [plugin source](https://github.com/xiaodong2008/BepInEx_ThiefSimulator2/blob/main/Plugin.cs) shows that a BepInEx plugin approach is publicly used for this game. We did not execute or copy this cheat mod, its features, or its access code. Local game metadata/decompilation remains the authority for class names and behavior in our build.

Use the [official BepInEx 5.4.23.5 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and [official documentation](https://docs.bepinex.dev/articles/user_guide/installation/index.html), rather than a repackaged game-specific loader archive. The downloaded loader/dependencies live in ignored storage and the installed game folder. Their license notices remain with those dependencies; they are not vendored into this repository.

[LiteNetLib 1.3.5](https://github.com/RevenantX/LiteNetLib/tree/1.3.5) is now the implemented transport dependency, built from pinned MIT-licensed source with two net46 compatibility adaptations. [Provenance and attribution](../third_party/LiteNetLib/PROVENANCE.md) record the archive hash, modifications and license requirements. Our protocol and game adapter code are separate from the transport. Reliable loopback was verified in the actual game runtime; final multiplayer gameplay acceptance remains pending. [Valve's networking documentation](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets) informs transport comparison; it does not certify the native Steam-emulation files found locally.
