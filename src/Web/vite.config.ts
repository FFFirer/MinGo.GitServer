import { defineConfig } from "vite";

export default defineConfig({
  server: {
    host: "127.0.0.1",
    proxy: { "/api": "http://127.0.0.1:5080", "/git": "http://127.0.0.1:5080" },
  },
  build: { outDir: "../Server/wwwroot", emptyOutDir: true },
});
