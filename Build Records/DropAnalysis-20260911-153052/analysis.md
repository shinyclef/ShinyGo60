# Go60 drop — 11 September 2026, 15:29 JST

Sources: `Go60-diagnostics-20260911-153052.zip` and the Windows System Bluetooth events saved alongside this report. All wall-clock times below are Japan time.

## What happened

- Companion transport loss: **15:29:09.729**. Validated Bluetooth session restored: **15:30:43.060**, approximately **93.3 seconds** later. Keyboard input could have recovered before the companion; this measures companion downtime.
- Firmware **0.10.1** is running, with critical capacity **224** and routine capacity **128**. The recorder captured critical sequences **1–63**, with no gaps. Collection after recovery is marked complete. The initiating event is available this time, although this burst did not exceed even the previous 64-event critical capacity and therefore does not independently stress-test protected retention.
- Initial critical event: host-link disconnect, result **40 / 0x28 (Instant Passed)**, firmware uptime **2,996,579 ms**. Its estimated occurrence bounds are **15:29:05.080–15:29:11.266**. The exporter currently labels this known code “unrecognized HCI code”; its raw value is intact.
- Next came **31 security failures (9, unspecified)** and **31 disconnects (62 / 0x3E, connection establishment failed)**. These describe recovery attempts within the outage, not 31 separate user-visible drops.
- The left boot ID remains **24EBF1AF** before and after the incident: no evidence of a left-half reboot.
- Windows logged **six BTHUSB event 5 errors**, 15:29:11.780–15:29:29.811, reporting an HCI event of unexpected size. At 15:30:38.616, BTHUSB event 18 warned that authentication keys could not be stored on the adapter for pre-OS keyboard support. That warning alone does not establish why the connection dropped or prove pairing keys were lost.

## Latency and interpretation

The last logged settings remain adaptive **enabled**, active **4**, idle **30**, idle timeout **60 seconds**, minimum switch interval **30 seconds**, idle on lock **enabled**. A fixed-latency comparison has therefore not been established by this export.

At approximately 15:27:52 the firmware's interactive lease expired and it requested latency 30. The update succeeded at uptime **2,923,539 ms**. The first disconnect followed **73,040 ms later**, at latency **30**, interval **15 ms**, supervision timeout **4 seconds**. There are no missing firmware records or intervening recorded parameter changes in this interval. This differs from the earlier 19–35 ms update-to-disconnect timing.

The Bluetooth specification defines Instant Passed for a received link-control procedure whose scheduled connection-event instant is already in the past:
https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-61/out/en/low-energy-controller/link-layer-specification.html

This identifies a link-layer timing failure, but not the exact control procedure or which endpoint is at fault. A host-initiated procedure that never completed would not necessarily appear as a successful parameter update in these logs. The observed sequence connects an initial Instant Passed failure with the familiar recovery/driver-error burst; it does not prove that the companion's adaptive switch caused the failure, or that the Windows driver initiated it.

## Recommended next step

Disable adaptive switching, leave idle/fixed latency at **30**, and choose **Save and apply**. No firmware reflash is needed for that comparison. Keep other settings unchanged and export again if a drop recurs. A recurrence under fixed latency would weaken the switching explanation and justify deeper host/controller investigation; a quiet period alone is not proof of a fix.

Useful follow-up diagnostic improvements are naming 0x28 correctly in the decoder and, if fixed-latency drops persist, capturing lower-level host/controller traces to identify the specific failed link-control procedure. No settings or code were changed during this analysis.
