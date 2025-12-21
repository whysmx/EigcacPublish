# VS Extension Requirements

## Goal
Build a single VSIX extension (one package) that supports VS 2019/2022/2026. The extension provides a command button named "发布". When invoked inside a solution that contains the target projects, it pulls latest code from TFS, publishes the backend project `Eigcac.Main`, and publishes the frontend project `Eigcac.BSServer` into a subfolder of the backend publish output.

## Scope
- Target environment: developer machine with .NET SDK installed.
- Projects already mapped locally (TFS workspace exists).
- The user opens the solution to publish; the extension operates on the currently opened solution.

## Behavior
1. **TFS Update**
   - Run `tf get /recursive` in the solution directory before publishing.
   - If TFS update fails, show a MessageBox and stop.

2. **Publish Backend**
   - Run `dotnet publish` for `Eigcac.Main` with the fixed publish profile `ARM64.pubxml`.
   - Publish directory is user-selected; default is the Desktop root directory.
   - Publish command will override PublishDir to the user-selected path.

3. **Publish Frontend**
   - Run `dotnet publish` for `Eigcac.BSServer` with the fixed publish profile `ARM64.pubxml`.
   - Publish to a temporary directory, then copy the output to `<BackendPublishPath>\BSServer`.
   - Clear `<BackendPublishPath>\BSServer` before copying.

4. **Path Selection**
   - On first run, prompt for backend publish path (default initial folder is Desktop root).
   - Persist the chosen path in VS settings.
   - Provide a way to change it (e.g., command uses the stored path but allows change via a dialog or option page).

5. **UI**
   - Single command/button labeled "发布".
   - Errors: show MessageBox only.

## Constraints
- Single VSIX must support VS 2019/2022/2026.
- Fixed publish profile: `ARM64.pubxml`.
- Must clear frontend target folder before copy.
- No extra logs required beyond MessageBox.

## Open Implementation Decisions (pre-approved)
- Use `dotnet publish` for both projects.
- Use `tf get /recursive` for TFS update.

