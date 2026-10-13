# Automatic reconnection

If an established client connection drops unexpectedly, the mod pauses local gameplay and tries the same host again, using the same direct address or Steam relay code and credentials. The connection screen shows progress and **Cancel** stops the retries.

Retries use delays of 1, 2, 4, 8, 16 and 30 seconds, with at most six attempts and a two-minute overall limit. A silent connection attempt times out after 15 seconds; host approval can wait until the overall limit. The host's normal password, compatibility, approval and ban checks still apply. After reconnecting, the client downloads a fresh authoritative city before gameplay resumes.

Deliberate disconnects, host shutdown notices, kicks, bans, rejected handshakes and protocol violations do not retry. A failed initial join also stays failed. When reconnection is cancelled or exhausted, the usual session exit returns the client to the main menu.
