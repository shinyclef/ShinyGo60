# Step 14 Windows experience

Status: implementation complete; physical taskbar-child acceptance in progress

Recorded: 2026-09-03 on Windows 11 with Start11 Taskbar Enhancer enabled

## Goal

Turn the Step 13 background companion into an ordinary Windows application: editable shortcut mappings, live status, optional background startup, and an
at-a-glance status widget at the far left of the taskbar. No firmware change, Docker operation, rebuild, or flash is required.

## Configuration application

The WPF settings window resolves every mapping against the manifest used by the flashed firmware. It supports adding and removing mappings, capturing a real
mouse-generated or keyboard gesture with modifiers, choosing momentary or persistent layer behavior, choosing a target layer, and preferring USB, Bluetooth,
or automatic transport selection. Save and apply writes the JSON atomically, restarts the runtime in-process, and reports success or failure without requiring
an application restart.

The start-with-Windows option writes an exact command under the current user's Run key. Its command identifies the executable, starts it with `--background`,
and supplies the current manifest and settings paths. Background startup creates the taskbar widget without opening the settings window. A named process mutex
and event keep one companion instance while allowing a second launch to open the existing instance's settings.

## Taskbar hosting

The widget is a WPF window whose HWND becomes a child of the primary Windows 11 `Shell_TrayWnd` through `SetParent`. Its popup window style is replaced with the
child style, and its physical-pixel bounds are calculated relative to the taskbar client area at that taskbar's DPI. It retains no-activate and tool-window
styles, so clicking the widget can request settings without taking focus merely because its status changes.

This replaces the discarded prototype that placed a topmost borderless window at screen coordinates and polled the foreground window for fullscreen state.
As a taskbar child, the widget inherits Explorer's visibility and z-order: fullscreen applications and taskbar visibility do not need to be detected by the
companion. A one-second maintenance check only finds taskbar replacement after Explorer restarts and reapplies attachment and placement. Keyboard status itself
continues to update from companion events.

This hosting method is an established but unofficial Windows shell technique. Failure to find or attach to the taskbar hides the widget and records one
structured warning rather than leaving a desktop overlay. It requires neither a driver nor administrator access; WinRing0 and hardware-monitor libraries are
not part of ShinyGo60.

## Display contract

The widget presents:

- the manifest-resolved effective layer as the primary value;
- `CURRENT`, `STALE`, or `SEARCHING` connection state with distinct colour;
- the current transport when available; and
- separate left and right battery values, with an asterisk on stale readings.

USB-powered battery values may saturate and are explicitly shown as stale where appropriate. Accurate battery reporting is required only for Bluetooth, as
decided after Step 11.

## Automated verification

The complete Release solution builds with zero warnings and errors. All thirteen offline suites pass. Step 14-specific checks cover current, stale, and
disconnected presentation plus taskbar-relative placement for normal DPI, scaled DPI, horizontal, vertical, and unavailable taskbar geometry.

An inspection of the running Windows build confirmed:

- companion process ID `27680` owns the widget HWND;
- the widget parent and the current `Shell_TrayWnd` are the same handle;
- Windows reports the child at taskbar-relative position `(5,5)`; and
- the rendered window is `252 x 38` physical pixels at the tested DPI.

## Physical verification

The configuration and initial widget prototype already passed these checks before the hosting change:

- current layer, USB/Bluetooth transport, and left/right battery presentation;
- stale transition when Bluetooth is disabled and current-state recovery when re-enabled;
- real G502 F23 capture, including Alt, Ctrl, and Shift modifiers;
- live persistent and momentary mapping changes followed by restoration to F23 momentary Navigation;
- momentary Navigation while held and Home after release over USB and Bluetooth;
- settings reopening through the focusless widget without stealing focus during background status updates;
- background startup with settings closed;
- single-instance activation and clean shutdown without diagnostic-log contention; and
- recovery after an Explorer restart with Start11 enabled.

The final taskbar-child build still requires a short visual regression pass for initial launch during fullscreen video, fullscreen show/hide, lost focus while
fullscreen remains active, widget click, and Explorer restart. Windows sleep remains deferred because this PC does not enter sleep reliably. Multi-monitor and
nonstandard taskbar-edge policy remain later acceptance work; version one targets the primary Windows 11 taskbar.

Step 14 also exposed an earlier firmware precedence defect in Go to layer: a companion persistent target prevented a physical `&to Home` binding from taking
effect. The corrected matched UF2 is `Output/ShinyGo60-20260902-155232-3fd12c2c/ShinyGo60-3fd12c2c.uf2`. After flashing both halves, its focused regression test
must select a non-Home layer through the companion and then return through the layout's physical `&to Home` key over USB and Bluetooth.
