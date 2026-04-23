# Changelog

## 0.1.1

- Fix client disconnect when the server broadcasted ball-state messages: Mirror clears message
  handlers on shutdown, so re-register via a `NetworkClient.RegisterMessageHandlers` postfix and
  use `ReplaceHandler` to remain idempotent across reconnects.
- Add `Debug.EnableSelfSabotageHotkey` (F9) for single-player verification: while hosting
  locally, pressing F9 sabotages the local player's own ball, bypassing the self-target guard.

## 0.1.0

- Initial Ball Saboteur prototype.
