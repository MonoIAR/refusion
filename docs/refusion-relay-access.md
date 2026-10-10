# Refusion Relay Access Data

The relay stores persistent access data in `refusion-server-access.json` next to the relay executable (`AppContext.BaseDirectory`), together with `refusion-server-state.json` and `refusion-server-settings.json`. Player identifiers are persistent client UUIDs, not Steam IDs or relay connection IDs.

Older builds stored these files in the process working directory. Move any existing files next to the executable after upgrading, otherwise bans and operators appear to be lost.

The relay listens on UDP port 28430 by default. Set `"port"` in `refusion-server-settings.json` (1-65535) to change it; a port passed as the first command-line argument overrides the file. The port is read once at startup.

Set `"statusPort"` (1-65535) in `refusion-server-settings.json` to enable a read-only HTTP status endpoint at `GET /status` (also served at `/`); `0` or omitted keeps it disabled, and the port is read once at startup. If `"statusToken"` is set, requests must carry `?token=<value>` or they receive `401 Unauthorized`. The JSON contains the relay's transport-level view (server info, connected sessions with endpoints, operators, bans) and `lobby`: the pseudo-host's last reported lobby state, including the host's own player list and player count — useful for spotting a divergence between the relay's sessions and the in-game player list. The endpoint is read-only and never mutates game state.

```json
{
  "statusPort": 28431,
  "statusToken": "change-me"
}
```

```json
{
  "operators": [
    {
      "playerId": "00000000-0000-0000-0000-000000000000"
    }
  ],
  "bans": [
    {
      "playerId": "00000000-0000-0000-0000-000000000000",
      "reason": "Example reason",
      "createdUtc": "2026-01-01T00:00:00Z"
    }
  ]
}
```

Available relay console commands:

- `players`
- `op <uuid>`
- `deop <uuid>`
- `ops`
- `ban <uuid> [reason]`
- `unban <uuid>`
- `bans`

Ban records are enforced during the relay Hello handshake and currently connected matches are removed immediately. Operator records are returned in the relay Welcome and state packets. Fusion's in-game permission UI and commands do not consume that operator state yet.

Protocol version 2 adds a per-session `SessionToken`: the relay issues it in `Welcome` and rejects any post-Hello packet that does not echo it. `Hello` and `Query` stay token-free.

The UUID is a stable local identifier, not a cryptographic identity. A player can change it by deleting `refusion-identity.json`; it is intended to deter casual abuse, not resist deliberate impersonation.
