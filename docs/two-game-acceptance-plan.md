# Future two-game acceptance

**TWO-GAME ACCEPTANCE: DEFERRED.** This is a separate cooperative gameplay gate;
it does not invalidate Milestone B's game + protocol-harness avatar acceptance.
Harness packets cannot replace real bidirectional gameplay evidence.

## Option 1 — two legitimate processes on the same PC

First attempt two normal, legitimate Thief Simulator 2 processes using supported
launch behavior. Process A selects Host; process B selects Client and joins
`127.0.0.1` on the configured port. Use compatible game fingerprints, mod 0.3.0
and protocol 2, and independently backed-up singleplayer profiles.

Record available RAM, each process's working set and private bytes, total system
commit/limit, CPU and GPU load before launch, during both loads and during play.
Previous base-game allocation failures and commit pressure make memory a real
constraint. Stop the extra process if sustained pressure makes the session unstable.
Do not automatically change pagefile settings. Separate startup failure from
avatar construction or transport failure using acquisition/spawn timestamps.

If normal simultaneous launching is unsupported, ownership/authentication rejects
the second launch, or memory makes it unstable, use Option 2. Never bypass DRM,
ownership, Steam authentication or supported launch restrictions.

## Option 2 — two separate PCs

Use two legitimate installations/accounts as required by the game. Match builds
and mod/protocol versions. Host on A and join A's LAN address from B. Retain
independent saves and perform the same acceptance matrix in both directions.

## Required real bidirectional acceptance

For each actor, exercise original local input and verify that the other game's
remote avatar displays sustained idle/walk/sprint/stop, small/90/180-degree turns,
yaw wraparound, look up/neutral/down and None/Crowbar switching. Record actual
outgoing and incoming semantic states separately from visual limitations.

Verify original local camera, listener, hands, arms, functional crowbar, inventory
and interactions before connection, while connected and after disconnect. Inspect
feet, model facing, interpolation, animation transitions and root immutability.
Exercise graceful and abrupt equipped disconnects, timeout cleanup, reconnects
and safe scene recreation. Repeat with network delay/loss when basic acceptance
passes. Count roots, avatars, equipment and private clips; record memory trends.

Keep raw captures locally ignored. Publish a source-backed acceptance summary
with explicit VERIFIED_RUNTIME / LIMITED / UNKNOWN results, rather than treating
successful handshakes or harness movement as completed two-game acceptance.

This plan authorizes no test in the Milestone B finalization task. Milestone C
requires a separate implementation request; the deferred gate remains visible
through later development and must pass before claiming cooperative acceptance.
