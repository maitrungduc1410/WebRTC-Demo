import { readFileSync } from "node:fs";
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";

// The MediaPipe WASM comes from a CDN and has to match the JS of the installed package.
const mediapipe = JSON.parse(
  readFileSync(new URL("./node_modules/@mediapipe/tasks-vision/package.json", import.meta.url), "utf8"),
);

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  define: {
    __MEDIAPIPE_VERSION__: JSON.stringify(mediapipe.version),
  },
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    fs: {
      // The backgrounds and stickers live in ../effects, shared with the mobile apps.
      allow: [fileURLToPath(new URL(".", import.meta.url)), fileURLToPath(new URL("../effects", import.meta.url))],
    },
  },
});
