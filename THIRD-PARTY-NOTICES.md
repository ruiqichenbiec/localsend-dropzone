# Third-party notices

LocalSend Dropzone itself is licensed under the Apache License 2.0 (see [LICENSE](LICENSE) and [NOTICE](NOTICE)).
This file lists the third-party components it uses, with their licences. Versions are the ones referenced by the project files in this repository.

## Redistributed in the self-contained release executable

The release build (`dotnet publish ... --self-contained true`, see [README.md](README.md)) copies these into `LocalSendDropzone.exe`'s publish folder.

| Component | Version | Licence | Source |
|---|---|---|---|
| .NET runtime (Microsoft.NETCore.App) | 10.0.13 runtime pack | MIT | https://dot.net/ |
| Windows Desktop runtime (Microsoft.WindowsDesktop.App: WPF and Windows Forms, used by the tray app) | 10.0.13 runtime pack | MIT | https://dot.net/ |
| ASP.NET Core and Kestrel (Microsoft.AspNetCore.App, `FrameworkReference` in `dropzone-core`) | 10.0.13 runtime pack | MIT | https://asp.net/ |
| System.Security.Cryptography.ProtectedData (`PackageReference` in `dropzone-core`) | 10.0.13 | MIT | https://dot.net/ |

The runtime packs ship their own licence files: `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` for the .NET runtime, `LICENSE.txt` and `THIRD-PARTY-NOTICES.TXT` for ASP.NET Core, and `LICENSE` for the Windows Desktop runtime. Those files carry the full licence texts and the notices of the components they contain.

## Test only (not redistributed)

These are referenced only by `tests/dropzone-core-tests` and are not part of the release executable. Their transitive dependencies are test-only as well.

| Component | Version | Licence |
|---|---|---|
| xUnit (xunit) | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| coverlet.collector | 6.0.4 | MIT |
