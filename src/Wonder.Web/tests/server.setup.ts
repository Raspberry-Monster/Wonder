import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { setTimeout } from "node:timers/promises";
import type { FullConfig } from "@playwright/test";

export default async function setup(config: FullConfig) {
  const baseURL = String(config.projects[0].use.baseURL);
  const dev = baseURL.endsWith(":5190");
  const webRoot = fileURLToPath(new URL("../", import.meta.url));
  const hostRoot = fileURLToPath(
    new URL("../../../tests/Wonder.BrowserHost/", import.meta.url),
  );
  const probe = dev ? baseURL : baseURL + "/health/live";
  const available = () =>
    fetch(probe)
      .then((r) => r.ok)
      .catch(() => false);
  if (await available())
    throw new Error(`Test port is already in use: ${baseURL}`);
  const server = spawn(
    dev ? process.execPath : "dotnet",
    dev
      ? [
          fileURLToPath(
            new URL("../node_modules/vite/bin/vite.js", import.meta.url),
          ),
          "--host",
          "127.0.0.1",
          "--port",
          "5190",
          "--strictPort",
        ]
      : [
          hostRoot + "bin/Debug/net10.0/Wonder.BrowserHost.dll",
          "--StaticRoot",
          webRoot + "dist",
          "--urls",
          baseURL,
        ],
    {
      cwd: dev ? webRoot : hostRoot,
      windowsHide: true,
      stdio: "ignore",
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "BrowserTest",
        Workers__Enabled: "false",
        ConnectionStrings__Postgres:
          "Host=127.0.0.1;Port=1;Database=wonder_browser_test;Username=unused;Password=unused",
      },
    },
  );
  let failure: Error | undefined;
  server.on("error", (error) => {
    failure = error;
  });
  const cleanup = () => {
    if (server.exitCode === null) server.kill("SIGKILL");
  };
  for (let attempt = 0; attempt < 100; attempt++) {
    if (failure || server.exitCode !== null) {
      cleanup();
      throw failure ?? new Error("Test server exited during startup.");
    }
    if (await available()) return cleanup;
    await setTimeout(100);
  }
  cleanup();
  throw new Error("Test server did not become ready.");
}
