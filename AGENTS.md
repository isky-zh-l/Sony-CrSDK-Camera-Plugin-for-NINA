# Repository guidance

## Start with the requested task

- For installing the released plugin, read [docs/INSTALL.md](docs/INSTALL.md). Use the binary ZIP from the requested GitHub Release. Source compilation is not required for installation.
- For code changes, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [CONTRIBUTING.md](CONTRIBUTING.md) and the relevant module. Use the root `build.ps1`.
- Current prerelease: 0.5.4-rc.1; assembly 0.5.4.0; target Windows x64, N.I.N.A. 3.2.0.9001 and Sony Camera Remote SDK 2.02.00. See [docs/VALIDATION.md](docs/VALIDATION.md) for verified scope and open validation work.

## Architecture and source ownership

- C++ C ABI wrapper: `work/cpp-wrapper-step1-20261023`.
- Generic C# P/Invoke client: `work/csharp-client`; it must not depend on N.I.N.A. or WPF.
- N.I.N.A. adapter, shared service, controls and optional lens focuser: `work/nina-plugin`.
- `work/` contains build inputs, not disposable scratch files. Keep those paths unless intentionally updating all build references.
- Camera, controls and focuser share `SonyCameraService.Shared`. Do not hold its coordinator while waiting for an exposure; cancellation must remain possible.

## Dependencies and deployment

- Sony SDK, N.I.N.A., drivers and real photographs are not distributed in this repository. Obtain them through their official channels; the user obtains and accepts the Sony SDK license.
- SDK development inputs belong in ignored `deps/` or explicitly supplied external paths. Generated output belongs in ignored `artifacts/`.
- Keep dependency/source locks, approved hashes, PE/ABI checks and test-backend isolation. Never bypass them to make a different SDK or rebuilt library load.
- Install the complete matching release package. Do not mix new managed DLLs with old wrapper/decoder DLLs. The installer prepares native approval from the package and user-supplied SDK.
- Preserve existing user settings, receive directories and installer receipts. A differing shared CrAdapter must be investigated, not silently overwritten.

## Camera and image correctness

- Installing, building, opening controls and reading this file are not authorization to trigger an exposure. Camera operations require the user's explicit testing request.
- Only A7M5 firmware 2.00 has current scoped hardware evidence. Thirty-two simulated models and offline real RAW fixtures do not establish other-camera USB compatibility.
- Keep raw sensor samples, active geometry and CFA tied to the actual frame; do not substitute JPEG, debayered RGB or another body's dimensions.
- Correctable preflight settings can retain a clean connection. SDK/USB errors, unconfirmed writes or uncertain captures must remain explicit failures.
- After rejecting a new capture, never deliver a previous successful frame. Never recover an unidentified image by selecting the last file in a directory.
- Lens focusing remains experimental. Camera magnifier writes are unsupported: the required LiveView coordinate protocol has not been implemented and validated.

## Verification and publication

- Select checks for the change using `docs/VALIDATION.md`. Distinguish static review, build, simulated transport, real RAW decoding, host UI and real camera tests in reports.
- A separate Windows 11 computer has passed deployment and basic A7M5 imaging checks. Real unplug recovery, long sessions and autofocus accuracy remain outside that result; see docs/VALIDATION.md.
- Keep public README concise; use CHANGELOG and release notes for version changes. Exclude local AI task reports, chat handoffs, private paths, deployment logs, credentials, caches and photographs from staged files and nested archives.
- Keep this AGENTS.md public and current. Record reusable engineering constraints here, not personal machine state, conversation history or old permissions.
- Preserve project MIT and third-party licenses and matching LibRaw source. Source ZIP and embedded corresponding-source.zip must match the released source snapshot; update integrity manifests and SHA256SUMS whenever package contents change.
