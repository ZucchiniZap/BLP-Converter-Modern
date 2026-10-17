# Modern BLP Converter

A cross-platform Avalonia desktop application for converting BLP textures and PNG images.

## Features

- Convert BLP textures to PNG and PNG images to BLP2.
- Choose Raw, DXT1, DXT3, or DXT5 BLP compression and a supported alpha depth.
- Generate mipmaps, with an optional color-key-aware reducer for opaque textures. For each 2x2 block, at least two non-key pixels are averaged while key pixels are ignored; otherwise the output is the selected key color.
- Convert multiple files or recursively scan dropped folders. Batch results are written beside each source using the same filename stem and the other format's extension. Existing counterpart files are overwritten without a prompt. Back up important files before running a batch.
- Configure conversion options in the UI and save preferences locally.

Only PNG and BLP files are converted during batch processing. Unsupported items and linked folders are skipped; conversion and file-system errors appear in the status message.

## Requirements

- .NET 10 SDK to build and run from source.
- Review the Six Labors ImageSharp 4.1.3 split-license terms for your use. Qualifying open-source/source-available software may use ImageSharp under Apache 2.0; other uses require the applicable commercial license. See [third-party licensing](THIRD-PARTY-NOTICES.md).

The checked-in source does not include a Six Labors license file or key. If your license requires a local license file/key for builds, keep it outside the source tree. MSBuild looks for `%APPDATA%\Modern BLP Converter\sixlabors.lic` on Windows, or accepts `SIXLABORS_LICENSE_FILE` / `SIXLABORS_LICENSE_KEY` from the build environment. Do not commit license files or keys.

## Build and run

From the repository root:

```powershell
dotnet restore ".\BLP Converter Modern.slnx"
dotnet build ".\BLP Converter Modern.slnx" --configuration Release
dotnet run --project ".\AvaloniaApplication1\AvaloniaApplication1.csproj"
```

Visual Studio 2026 can also open `BLP Converter Modern.slnx`.

## Publish

Self-contained, single-file publish profiles are provided for Windows x64, Linux x64, macOS x64, and macOS ARM64. For example:

```powershell
dotnet publish ".\AvaloniaApplication1\AvaloniaApplication1.csproj" --configuration Release --property:PublishProfile=win-x64
```

The profiles write to `artifacts/publish/<runtime>/`. Build on a supported SDK host and check the platform-specific third-party license/notice obligations before redistributing a published application.

## Conversion behavior

- Single-file conversion lets you choose its output path.
- Batch conversion writes to each source's directory, changing `.png` to `.blp` or `.blp` to `.png`. Matching existing files are overwritten without confirmation.
- Batch conversion stages results before replacing files so that PNG and BLP files with the same stem in one batch are read before either is overwritten. If a counterpart input fails to convert, it is not overwritten by the other result.

## Licensing

The original code in this repository is offered under the MIT License in [`LICENSE`](LICENSE), except for third-party components identified in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). SereniaBLPLib retains its original MIT copyright and notice in `SereniaBLPLib/LICENSE`. ImageSharp has a separate split license; this repository's MIT license does not relicense ImageSharp or grant a Six Labors commercial license. Review the third-party notices and obtain any license required for your circumstances before use or redistribution.
