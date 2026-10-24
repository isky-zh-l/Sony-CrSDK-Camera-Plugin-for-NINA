# Third-party components

The root MIT license covers project-owned code only.

- **Sony Camera Remote SDK**: external proprietary dependency, obtained by each user from https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement_d/en.html . No Sony SDK header, import library, runtime, sample source or driver is shipped in this repository or its source export. Local build/deployment outputs can contain SDK files and are excluded from public exports.
- **LibRaw**: the pinned corresponding source is included in `third_party/LibRaw`, unchanged. Its own COPYRIGHT and LICENSE files apply, including its LGPL/CDDL licensing options; the root MIT license does not relicense it. `work/nina-plugin/dependencies/libraw-source-lock.json` records source identity. `SonyArw6.cpp` is a project-owned bridge dynamically linked to the private LibRaw DLL. When distributing a LibRaw binary separately, retain the applicable notices and provide corresponding source and build information under the chosen upstream license.
- **N.I.N.A.**: external host, not bundled. Build references use a user-supplied NINA installation. Upstream host and API license notices remain applicable.
- **.NET / NuGet packages**: restored from the package sources in NuGet.Config. See project PackageReference entries and resolved package metadata for their licenses; package caches and binaries are excluded from source export.

This project is independently maintained and is not endorsed by Sony or the NINA project. No icons, endorsements or branding rights are implied.
