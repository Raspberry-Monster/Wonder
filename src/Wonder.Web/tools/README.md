# Frontend compiler tools

The application uses the official `typescript@7.0.2` package. Vue type checking and
OpenAPI generation need the classic compiler API, so this separate package uses
the Vue maintainer's `typescript-native-bridge`, backed by the TS 7.0.2 Go checker.
The bridge dependency is pinned in this package and its lockfile. See the
[bridge instructions](https://github.com/johnsoncodehk/typescript-native-bridge#install)
for its compiler API integration.

Run `npm ci` in `Wonder.Web`; its postinstall installs this package from its own
lockfile. Normal `build`, `typecheck`, and `generate:api` scripts route here.
Type checking prints `TNB ACTIVE` when the native bridge is loaded.

The bridge supports Windows and glibc Linux; the Docker build uses
`node:24-bookworm-slim`. Do not switch the build stage to Alpine/musl.
