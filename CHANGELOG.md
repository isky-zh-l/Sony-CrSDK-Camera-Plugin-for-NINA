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

