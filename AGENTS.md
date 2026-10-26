# Repository conventions

- Current official release: 0.5.4, assembly 0.5.4.0. The GitHub release is stable/latest.
- Install from the release ZIP using docs/INSTALL.md. Build from source using docs/BUILD.md and root build.ps1.
- Architecture: src/native (C++ C ABI), src/managed (generic C# P/Invoke), src/nina (NINA adapter). Generic client code must not depend on NINA or WPF.
- Camera, controls and focuser share SonyCameraService.Shared. Never hold the coordinator while waiting for an exposure; cancellation needs access.
- Keep dependency hashes, source locks, ABI/PE checks and test-backend isolation. Never bypass validation or mix managed/native versions to make a package load.
- Preserve capture identity, raw Bayer samples and confirmed camera state. Reject stale frames and unconfirmed SDK/USB operations.
- Scope hardware claims using docs/COMPATIBILITY.md. Simulator and offline RAW checks do not establish another camera's USB compatibility.
- Keep credentials, SDK files, photographs, machine configuration, local approvals and task reports outside Git. Use ignored work/ or artifacts/ for local output.
- Keep README concise; use docs/ for maintained instructions and CHANGELOG for public release changes. Do not commit conversation handoffs or personal testing arrangements.
- Preserve project and third-party licenses and corresponding LibRaw source. Inspect nested archives as well as staged files before publication.
- Installing, building or opening settings does not authorize camera operations; these must be explicitly requested by the operator.
