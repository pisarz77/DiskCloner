# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.2] - 2026-03-08

### Added
- Professional assembly metadata to `DiskCloner.UI` and `DiskCloner.Core`.
- Custom-designed, minimalist application icon embedded in the executable.
- GitHub Actions CI/CD pipeline for automated builds, testing, and releases.
- Structured bug report template and `CONTRIBUTING.md` guide.
- Comprehensive `README.md` with status badges and usage documentation.

### Fixed
- Resolved `CS8625` nullable reference type warnings in `CloneEngineSafetyTests.cs` to ensure clean builds in CI environments.
- Corrected various path and renaming artifacts after the repository reorganization.

## [1.0.0] - 2026-03-08

### Initial Release
- Core disk cloning functionality with VSS (Volume Shadow Copy) support.
- Live system disk cloning for Windows 10 and 11.
- Partition management and automatic expansion for target drives.
- SHA-256 data integrity verification.
- WPF-based user interface with real-time progress and throughput monitoring.
