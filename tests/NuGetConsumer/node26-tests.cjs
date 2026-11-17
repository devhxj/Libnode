// Exercise the embedded Node 26 runtime through the real NodeApi NuGet consumer.
// All network traffic stays on loopback; resources are closed in finally blocks.
module.exports = (async () => {
  const assert = require('node:assert/strict');
  const tests = [];
  const test = (name, run) => tests.push({ name, run });

  test('runtime identity and native process architecture', () => {
    assert.equal(process.version, 'v26.13.1');
    assert.equal(process.versions.modules, '147');
    assert.equal(process.versions.napi, '10');
    assert.ok(['x64', 'arm64'].includes(process.arch));
  });
  test('Temporal calendar arithmetic', () => {
    const leapDay = Temporal.PlainDate.from('2024-02-28').add({ days: 1 });
    assert.equal(leapDay.toString(), '2024-02-29');
    assert.equal(leapDay.add({ years: 1 }).toString(), '2025-02-28');
  });
  test('Temporal nanosecond precision', () => {
    const instant = Temporal.Instant.from('2026-11-17T00:00:00.123456789Z');
    assert.equal(instant.add({ nanoseconds: 1 }).epochNanoseconds - instant.epochNanoseconds, 1n);
    assert.equal(instant.toString(), '2026-11-17T00:00:00.123456789Z');
  });
  test('Temporal time zone and daylight saving transition', () => {
    const start = Temporal.ZonedDateTime.from('2024-03-10T01:30:00-05:00[America/New_York]');
    const next = start.add({ hours: 1 });
    assert.equal(next.hour, 3);
    assert.equal(next.offset, '-04:00');
    assert.equal(next.epochNanoseconds - start.epochNanoseconds, 3600_000_000_000n);
  });
  test('Temporal duration rounding', () => {
    const duration = Temporal.Duration.from('PT1H31M');
    assert.equal(duration.total({ unit: 'minutes' }), 91);
    assert.equal(duration.round({ smallestUnit: 'hours', roundingMode: 'halfExpand' }).toString(), 'PT2H');
  });
  test('iterator helpers', () => {
    assert.deepEqual([1, 2, 3, 4].values().filter(x => x % 2 === 0).map(x => x * 10).take(2).toArray(), [20, 40]);
  });
  test('Set composition', () => {
    assert.deepEqual([...new Set([1, 2]).union(new Set([2, 3]))], [1, 2, 3]);
    assert.deepEqual([...new Set([1, 2]).intersection(new Set([2, 3]))], [2]);
  });
  test('Float16Array numeric conversion', () => {
    const values = new Float16Array([1.5, -2.25, Infinity]);
    assert.equal(values.BYTES_PER_ELEMENT, 2);
    assert.deepEqual([...values], [1.5, -2.25, Infinity]);
  });
  test('JSON reviver source preserves large integers', () => {
    const value = JSON.parse('{"id":9007199254740993}', (key, value, context) => key === 'id' ? BigInt(context.source) : value);
    assert.equal(value.id, 9007199254740993n);
  });
  test('Unicode regular expression set operations', () => {
    assert.equal(/[\p{ASCII}&&\p{Letter}]+/v.exec('123abcé')[0], 'abc');
  });
  test('structuredClone cycles and ownership transfer', () => {
    const input = { buffer: new Uint8Array([4, 2]).buffer, map: new Map([['x', 42n]]) };
    input.self = input;
    const clone = structuredClone(input, { transfer: [input.buffer] });
    assert.equal(input.buffer.byteLength, 0);
    assert.equal(clone.self, clone);
    assert.equal(clone.map.get('x'), 42n);
    assert.deepEqual([...new Uint8Array(clone.buffer)], [4, 2]);
  });
  test('Web Crypto SHA-256', async () => {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode('abc'));
    assert.equal(Buffer.from(digest).toString('hex'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });
  test('Web Crypto AES-GCM and authentication failure', async () => {
    const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 128 }, false, ['encrypt', 'decrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const encrypted = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode('libnode'));
    const clear = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, encrypted);
    assert.equal(new TextDecoder().decode(clear), 'libnode');
    new Uint8Array(encrypted)[0] ^= 1;
    await assert.rejects(crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, encrypted));
  });
  test('OpenSSL Ed25519 signing', () => {
    const { generateKeyPairSync, sign, verify } = require('node:crypto');
    const { publicKey, privateKey } = generateKeyPairSync('ed25519');
    const signature = sign(null, Buffer.from('node26'), privateKey);
    assert.equal(verify(null, Buffer.from('node26'), publicKey, signature), true);
    assert.equal(verify(null, Buffer.from('changed'), publicKey, signature), false);
  });
  test('Web Streams transform and async iteration', async () => {
    const source = new ReadableStream({ start(controller) { controller.enqueue('node'); controller.enqueue('26'); controller.close(); } });
    const transformed = source.pipeThrough(new TransformStream({ transform(chunk, controller) { controller.enqueue(chunk.toUpperCase()); } }));
    let output = '';
    for await (const chunk of transformed) output += chunk;
    assert.equal(output, 'NODE26');
  });
  test('CompressionStream and DecompressionStream', async () => {
    const input = 'embedded-node-26 '.repeat(512);
    const compressed = await new Response(new Blob([input]).stream().pipeThrough(new CompressionStream('gzip'))).arrayBuffer();
    assert.ok(compressed.byteLength < input.length);
    const output = await new Response(new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip'))).text();
    assert.equal(output, input);
  });
  test('fetch data URL', async () => {
    const response = await fetch('data:application/json,%7B%22answer%22%3A42%7D');
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { answer: 42 });
  });
  test('fetch loopback HTTP and request body', async () => {
    const server = require('node:http').createServer(async (request, response) => {
      let body = '';
      for await (const chunk of request) body += chunk;
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify({ method: request.method, body }));
    });
    try {
      await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
      const response = await fetch(`http://127.0.0.1:${server.address().port}/`, { method: 'POST', body: '42', signal: AbortSignal.timeout(5000) });
      assert.deepEqual(await response.json(), { method: 'POST', body: '42' });
    } finally {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    }
  });
  test('AbortSignal cancellation', async () => {
    const { setTimeout } = require('node:timers/promises');
    const controller = new AbortController();
    const pending = setTimeout(10000, null, { signal: controller.signal });
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
  });
  test('WebAssembly compile and execution', async () => {
    const bytes = Uint8Array.from([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,97,100,100,0,0,10,9,1,7,0,32,0,32,1,106,11]);
    const { instance } = await WebAssembly.instantiate(bytes);
    assert.equal(instance.exports.add(20, 22), 42);
  });
  test('worker ArrayBuffer transfer and clean exit', async () => {
    const { Worker } = require('node:worker_threads');
    const worker = new Worker(`const {parentPort}=require('node:worker_threads'); parentPort.once('message', buffer => { new Uint8Array(buffer)[0] += 1; parentPort.postMessage(buffer, [buffer]); parentPort.close(); });`, { eval: true });
    try {
      const exit = new Promise((resolve, reject) => { worker.once('exit', code => code === 0 ? resolve() : reject(new Error('worker exit ' + code))); worker.once('error', reject); });
      const result = new Promise((resolve, reject) => { worker.once('message', resolve); worker.once('error', reject); });
      const buffer = new Uint8Array([41]).buffer;
      worker.postMessage(buffer, [buffer]);
      assert.equal(buffer.byteLength, 0);
      assert.equal(new Uint8Array(await result)[0], 42);
      await exit;
    } finally { await worker.terminate(); }
  });
  test('SharedArrayBuffer and Atomics across worker', async () => {
    const { Worker } = require('node:worker_threads');
    const values = new Int32Array(new SharedArrayBuffer(4));
    const worker = new Worker(`const {workerData,parentPort}=require('node:worker_threads'); const values=new Int32Array(workerData); Atomics.add(values,0,42); parentPort.postMessage(Atomics.load(values,0));`, { eval: true, workerData: values.buffer });
    try {
      const result = await new Promise((resolve, reject) => { worker.once('message', resolve); worker.once('error', reject); });
      assert.equal(result, 42);
      assert.equal(Atomics.load(values, 0), 42);
    } finally { await worker.terminate(); }
  });
  test('ES modules and top-level await', async () => {
    const module = await import('data:text/javascript,' + encodeURIComponent('export const answer = await Promise.resolve(42); export default import.meta.url;'));
    assert.equal(module.answer, 42);
    assert.ok(module.default.startsWith('data:text/javascript,'));
  });
  test('TypeScript type stripping', () => {
    const output = require('node:module').stripTypeScriptTypes('const answer: number = 42; answer;', { mode: 'strip' });
    assert.equal(require('node:vm').runInNewContext(output), 42);
  });
  test('SQLite transactions and prepared statements', () => {
    const { DatabaseSync } = require('node:sqlite');
    const db = new DatabaseSync(':memory:');
    try {
      db.exec('CREATE TABLE probes(id INTEGER PRIMARY KEY, value TEXT); BEGIN');
      db.prepare('INSERT INTO probes VALUES (?, ?)').run(1, 'node26');
      db.exec('COMMIT');
      assert.equal(db.prepare('SELECT value FROM probes WHERE id = ?').get(1).value, 'node26');
      db.exec('BEGIN; DELETE FROM probes; ROLLBACK');
      assert.equal(db.prepare('SELECT count(*) AS n FROM probes').get().n, 1);
    } finally { db.close(); }
  });
  test('filesystem and CommonJS module loading', async () => {
    const fs = require('node:fs/promises');
    const path = require('node:path');
    const folder = await fs.mkdtemp(path.join(require('node:os').tmpdir(), 'libnode26-'));
    const modulePath = path.join(folder, 'probe.cjs');
    try {
      await fs.writeFile(modulePath, 'module.exports = { answer: 42 };');
      assert.equal(require(modulePath).answer, 42);
      await fs.rename(modulePath, path.join(folder, 'renamed.cjs'));
      assert.match(await fs.readFile(path.join(folder, 'renamed.cjs'), 'utf8'), /answer: 42/);
    } finally { delete require.cache[modulePath]; await fs.rm(folder, { recursive: true, force: true }); }
  });
  test('AsyncLocalStorage concurrent promise isolation', async () => {
    const { AsyncLocalStorage } = require('node:async_hooks');
    const storage = new AsyncLocalStorage();
    try {
      const values = await Promise.all([20, 22].map(value => storage.run(value, async () => { await require('node:timers/promises').setTimeout(1); return storage.getStore(); })));
      assert.deepEqual(values, [20, 22]);
      assert.equal(storage.getStore(), undefined);
    } finally { storage.disable(); }
  });
  test('ICU segmentation and time zone data', () => {
    assert.deepEqual([...new Intl.Segmenter('en', { granularity: 'word' }).segment('hello world')].filter(x => x.isWordLike).map(x => x.segment), ['hello', 'world']);
    const formatted = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Shanghai', hour: '2-digit', hourCycle: 'h23' }).format(new Date('2026-01-01T00:00:00Z'));
    assert.equal(formatted, '08');
  });
  test('V8 serialization of BigInt and typed arrays', () => {
    const v8 = require('node:v8');
    const input = { id: 9007199254740993n, values: new Uint16Array([42, 65535]), map: new Map([['answer', 42]]) };
    assert.deepEqual(v8.deserialize(v8.serialize(input)), input);
  });
  test('streams pipeline with gzip round trip', async () => {
    const { Readable, Writable } = require('node:stream');
    const { pipeline } = require('node:stream/promises');
    const { createGzip, createGunzip } = require('node:zlib');
    const chunks = [];
    await pipeline(Readable.from(['node', '26']), createGzip(), createGunzip(), new Writable({ write(chunk, encoding, done) { chunks.push(chunk); done(); } }));
    assert.equal(Buffer.concat(chunks).toString(), 'node26');
  });

  const results = [];
  for (const { name, run } of tests) {
    const start = performance.now();
    let timer;
    try {
      await Promise.race([Promise.resolve().then(run), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('case timeout')), 10000); })]);
      results.push({ name, passed: true, elapsedMs: Math.round(performance.now() - start) });
    } catch (error) {
      results.push({ name, passed: false, error: error.stack || String(error) });
    } finally { clearTimeout(timer); }
  }
  return JSON.stringify({ node: process.version, arch: process.arch, total: results.length, passed: results.filter(x => x.passed).length, results });
})()
