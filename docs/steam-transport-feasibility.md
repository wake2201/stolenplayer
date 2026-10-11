# Optional Steam transport feasibility

Research only. LAN/direct-IP LiteNetLib stays independent. No Steam identity,
AppID file, ownership/authentication, original DLL or game binary was changed.
No lobby/networking API was called in this investigation.

## Local integration

VERIFIED_STATIC: loaded `com.rlabrecque.steamworks.net` metadata reports .NET
version 20.0.0 / Steamworks SDK 1.52. Constants expose
SteamNetworkingSockets009, SteamNetworkingMessages002, SteamNetworkingUtils003,
SteamMatchMaking009 and legacy SteamNetworking006. This proves wrapper surface,
not working backend availability. Source expects native version 06.75.97.18 /
declared native DLL size 290,536; the observed crash module's steam_api64 size was
1,126,400 and file version 1.0.1.47. Compatibility/provenance is not established by
that discrepancy alone.

VERIFIED_STATIC: SteamManager.Awake tests packing/native binaries, calls
RestartAppIfNecessary with the game's original ID 1332720, then SteamAPI.Init;
it requests DDOL, runs callbacks in Update and Shutdown on destruction. Its
Initialized property invokes a lazy singleton getter. The diagnostics never call
it because read-only inspection must not create/reinitialize Steam.

VERIFIED_RUNTIME: Steamworks.NET is loaded and game logs printed a persona-like
string and `1332720 True`. R9 expected-name existing-object lookup produced no
SteamManager record. Neither log nor DLL presence verifies authenticated service,
SteamAPI initialization result, lobby creation, network sockets or relay operation.
The discovered installation is not manifest-verified as a legitimate Steam launch.
Those runtime capabilities remain UNKNOWN. There is no second legitimate game
installation available for peer testing.

## Legitimate development options

Valve documents Spacewar (AppID 480) as its Steamworks SDK example, including
Steam-client setup and multiplayer features. A separate legitimate SDK example/test
application can exercise that configuration. It does not authorize changing Thief
Simulator 2's identity or bypassing ownership. Keep such a harness entirely separate
from the game's initialized SteamAPI. [Valve SDK example](https://partner.steamgames.com/doc/sdk/api/example).

Valve describes Steam networking sockets/messages and matchmaking lobbies as
distinct facilities. Sockets can provide connection-oriented transport; lobbies
provide discovery/session metadata and do not substitute for gameplay replication.
The installed wrappers predate current documentation, so API signatures/version
availability must be checked against the pinned wrapper before implementation.
[Networking overview](https://partner.steamgames.com/doc/features/multiplayer/networking),
[ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets),
[matchmaking](https://partner.steamgames.com/doc/features/multiplayer/matchmaking).

Steamworks.NET's manager guidance explains lifecycle/callback responsibility;
integrate with the game's verified existing lifetime rather than a second Init or
Shutdown owner. [SteamManager guidance](https://steamworks.github.io/steammanager/).

LIKELY: an optional sockets transport is technically appropriate if the game's
legitimate Steam runtime exposes compatible interfaces and both players can
authenticate. It is not accepted based on source constants. Wrapper/native mismatch,
SDK redistribution terms, callbacks, ownership and relay/lobby operation are gates.

## Evaluation gates (future, not implemented)

1. Run an owned, legitimate Steam build normally and inspect an existing initialized
   manager without invoking its lazy constructor. Record actual user/app/interface versions.
2. Exercise a separate official SDK/Spacewar example with two authorized accounts;
   verify connectivity/relay/authentication without modifying the game.
3. Select compatible wrapper APIs, callback ownership and bounded send/receive paths.
   Test cancellation, reconnect, packet-size limits and service errors.
4. Implement an optional ITransport adapter with the same session/protocol semantics;
   lobby discovery remains optional. Keep LAN/direct-IP runnable without Steam.
5. Validate on two legitimate game installations and supported Steam/network settings.

No Steam migration is required for the next avatar/registry milestones.
