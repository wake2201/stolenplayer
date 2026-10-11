# Runtime analysis

Investigation date: 6 October 2026 (Europe/Berlin). Status: gameplay capture, harmless Harmony detours and Unity/net46 LiteNetLib loopback verified; final multiplayer plugin gameplay acceptance pending.

## Discovery and build identity

Automatic discovery inspected Steam registry keys, the running Steam process, default installation directories, `libraryfolders.vdf`, and app manifests. Neither configured Steam library contains `appmanifest_1332720.acf`. Resolved machine paths remain in ignored `.local/discovery.json`.

A bounded drive-root fallback discovered the analyzed local installation, **not a manifest-verified Steam installation**. Its exact path remains in ignored `.local/game-path.txt`. Its managed `SteamManager.Awake()` passes app ID 1332720 to `RestartAppIfNecessary`. Native plugin files include `steam_emu.ini` and `steam_api64.rne`; Steam service behavior on this installation cannot establish official Steam interoperability. No emulator configuration or native binary was changed.

The resolved machine path and discovery record are ignored: `.local/game-path.txt`, `.local/discovery.json`. No entire-game copy was made.

`Assembly-CSharp.dll` SHA-256: `3786E09344BD2CC72E599EBC2468F10C04BAEC58A9279A43B70FB56F79A9EA50`. This fingerprint scopes the implementation findings; other releases need reinspection.

## Verified runtime facts

| Finding | Evidence | Confidence |
|---|---|---|
| Unity 2020.3.33f1 | Serialized header of `Thief Simulator 2_Data/globalgamemanagers`; diagnostic `Application.unityVersion` | Verified static and runtime |
| 64-bit Windows | Game EXE PE machine `0x8664`; loader reports Bits64 | Verified static and runtime |
| Unity Mono | `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`, managed implementation bodies; loader CLR `4.0.30319.17020` | Verified |
| Not an IL2CPP build | No `GameAssembly.dll` or `il2cpp_data/Metadata/global-metadata.dat`; executable managed method bodies are present | Verified for this installation |
| .NET 4.x API family | Cecil module runtime `v4.0.30319`, `mscorlib` 4.0.0.0, `netstandard` facade 2.0.0.0 | Verified metadata; not a claim that every desktop .NET 4.x API exists |
| `net46` diagnostics supported | Own plugin compiled against official net46 reference assemblies and installed Unity assemblies; loaded successfully in game | Verified for APIs used by our plugin |
| BepInEx 5.4.23.5 x64 compatible | Loader reaches chainloader, diagnostics captures menu and loaded gameplay | Verified diagnostics runtime; final multiplayer plugin startup pending |
| Harmony detours | Runtime identity 0Harmony 2.9.0.0; harmless empty LootBox.Start Prefix/Postfix execute once each then unpatch | Verified detours and immediate removal |
| Dynamic reflection emit limitation | BepInEx reports Supports SRE=False, but harmless Harmony detour succeeds | Verified; no general certification of arbitrary patches |
| LiteNetLib 1.3.5 Unity/net46 | Pinned source with documented net46 platform/tuple adaptations exchanges a reliable loopback packet in game | Verified runtime, no extra runtime packages |
| Steamworks.NET integration | `com.rlabrecque.steamworks.net.dll`; `SteamManager` calls Init/RunCallbacks/Shutdown; wrapper includes Networking/Sockets/Messages | Verified static; native networking availability unknown |

Managed directory: `<game>/Thief Simulator 2_Data/Managed`. `Assembly-CSharp.dll` is located directly there, not in this repository.

## Important assemblies

| Assembly | Relevant purpose |
|---|---|
| Assembly-CSharp.dll | Gameplay, UI, player, saves, missions, AI; also many third-party/demo classes |
| Assembly-CSharp-firstpass.dll | RootMotion/FinalIK and other third-party functionality; not the primary player controller |
| UnityEngine.CoreModule.dll | GameObject, Transform, MonoBehaviour, scene APIs |
| UnityEngine.PhysicsModule.dll | CharacterController, Rigidbody, collision/raycast APIs |
| UnityEngine.InputLegacyModule.dll | Legacy input still used alongside Rewired |
| UnityEngine.AnimationModule.dll | Animation/Animator and skinned mesh support |
| UnityEngine.IMGUIModule.dll | Potential Phase 1 debug UI; diagnostics currently has no UI |
| Rewired_Core.dll / Rewired_Windows.dll | Local input ownership |
| AstarPathfindingProject.dll | `Pathfinding.RichAI` used by citizen AI |
| FileBasedPrefs.dll | FBPP persistence and typed JSON save model |
| com.rlabrecque.steamworks.net.dll | Managed Steamworks wrapper |

All assembly identities and hashes are in ignored `.local/assembly-list.json`. Metadata index: 4,102 game types, 484 firstpass types, 488 Steamworks types, 9 FileBasedPrefs types, including nested compiler-generated types.

## Toolchain and evidence

No SDK or `ilspycmd` initially existed on PATH. Bootstrapped an ignored local .NET SDK 9.0.318, ILSpy CLI/Decompiler 9.1.0.7988 with runtime roll-forward, and Mono.Cecil 0.11.6. The modern SDK is a build tool, **not** the runtime injected into the game. No global ILSpy installation is required.

Static inspection uses Cecil metadata without loading/executing game assemblies. Decompilation is type-targeted. `.local/type-index.json` contains namespaces, kind, base classes, interfaces, fields, properties, methods/signatures, attributes, references and hashes. `.local/system-candidates.json` records systematic concept searches. Candidate names alone are not treated as evidence.

Runtime evidence: `.local/runtime-dumps/BepInEx-LogOutput.log` and ignored `recon-*.ndjson`. Initial menu captures were followed by the supplied 112054 gameplay capture: MainScene/IntroHouse and eight PlayerScript snapshots over a full scene reload. The 125807/130053 runs verify an empty-method Harmony detour and LiteNetLib reliable loopback. [Current report](recon-report.md) distinguishes these results from unverified final multiplayer UI/proxy gameplay acceptance. Computer Use was stopped with physical Escape during the expanded follow-up capture.

## Original file protection

Before adding loader/plugin files, 183 original DLL/EXE files were hashed. Every original binary still matches its baseline. Loader deployment adds files and refuses to overwrite existing loader destinations. Our own plugin/config are the only replaceable paths. Assets and saves were not edited by our tools; assets were not fully hashed, and ordinary game startup can update its own preferences/logs.

Sources for tooling: [official BepInEx release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5), [BepInEx installation documentation](https://docs.bepinex.dev/articles/user_guide/installation/index.html). Local runtime success is stronger evidence than general compatibility guidance.
