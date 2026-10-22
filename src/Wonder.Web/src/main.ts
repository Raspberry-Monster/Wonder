import { createApp } from "vue";
import { createPinia } from "pinia";
import ArcoVue from "@arco-design/web-vue";
import "@arco-design/web-vue/dist/arco.css";
import "./style.css";
import App from "./App.vue";
import { router } from "./router";
createApp(App).use(createPinia()).use(router).use(ArcoVue).mount("#app");
