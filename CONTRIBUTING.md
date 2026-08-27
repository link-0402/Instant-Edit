# Contributing

Thank you for helping improve XIV Instant Edit. Bug reports, feature requests, and
focused pull requests are welcome.

## Reporting issues

Please use [GitHub Issues](https://github.com/link-0402/XIV-Instant-Edit/issues).
Include:

- the XIV Instant Edit version;
- Blender, FFXIV, Dalamud, and Penumbra versions;
- clear steps to reproduce the problem;
- relevant error messages or logs; and
- a description of the expected and actual behavior.

Remove personal paths, account information, and other sensitive data from logs
before sharing them.

## Development setup

- Blender 4.5 or newer is required for add-on development. Continuous
  integration currently exercises Blender 4.5.12 and 5.2.0.
- Plugin development requires the .NET 10 SDK and a local Dalamud development
  installation.
- The add-on source is under `Blender-Addon`; the Dalamud plugin source is
  under `Dalamud-Plugin`.

## Tests and pull requests

Run the regression commands in
`.github/workflows/blender-extension-repository.yml` for add-on changes. For
plugin changes, build the Release configuration and run the relevant tests
under `Tests`.

Keep pull requests focused, explain behavior changes, and update the README or
third-party notices when user-facing behavior or attribution changes. Release
archives and the Blender repository index should be regenerated together with
`scripts/prepare-release.ps1` only when preparing a release.
