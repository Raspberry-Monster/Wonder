import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
export default defineConfig({
  plugins: [
    vue(),
    {
      name: "antd-style-nonce",
      enforce: "pre",
      transform(code, id) {
        // Ant 4.2.6's scroll locker does not forward ConfigProvider.csp.
        // Attach the host nonce at the library's style creation point, before insertion.
        if (
          !id
            .replaceAll("\\", "/")
            .endsWith("/ant-design-vue/es/vc-util/Dom/dynamicCSS.js")
        )
          return;
        const marker = "const styleNode = document.createElement('style');";
        if (!code.includes(marker))
          throw new Error(
            "Review Ant Design Vue CSP integration after upgrading.",
          );
        return code.replace(
          marker,
          marker +
            "\n  styleNode.nonce = document.querySelector('meta[name=\"csp-nonce\"]')?.content || ''; ",
        );
      },
    },
  ],
  server: {
    proxy: {
      "/api": "http://localhost:5080",
      "/openapi": "http://localhost:5080",
    },
  },
  build: { outDir: "dist", chunkSizeWarningLimit: 1800 },
});
