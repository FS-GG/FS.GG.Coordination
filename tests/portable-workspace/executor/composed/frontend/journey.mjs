import { strict as assert } from "node:assert";
import { render } from "./app.mjs";

const response = await fetch(process.argv[2]);
assert.equal(response.status, 200);
assert.equal(render(await response.text()), "frontend:backend-ok");
console.log("frontend-backend-journey-ok");
