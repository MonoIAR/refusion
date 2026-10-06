# Refusion Relay Access Data

The relay stores persistent access data in `refusion-server-access.json` in its working directory. Player identifiers are persistent client UUIDs, not Steam IDs or relay connection IDs.

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

The UUID is a stable local identifier, not a cryptographic identity. A player can change it by deleting `refusion-identity.json`; it is intended to deter casual abuse, not resist deliberate impersonation.
