# Contributing to OpenVersus

Thanks for your interest in contributing to OpenVersus! This project is community-maintained and we welcome contributions of all kinds.

## Getting Started

- Install the .NET 10 SDK and Docker (the test suites use throwaway Redis and MongoDB containers; see `test.sh`)
- Fork the repository and clone it locally
- Check if any open Issues need work before starting something new

## How to Contribute

**Reporting Bugs**

Open an Issue on GitHub with a clear description of the problem, steps to reproduce it, and your environment details.

**Suggesting Features**

Open an issue tagged `enhancement` and describe what you'd like to see, and why/how it would benefit the community.

**Submitting Code**
1. Fork the repo and create a new branch from the default branch of the repo. This will typically be `main`, `openversus`, or similar. Please check the the specific repo to determine the default branch name.
2. Make your changes
3. *Test your changes locally before submitting*
4. Open a pull request with a clear description of what you changed and why

## Code Style

The server is C# (.NET 10). Keep your code consistent with the existing style and the repository's `.editorconfig`; `dotnet format` applies it. The TypeScript server under `legacy/reference/ts_server/` is a frozen reference: changes to it are not accepted.

## Questions

If you're unsure about anything, please open an issue or reach out in the community Discord server before spending time on a large change.
