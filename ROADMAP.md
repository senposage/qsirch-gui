# Future Work

## Release Automation And Updates

- Publish tagged WPF releases through GitHub Actions.
- Attach the portable Windows package, checksum, and release notes to GitHub Releases.
- Generate a small release manifest containing the version, package URL, checksum, and minimum supported launcher version.
- Add a stable launcher that reads the manifest, starts the selected immutable release folder, and supports a simple rollback.
- Keep user configuration, logs, and saved data outside versioned release folders.
- Remove retired release folders only after no running session can use them.

This should remain share-friendly and should not require a central server process. A server is only needed later if centrally managed policy, reporting, or push notifications become useful.
