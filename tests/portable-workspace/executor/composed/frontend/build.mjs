import { mkdir, copyFile } from "node:fs/promises";

await mkdir("artifact", { recursive: true });
await copyFile("app.mjs", "artifact/app.mjs");
console.log("frontend-build-ok");
