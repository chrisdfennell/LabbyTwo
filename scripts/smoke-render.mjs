// Opens one LabbyTwo page in headless Chrome and waits for its cards to draw.
//
//   node scripts/smoke-render.mjs <chrome> <url> <profile dir> <seconds>
//
// Part of smoke-boot.sh, which explains why. The short version: the server's first answer
// is a grid of skeletons, and the cards only draw once a browser has connected its
// circuit — so the only way to know a card renders, rather than throws or blocks, is to
// be a browser. Chrome's --dump-dom cannot be used for this: it returns as soon as the
// page has loaded, which is before the circuit has connected, and --virtual-time-budget
// does not wait on a WebSocket. So this drives Chrome over its DevTools protocol instead,
// with nothing to install: Node 22 has a WebSocket client built in.
//
// Exits 0 when every card has drawn and none failed, 1 when a card failed or Blazor
// reported an error, and 2 when the deadline passed with cards still waiting.

import { spawn } from 'node:child_process';

const [chrome, url, profile, seconds = '30'] = process.argv.slice(2);
if (!chrome || !url || !profile) {
  console.error('usage: smoke-render.mjs <chrome> <url> <profile dir> <seconds>');
  process.exit(64);
}
const deadline = Date.now() + Number(seconds) * 1000;

const browser = spawn(chrome, [
  '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run',
  '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

// Whatever happens, the browser goes too. A stray Chrome would keep the CI step open.
const finish = (code, message) => {
  if (message) console.log(message);
  try { browser.kill('SIGKILL'); } catch { /* already gone */ }
  process.exit(code);
};
setTimeout(() => finish(2, `Gave up after ${seconds}s.`), Number(seconds) * 1000 + 5000).unref();

// Chrome picks a free port and says which on stderr.
const endpoint = await new Promise((resolve, reject) => {
  let seen = '';
  browser.stderr.on('data', chunk => {
    seen += chunk;
    const match = seen.match(/DevTools listening on (ws:\/\/\S+)/);
    if (match) resolve(match[1]);
  });
  browser.on('exit', code => reject(new Error(`Chrome exited (${code}) before it listened:\n${seen}`)));
}).catch(error => finish(1, error.message));

const socket = new WebSocket(endpoint);
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = () => reject(new Error('Could not connect to Chrome.'));
}).catch(error => finish(1, error.message));

let nextId = 0;
const pending = new Map();
const problems = [];

socket.onmessage = event => {
  const message = JSON.parse(event.data);
  if (message.id !== undefined && pending.has(message.id)) {
    const { resolve, reject } = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) reject(new Error(message.error.message));
    else resolve(message.result);
    return;
  }
  // Errors in the page itself, which is where a circuit that failed says so.
  if (message.method === 'Runtime.exceptionThrown') {
    problems.push(message.params.exceptionDetails.exception?.description
      ?? message.params.exceptionDetails.text);
  } else if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
    problems.push(message.params.args.map(arg => arg.value ?? arg.description ?? '').join(' '));
  }
};

const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
  const id = ++nextId;
  pending.set(id, { resolve, reject });
  socket.send(JSON.stringify({ id, method, params, sessionId }));
});

const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
await send('Runtime.enable', {}, sessionId);
await send('Page.enable', {}, sessionId);
await send('Page.navigate', { url }, sessionId);

// What the page looks like right now. #blazor-error-ui is the bar Blazor shows when the
// circuit has died of an unhandled exception.
const inspect = `JSON.stringify({
  cards: document.querySelectorAll('[data-widget-id]').length,
  waiting: document.querySelectorAll('.widget-skeleton').length,
  failed: [...document.querySelectorAll('.card-failed')].map(e => e.textContent.trim()),
  crashed: (() => { const bar = document.getElementById('blazor-error-ui');
                    return !!bar && getComputedStyle(bar).display !== 'none'; })(),
})`;

let state = { cards: 0, waiting: -1, failed: [], crashed: false };
while (Date.now() < deadline) {
  try {
    const { result } = await send('Runtime.evaluate', { expression: inspect, returnByValue: true }, sessionId);
    if (typeof result.value === 'string') state = JSON.parse(result.value);
  } catch {
    // Mid-navigation there is no document to ask. The next attempt will have one.
  }

  if (state.crashed) finish(1, `Blazor reported an unhandled error.\n${problems.join('\n')}`);
  if (state.failed.length > 0) finish(1, `Failed cards:\n  ${state.failed.join('\n  ')}\n${problems.join('\n')}`);
  if (state.cards > 0 && state.waiting === 0) finish(0, `${state.cards} cards drawn, none failed.`);

  await new Promise(resolve => setTimeout(resolve, 250));
}

finish(2, `After ${seconds}s, ${state.waiting} of ${state.cards} cards were still loading.` +
  (problems.length ? `\nThe page said:\n${problems.join('\n')}` : ''));
