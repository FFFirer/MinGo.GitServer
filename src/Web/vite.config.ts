import { defineConfig } from "vite";

export default defineConfig({
  server: {
    host: "127.0.0.1",
    strictPort: true,
  },
  build: { outDir: "../Server/wwwroot", emptyOutDir: true },
});
