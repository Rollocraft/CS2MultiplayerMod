# Smart and partial resynchronization

Clients can choose **Smart sync** in the multiplayer panel to check treasury, XP, development points, tourism and weather against the host. Only mismatching channels are returned. The client applies and verifies those values without loading the city again. A successful check covers those five channels; it does not certify roads, buildings or other structural state.

If capture, application or verification fails, or an approved repair receives no acknowledgement within 30 seconds, the host sends a full world snapshot to the affected client. Host-only and approval-required resync policies also apply to smart sync. Smart requests have a five-second cooldown. Fallbacks share the existing full-world recovery rate limit; a repeated failure may need to wait before another world sync.

**Sync World** and `/sync` remain available for structural problems and stream loss. A client request now reloads only that client's world. Other clients still pause at the snapshot barrier, drain pending work and resume together, without downloading or loading the city. Host-initiated recovery reloads all clients. Concurrent targeted requests are combined; requests arriving after a snapshot starts run in a following epoch.

Initial joins always require a world snapshot. Editable settings, paged simulation state, roads, zoning and entity topology are deliberately excluded from lightweight repair because they require ordered processing or a world cut.
