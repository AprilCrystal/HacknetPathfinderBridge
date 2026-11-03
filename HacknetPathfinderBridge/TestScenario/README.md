# Optional security test fixture

This plugin registers `hnpb.testscenario` through the public `IBridgeAdapter` API. Install it only for an explicitly requested test. It accepts the extension named `I`; other extensions are rejected.

Copy `BridgeSecurityLab.xml` into that extension's `Nodes/` directory. Invoke `setup` with empty arguments while paused to load a missing node, discover it and install missing port crackers on the actual player computer. Existing node security progress is retained; setup is not a reset command.

The target is `198.18.0.90`: four required ports, a 90-second trace, a 15-second proxy and a firewall. Built-in `shell`, `analyze`, `solve` and `PortHack` complete the rest of the normal workflow. `observe` returns DTOs for admin, proxy, firewall and Pathfinder port states, without exposing the firewall solution or admin password. The raw game field portsNeededForCrack is zero-based (3 means four ports required).

Live validation completed proxy overload, firewall analysis/solve, four port crackers, PortHack and proof-file reading in approximately 45 seconds of active gameplay. Tracekill was not used. This does not validate every mod or every client.

Removing this optional DLL disables the adapter; already-saved nodes and tools remain in the player's save. No live saves or test transcripts are included in the repository.
