# DiskCloner Review Implementation Plan

This document lists prioritized fixes and implementation tasks derived from the code review of DiskCloner.Core services.

## Summary
- Address correctness and safety issues in cloning engine, disk enumerator, VSS service, and logging.
- Prioritize fixes that can cause data loss or incorrect verification.
- Make small, testable changes and add unit tests for parsing/hashing logic.

## Prioritized Tasks
1. Fix SHA256 hashing bug (high) — ensure independent hash objects for source and target reads.
2. Fix sector-aligned write padding (high) — avoid writing stale/overrun bytes when rounding to sector sizes.
3. Add unit tests for hashing and copy code (high) — cover `ComputeHashAsync`, sampling, and padding behavior.
4. Update VSS API or add adapter (medium) — align `VssSnapshotService` API with tests or vice versa.
5. Harden DiskPart parsing & add tests (medium) — make `ParsePartitionTableFromDiskPartOutput` robust to locale and variations.
6. Improve partition mapping safety (high) — avoid mis-mapping partitions; add sanity checks and early aborts if mapping confidence is low.
7. Use DeviceIoControl or SetupAPI to determine partition style (MBR/GPT) reliably (medium).
8. Propagate cancellation tokens into blocking tasks and long DeviceIoControl calls (low-medium).
9. Refactor unmanaged buffer handling to use safer wrappers and ensure no leaks (low).
10. Add elevation/privilege checks and clearer errors for admin-only operations (medium).
11. Harden external process handling (diskpart/manage-bde): null-checks, timeouts, output logging (low).
12. Improve `FileLogger` (optionally append vs overwrite, thread-safety on dispose) (low).
13. Run static analysis and format the codebase (low).
14. Run full test suite and fix regressions (high after changes).
15. Document changes and create a PR with description and testing notes (high).

## Quick Implementation Notes
- Make minimal, focused changes per commit (one logical fix per commit).
- Add unit tests for `ParseSizeToBytes`, `NormalizeDiskPartType`, `ComputeHashAsync` behavior, and diskpart parsing edge cases.
- Where native calls are used, add detailed logging and safe fallbacks.

## Estimates (rough)
- SHA hashing fix + tests: 1–2 hours
- Write-padding fix + tests: 2–4 hours
- VSS adapter or test updates: 1–3 hours
- DiskPart parsing hardening + tests: 2–5 hours
- Other items: incremental, 1–3 hours each

## Next Steps
- Implement SHA256 fix and run unit tests (now).
- Then implement write-padding fix and add tests.

---

Saved TODO mapping:
- See the in-repo task tracker (todo list) for live statuses.
