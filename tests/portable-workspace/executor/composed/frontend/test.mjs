import { strict as assert } from "node:assert";
import { render } from "./app.mjs";

assert.equal(render("backend-ok"), "frontend:backend-ok");
console.log("frontend-test-ok");
