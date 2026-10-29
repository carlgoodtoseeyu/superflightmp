# Releasing

1. Make sure `main` is clean and both test suites pass on the private copies:
   `tests/run_two.py` and `tests/run_portal.py` (direct IP), `tests/run_steam.py` (Steam, needs
   the Steam client signed in).
2. Bump `Plugin.Version` in `src/Plugin.cs` and the version line in `INSTALL.txt`. Commit.
3. Build from the committed source so `BuildInfo.Commit` matches the tag:
   `.\build.ps1 -GameDir <private copy>`. The log line at start shows the commit.
4. Package `bin\SuperflightCoop.dll`, `README.md`, `LICENSE` and `INSTALL.txt` as
   `SuperflightCoop-<version>.zip`. Never ship the `.pdb`: it carries local source paths.
5. `sha256sum SuperflightCoop-<version>.zip` and put the hash in the release notes.
6. `git tag v<version>`, push the tag, create the GitHub release with the zip and the hash.
7. Update the coop-anything directory entry (`data/directory.yaml`) with the release link.
