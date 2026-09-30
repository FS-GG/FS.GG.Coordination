declare const process: { argv: string[] };

function render(message: string): string {
  return `frontend:${message}`;
}

const response = await fetch(process.argv[2]);
if (response.status !== 200) throw new Error(`unexpected status ${response.status}`);
const rendered = render(await response.text());
if (rendered !== "frontend:backend-ok") throw new Error(`unexpected body ${rendered}`);
console.log("frontend-backend-journey-ok");

export {};
