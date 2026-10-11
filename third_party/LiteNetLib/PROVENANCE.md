# LiteNetLib 1.3.5

C# sources from the upstream `1.3.5` tag:
https://github.com/RevenantX/LiteNetLib/tree/1.3.5

Archive: https://codeload.github.com/RevenantX/LiteNetLib/zip/refs/tags/1.3.5
SHA-256: `36714C2FAF844713768AD37A234AD3483F53DF98CAEFDC7A38C864D6667ADC18`.

Our project file builds this source for net46 with Unity 2020.3 platform flags,
and net9.0 for the independent transport harness. The upstream tag's project
metadata still says 1.3.1; source provenance is the pinned tag/archive, not that
stale assembly metadata. A net46-only compatibility file substitutes platform
detection using Environment.OSVersion for RuntimeInformation (this Windows
build is the supported target). NetPeer's single tuple swap has an equivalent
temporary-variable swap under NET46. Vendored sources also have normalized LF
line endings and trailing whitespace removed. NetManager has the explicitly recorded
queue-budget adaptation in NetManager.patch.json: pending-event counters and dequeue
of one complete callback at a time, bounded by count/time. Upstream's bounded poll
detached the complete chain and discarded the remaining tail when its count limit
was reached. Deferred reliable callbacks now remain ordered on the original queue.
The validator reconstructs the exact adaptation from pinned upstream using checked
replacement counts, rather than exempting this file from provenance checking.
ManualReceive and unsynchronized callback modes do not use this owner-thread time
budget; the game uses the synchronized event queue. No other source changes are made.
The upstream 1.2.0 release has the same RuntimeInformation requirement; 0.9.5.2
supports net35, but retaining 1.3.5 with these two small adaptations avoids
reverting the transport implementation. The Microsoft compatibility packages
were tested and rejected because this game lacks their required BCL facades.
No runtime NuGet dependencies are required. Framework reference assemblies are
build-only.

MIT license: retain the complete accompanying LICENSE.txt in source and plugin
packages. The copyright holder is Ruslan Pyrch.
