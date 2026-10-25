## 0.5.4-rc.1 (assembly 0.5.4.0; release candidate)

- Share one coordinated camera service between the NINA camera, optional lens focuser and Sony dock/settings UI. Remove camera magnifier UI and route wheel input through the panel.
- Preserve clean connections after recognized correctable preflight failures or local decoding errors; provide recheck, and reject stale-image delivery after failed requests.
- Activate the existing Sony dock behind Image; cancel queued/retrying connection on final shutdown.
- Preserve user receiveRoot on upgrade while refreshing native approvals; regression-test install and rollback.
- A7M5 v2.00: five 1-second multi-RAW frames, one 60-second Bulb frame, plus one daily-host frame; all saved FITS sensor samples match original RAW decoding exactly. Both local NINA installations pass actual dock/wheel checks.
- 553 integration assertions, 32 simulated models, 19 real RAW fixtures, 47 UI assertions and 9 installer cases pass. Other models are not hardware accepted; see docs/VALIDATION.md.
- Separate-computer validation: Windows 11 x64, NINA 3.2.0.9001, SDK 2.02.00 and ILCE-7M5; installation, restart/reconnect, controls and 68 consecutive 1-second FITS pass. APS-C output is 4624x3080 RGGB; file/header checks are not per-frame pixel validation.
- No Sony SDK binaries, photographs or installed native approvals are distributed.

## 0.5.0-dev (assembly 0.5.0.0; not hardware validated)

- Generalize discovery, exact model selection and NINA identity to SDK-enumerated Sony cameras.
- Negotiate shutter/Bulb/LiveView from current device capabilities; support legacy absent optional properties while refusing unreadable/error states.
- Decode Sony single-plane Bayer RAW using the pinned LibRaw; learn active geometry/CFA per frame, without A7M5 metadata on other bodies.
- Handle all SDK 2.02 RAW format tokens, extend manifest inventories, retain ABI/dependency hash validation.
- Add simulated 32-model, legacy Bulb, synthetic Bayer/NINA image pipeline and failure regression tests. No new camera or firmware has been hardware validated.
- Published 0.4.1.3 packages and installed native approvals remain unchanged. See docs/MULTI_CAMERA.md.

## 0.4.1.3

- Show the selected project icon in the GitHub README.
- Install icon.png and use its local URI for the NINA installed-plugin card, including offline use.
- Camera capture and decoding behavior are unchanged.

# Changes

## 0.4.1-rc.2 (assembly 0.4.1.2)

- Set public name to Sony CrSDK Camera Plugin for N.I.N.A., author isky, and update LiveView metadata and reported driver version.
- Default to no extra ARW copy; NINA remains responsible for FITS/XISF output.
- Add a public binary package without Sony SDK files, plus local SDK setup and backup/restore tools.
- Include matching source and LibRaw notices in binary packages.
- Installer regression completed in isolated local directories; this release candidate still needs final camera deployment and second-computer validation.

## 0.4.1-dev.1

- Only report connected after readiness, camera facts and receive-directory setup succeed.
- Disconnect a partially established SDK connection when connection initialization fails, preserving its original error and allowing another attempt.
- Clear camera facts on disconnect; retry incomplete SDK initialization instead of skipping it.

## 0.4.1-dev

- Add `liveViewColor` to settings.json; false produces monochrome JPEG preview without debayering.
- Preserve existing `saveOriginalRaw` behaviour and document RAW-copy configuration.
- Reject `.` and `..` as RAW-copy subfolder names.
- Include default settings.json in camera build output.
- Prepare a source-only repository, explicit external SDK layout, unified build, private local deployment and source export scripts.
- Hardware regression pending; previously tested 0.4.0 binaries remain in the private archive.

