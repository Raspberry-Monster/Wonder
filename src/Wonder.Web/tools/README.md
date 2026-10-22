# Frontend compiler tools

The application uses the official `typescript@7.0.2` package. Vue type checking and
OpenAPI generation need the classic compiler API, so this separate package uses
the Vue maintainer's `typescript-native-bridge`, backed by the TS 7.0.2 Go checker.
There is no TypeScript 5 fallback.

The bridge version is pinned exactly as recommended by its maintainer. Its npm
version includes a prerelease suffix even though it is published under `latest`.
See [Vue issue #5381](https://github.com/vuejs/language-tools/issues/5381) and the
[bridge installation instructions](https://github.com/johnsoncodehk/typescript-native-bridge#install).

Run `npm ci` in `Wonder.Web`; its postinstall installs this package from its own
lockfile. Normal `build`, `typecheck`, and `generate:api` scripts route here.
Type checking prints `TNB ACTIVE` when the native bridge is loaded.

The bridge supports Windows and glibc Linux; the Docker build uses
`node:24-bookworm-slim`. Do not switch the build stage to Alpine/musl.
