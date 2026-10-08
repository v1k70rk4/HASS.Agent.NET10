# Contributing

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome.

## Questions, bugs and ideas

Open an [issue](https://github.com/v1k70rk4/HASS.Agent.NET10/issues/new/choose) and pick the template that fits. For a bug, the version, the connection mode (MQTT, HA API or both) and the relevant part of the log help the most; the log is in `C:\ProgramData\HASS.Agent.NET10`.

Problems on the Home Assistant side (entities, config flow, services) belong to the [integration](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/issues). Not sure which one? Open it here, it will be moved.

**Security problems are not reported in issues.** See [SECURITY.md](SECURITY.md) for the private way.

## Pull requests

1. Fork the repository and branch off `main`.
2. Keep a pull request to one change. Say what it changes and why; for a bug, how to reproduce it.
3. Open the pull request against `main`. CI builds it and runs the tests, CodeQL scans it, and it gets a review before it is merged.

Before you open it:

- **It builds.** Warnings are errors in this project, so a new warning fails the build.
- **The tests pass:** `dotnet test --project tests/HASS.Agent.NET10.Tests -c Release`.
- **A bug fix comes with a test** that fails without the fix. New logic with an input and an output gets tests in the same pull request. What only Windows itself can show (windows, the tray, audio and display devices) is tested by hand; write in the pull request what you tried and on which Windows version.
- **Text the user sees** goes into both `Localization/Strings.en.cs` and `Localization/Strings.hu.cs`. If you don't speak Hungarian, put the English text in both and say so; it will be translated.
- **A new feature is documented** under [`docs/`](docs/), with one line in the README's feature table.
- **Leave the version numbers and the changelog alone.** They are updated when a release is made.

How to build and run it: [Building and development](docs/development.md).

## License

By contributing you agree that your contribution is released under the project's [MIT License](LICENSE).
