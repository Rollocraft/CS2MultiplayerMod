Run `dotnet run --project CS2MultiplayerMod.ResyncTests` from the repository root.

This harness exercises the production resync arbiter, retained retry lifecycle,
growable command codec, and growable inbox without launching the game. It covers
late recovery cancelling a reload, persistent failure expiring once, superseded
buildings, snapshot resets, authoritative road connection flags, and ordering of
road connection transitions.

Native building integration and railway connector matching still need a live
host/client session; the harness does not simulate Unity's native ECS pipeline.
