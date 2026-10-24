# Working conventions

- Active architecture: C++ C ABI -> generic C# P/Invoke client -> NINA adapter.
- Independent desktop and console tools share the generic client; do not introduce NINA dependencies there.
- Never commit Sony SDK headers, libraries, binaries, hosts, photographs, local configuration or build caches.
- External development inputs live in ignored deps/ or paths explicitly supplied to scripts.
- Use root build.ps1. Hardware tests and camera operations must be explicitly requested.
- 0.4.0 is the previously hardware-tested baseline; 0.4.1-dev adds preview configuration and needs hardware regression.
- Do not disable runtime hash validation to make rebuilt binaries load. Prepare a separate reviewed local deployment.
- Do not claim a build, fake test or source review constitutes hardware acceptance.
- Root MIT license applies to project-owned code; third_party/LibRaw retains its own licenses.
