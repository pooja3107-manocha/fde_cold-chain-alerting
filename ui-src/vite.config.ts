import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// `npm run build` writes straight into the ASP.NET app's wwwroot, so one `dotnet run` serves API + UI.
// `npm run dev` serves the UI on :5173 with hot reload and forwards /api to the running dashboard API.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/ColdChain.Dashboard/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: { "/api": "http://localhost:5080" },
  },
});
