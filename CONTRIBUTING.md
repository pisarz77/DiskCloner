# Contributing to Disk Cloner

First off, thank you for considering contributing to Disk Cloner!

Because Disk Cloner is a powerful system tool that interacts with raw disk IO, WMI, and Volume Shadow Copy Service, even small changes can have a large impact on system stability and data safety. 

## How Can I Contribute?

### Reporting Bugs
This section guides you through submitting a bug report for Disk Cloner. 
* Use the provided [Bug Report Template](.github/ISSUE_TEMPLATE/bug_report.md).
* **Always attach the log file** (`clone_YYYYMMDD_HHmmss.log`). Without it, diagnosing low-level disk access errors is extremely difficult.
* Clearly state your Windows version and whether BitLocker was active.

### Suggesting Enhancements
Enhancement suggestions are tracked as GitHub issues. Provide a clear description of the enhancement, how it should work, and why it's beneficial. 

### Pull Requests
1. **Fork the repo** and create your branch from `main`.
2. **Discuss first**: For major architectural changes, please open an issue first to discuss the proposed changes.
3. **Write tests**: If you fix a bug or add functionality, ensure existing unit tests pass and add new ones if possible.
4. **Safety considerations**: Any PR that modifies `$namespace:\\.\PhysicalDrive` operations must clearly document why the change was made and what edge cases were considered.
5. Create the PR with a clear summary and link it to any related issues.

## Development Setup

1. Requirements: Windows 10/11, .NET 8.0 SDK, Visual Studio 2022.
2. The project must be run **As Administrator** for debugger attachment when testing live cloning operations.

Thank you for contributing to make Disk Cloner safer and better!
