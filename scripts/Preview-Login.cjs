// Local visual check only. Serves two generated fixture pages; never reads account data.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..', 'tests', 'bin', 'Release', 'net10.0-windows10.0.19041.0', 'login-previews');
const pages = { '/success': fs.readFileSync(path.join(root, 'success.html')), '/failure': fs.readFileSync(path.join(root, 'failure.html')) };
const server = http.createServer((request, response) => {
  const page = pages[request.url];
  response.writeHead(page ? 200 : 404, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'none'; img-src data:; style-src 'unsafe-inline'" });
  response.end(page || 'Not found');
});
server.listen(0, '127.0.0.1', () => console.log(`Preview: http://127.0.0.1:${server.address().port}/success`));
setTimeout(() => server.close(), 120000);
