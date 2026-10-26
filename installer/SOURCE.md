# Corresponding source

corresponding-source.zip contains project source and the pinned, unchanged LibRaw source and licenses. The private LibRaw DLL is dynamically linked under its LGPL option; see THIRD-PARTY-NOTICES.md and licenses/.

The project-owned bridge is src/nina/native/SonyArw6.cpp. Extract the source, open an x64 MSVC developer PowerShell, and run tools/Build-Decoder.ps1 to rebuild LibRaw and the bridge. Sony SDK and .NET are not needed for this decoder-only build. Compatible replacements require reviewed native/identity.json hashes.
