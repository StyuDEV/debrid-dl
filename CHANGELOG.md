# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-29

### Added

- Multi-connection downloads: each file is split into chunks fetched in parallel.
- Resume after a pause, a crash or a reboot, including files started by the
  former Python downloader (`.telech` + `.telech.json`).
- AllDebrid integration: links are unlocked with the API key, delayed links are
  waited for, and an expired direct link is unlocked again mid-download.
- Clipboard capture of links from hosts supported by AllDebrid.
- Queue saved between sessions, with a limit on simultaneous files.
- Automatic updates from GitHub Releases.

[1.0.0]: https://github.com/StyuDEV/debrid-dl/releases/tag/v1.0.0
