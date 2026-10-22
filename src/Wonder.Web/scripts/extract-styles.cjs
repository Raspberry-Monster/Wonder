// Use Ant Design Vue's SSR extraction and cache-path map so client components reuse this stylesheet.
process.env.NODE_ENV = "production";
const fs = require("node:fs");
const path = require("node:path");
const { createSSRApp, defineComponent, h, ref } = require("vue");
const { renderToString } = require("@vue/server-renderer");
const {
  StyleProvider,
  createCache,
  extractStyle,
} = require("ant-design-vue/lib/_util/cssinjs");
const ConfigProvider = require("ant-design-vue/lib/config-provider").default;
const root = path.dirname(require.resolve("ant-design-vue/package.json"));
const components = [
  "alert",
  "breadcrumb",
  "button",
  "card",
  "checkbox",
  "collapse",
  "config-provider",
  "descriptions",
  "divider",
  "empty",
  "form",
  "input",
  "input-number",
  "layout",
  "list",
  "message",
  "modal",
  "notification",
  "pagination",
  "popconfirm",
  "popover",
  "radio",
  "select",
  "space",
  "spin",
  "switch",
  "table",
  "tabs",
  "tag",
  "tooltip",
  "typography",
];
const styles = components.flatMap((name) => {
  const file = path.join(root, "lib", name, "style", "index.js");
  if (!fs.existsSync(file)) return [];
  const hook = require(file).default;
  return typeof hook === "function" ? [{ name, hook }] : [];
});
const cache = createCache();
const Collector = defineComponent({
  setup() {
    for (const { name, hook } of styles)
      hook(ref("ant-" + (name === "button" ? "btn" : name)));
    require("ant-design-vue/lib/_util/wave/style").default(ref("ant-wave"));
    require("ant-design-vue/lib/config-provider/style").default(ref("anticon"));
    const grid = require("ant-design-vue/lib/grid/style");
    grid.useRowStyle(ref("ant-row"));
    grid.useColStyle(ref("ant-col"));
    return () => h("div");
  },
});
(async () => {
  for (const theme of [
    undefined,
    {
      token: {
        colorPrimary: "#28685b",
        borderRadius: 8,
        fontFamily: "Inter, system-ui, sans-serif",
      },
    },
  ]) {
    await renderToString(
      createSSRApp({
        render: () =>
          h(
            ConfigProvider,
            { theme },
            {
              default: () =>
                h(StyleProvider, { cache }, { default: () => h(Collector) }),
            },
          ),
      }),
    );
  }
  fs.mkdirSync("public", { recursive: true });
  fs.writeFileSync("public/antd.css", extractStyle(cache, true));
  console.log(`Extracted ${styles.length} Ant Design Vue component styles.`);
})();
