# Adaptive Bluetooth latency settings

Companion 1.2.0 and firmware 0.10.0 use protocol 1.3. Flash the new UF2 to both halves, exit the previous companion, then launch the matching companion package. Its `layout-manifest.json` must come from the same firmware build.

Open **Adaptive Bluetooth latency** in the companion settings. Changes take effect with **Save and apply**, which reconnects the companion session.

| Setting | Default | Allowed values / behavior |
| --- | --- | --- |
| Enable adaptive latency | On | Off requests the idle/fixed latency regardless of activity. |
| Active latency | 4 | 0–99, no greater than idle latency. |
| Idle/fixed latency | 30 | 0–99, no less than active latency. |
| Idle after | 60 seconds | 15–3600 seconds without Windows input. |
| Minimum time between changes | 30 seconds | 5–300 seconds between firmware parameter requests accepted by the Bluetooth stack. |
| Use idle latency when locked | On | Locking Windows requests idle mode immediately, subject to the minimum time between changes. |

Latency values count connection events the keyboard may skip; they are not milliseconds or a guaranteed input delay. Lower values favor responsiveness; higher values permit more power saving. Windows still negotiates the actual connection parameters.

The firmware skips a parameter request when the current negotiated latency already matches. During the minimum interval it defers changes and uses the latest requested mode. The interval survives companion session reconnects. Failed requests retain the existing bounded retry behavior. Normal companion traffic renews the 90-second active-mode lease; losing the companion eventually returns the keyboard to power saving. Custom settings are sent by the companion and are not stored in keyboard flash.

For a comparison with adaptive switching disabled, turn the feature off and keep idle/fixed latency at 30. Export diagnostics after a drop. Saved settings are included in the export and startup logs record all adaptive settings; firmware history records requested and negotiated connection parameters.

This feature permits investigation of the observed parameter-update-related disconnects. It does not establish that switching caused every drop, and it does not repair Windows Bluetooth driver errors. Offline tests and firmware compilation do not verify real hardware timing, reliability, or battery impact.
