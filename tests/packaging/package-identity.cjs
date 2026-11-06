// Exercise the production MSBuild task without browser, workload or NuGet dependencies.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const zlib = require('node:zlib');
const { spawnSync } = require('node:child_process');

const repository = path.resolve(__dirname, '../..');
const target = path.join(repository, 'build/VersionWasmPackage.targets');
const dotnet = process.env.DOTNET_HOST_PATH || 'dotnet';
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'pixelmap-packaging-'));
const sdkPackage = `package_${'a'.repeat(40)}`;
const unrelatedPackage = `package_${'b'.repeat(40)}`;
const adapters = ['audio/journey-audio.js', 'input/map-input.js'];

function write(root, name, value) {
  const file = path.join(root, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, value);
}

function read(root, name) {
  return fs.readFileSync(path.join(root, name), 'utf8');
}

function files(root) {
  return fs.readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(root, entry.name);
    return entry.isDirectory() ? files(file) : [file];
  }).sort();
}

function hash(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function snapshot(root) {
  return Object.fromEntries(files(root).map(file => [path.relative(root, file), hash(fs.readFileSync(file))]));
}

function run(root, success = true) {
  const result = spawnSync(dotnet, ['msbuild', target, '-nologo', '-v:minimal',
    '-t:VersionPixelMapWasmPackage', `-p:PixelMapPublishWebRoot=${root}`], {
    cwd: repository, encoding: 'utf8', timeout: 120_000
  });
  if (result.error) throw result.error;
  const output = result.stdout + result.stderr;
  if (!success) {
    assert.notEqual(result.status, 0, 'Invalid output must fail publication');
    assert.match(output, /PixelMap publish versioning failed:/);
    return output;
  }
  assert.equal(result.status, 0, output);
  const match = output.match(/PixelMap published package: (package_[0-9a-f]{64})/);
  assert.ok(match, output);
  return match[1];
}

function fixture(root) {
  write(root, 'index.html', `<html><head>
    <script src="audio/journey-audio.js"></script>
    <script src='./input/map-input.js?mode=pointer&amp;debug=false#adapter'></script>
    <script src="./${sdkPackage}/require.js"></script>
    <script type="module" src="./${sdkPackage}/uno-bootstrap.js"></script>
    <link rel="stylesheet" href="./${sdkPackage}/uno.css" />
    </head><body></body></html>\n`);
  write(root, 'service-worker.js', `import { config } from './${sdkPackage}/uno-config.js';\n`);
  write(root, `${sdkPackage}/uno-config.js`, `const config = {};
    config.uno_app_base = "./${sdkPackage}";
    config.dotnet_js_filename = "dotnet.fixture.js";
    config.environmentVariables = {"UNO_BOOTSTRAP_APP_BASE": "${sdkPackage}"};
    export { config };\n`);
  write(root, `${sdkPackage}/uno-bootstrap.js`, "export const config = import('./uno-config.js');\n");
  write(root, `${sdkPackage}/require.js`, '/* unchanged dependency */\n');
  write(root, `${sdkPackage}/uno.css`, `body { background: url("/${sdkPackage}/icon.svg"); }\n`);
  write(root, `${sdkPackage}/icon.svg`, '<svg xmlns="http://www.w3.org/2000/svg"/>\n');
  write(root, `${sdkPackage}/manifest.json`, JSON.stringify({ path: `./${sdkPackage}/icon.svg` }));
  write(root, `${sdkPackage}/module.js`, `const image = './${sdkPackage}/icon.svg';\n`);
  for (const adapter of adapters) write(root, adapter, `// ${adapter}\n`);
  write(root, '_framework/dotnet.fixture.js', 'export const runtime = "fixture";\n');
  for (let index = 0; index < 71; index++)
    write(root, `_framework/resource-${index}.wasm`, Buffer.from([0, 97, 115, 109, index]));
  write(root, '_framework/resource-0.wasm.gz', zlib.gzipSync(fs.readFileSync(path.join(root, '_framework/resource-0.wasm'))));
  for (const file of ['index.html', 'service-worker.js', `${sdkPackage}/uno-config.js`, `${sdkPackage}/module.js`, `${sdkPackage}/uno.css`, `${sdkPackage}/manifest.json`]) {
    write(root, `${file}.gz`, zlib.gzipSync(fs.readFileSync(path.join(root, file))));
    write(root, `${file}.br`, zlib.brotliCompressSync(fs.readFileSync(path.join(root, file))));
  }
  write(root, `${sdkPackage}/require.js.gz`, zlib.gzipSync(fs.readFileSync(path.join(root, sdkPackage, 'require.js'))));
  write(root, `${sdkPackage}/require.js.br`, zlib.brotliCompressSync(fs.readFileSync(path.join(root, sdkPackage, 'require.js'))));
  write(root, `${unrelatedPackage}/unrelated.bin`, 'stale unrelated package');
  write(root, 'unrelated.bin', 'unrelated root file');
}

function verifyReferences(root, name) {
  const digest = name.substring('package_'.length);
  const index = read(root, 'index.html');
  assert.ok(index.includes(`./${name}/uno-bootstrap.js`));
  assert.ok(!index.includes(sdkPackage));
  for (const adapter of adapters) {
    const expression = new RegExp(`<script[^>]+src=["']([^"']*${adapter.replaceAll('.', '\\.')}[^"']*)["']`);
    const url = new URL(index.match(expression)[1].replaceAll('&amp;', '&'), 'https://example.test/UnoPixelMap/');
    assert.equal(url.searchParams.get('pixelmap-build'), digest);
    assert.equal(url.searchParams.getAll('pixelmap-build').length, 1);
    if (adapter.startsWith('input/')) {
      assert.equal(url.searchParams.get('mode'), 'pointer');
      assert.equal(url.searchParams.get('debug'), 'false');
      assert.equal(url.hash, '#adapter');
    }
  }
  for (const file of ['service-worker.js', `${name}/uno-config.js`, `${name}/uno.css`, `${name}/manifest.json`, `${name}/module.js`]) {
    assert.ok(read(root, file).includes(name), file);
    assert.ok(!read(root, file).includes(sdkPackage), file);
    assert.ok(!fs.existsSync(path.join(root, file + '.gz')), file);
    assert.ok(!fs.existsSync(path.join(root, file + '.br')), file);
  }
  assert.ok(!fs.existsSync(path.join(root, 'index.html.gz')));
  assert.ok(!fs.existsSync(path.join(root, 'index.html.br')));
  const dependency = fs.readFileSync(path.join(root, name, 'require.js'));
  assert.deepEqual(zlib.gunzipSync(fs.readFileSync(path.join(root, name, 'require.js.gz'))), dependency);
  assert.deepEqual(zlib.brotliDecompressSync(fs.readFileSync(path.join(root, name, 'require.js.br'))), dependency);
  assert.equal(read(root, `${unrelatedPackage}/unrelated.bin`), 'stale unrelated package');
  assert.equal(read(root, 'unrelated.bin'), 'unrelated root file');
}

function verifyPublished(root) {
  const initialIndex = read(root, 'index.html');
  const selected = initialIndex.match(/(?:^|\/)package_([0-9a-f]{64})\/uno-bootstrap\.js/);
  assert.ok(selected, 'dotnet publish must finalize the bootstrap package before artifact verification');
  assert.equal((initialIndex.match(new RegExp(`pixelmap-build=${selected[1]}`, 'g')) || []).length, 2,
    'dotnet publish must version both browser adapters before artifact verification');
  const before = snapshot(root);
  const frameworkBefore = snapshot(path.join(root, '_framework'));
  const first = run(root);
  assert.equal(first, `package_${selected[1]}`);
  assert.deepEqual(snapshot(root), before, 'The publish hook must have finalized every artifact byte before verification');
  assert.equal(run(root), first, 'Repeated finalization must have an identical identity');
  assert.deepEqual(snapshot(root), before, 'Repeated finalization must leave all bytes unchanged');
  assert.deepEqual(snapshot(path.join(root, '_framework')), frameworkBefore, 'Framework resources and compressed variants must remain byte-identical');
  let compressedCount = 0;
  for (const file of files(root)) {
    const decompress = file.endsWith('.gz') ? zlib.gunzipSync : file.endsWith('.br') ? zlib.brotliDecompressSync : null;
    if (!decompress) continue;
    assert.deepEqual(decompress(fs.readFileSync(file)), fs.readFileSync(file.slice(0, -3)), file);
    compressedCount++;
  }
  const config = read(root, `${first}/uno-config.js`);
  const loader = config.match(/config\.dotnet_js_filename\s*=\s*"([\w.-]+)"/)[1];
  const loaderText = read(root, `_framework/${loader}`);
  const boot = JSON.parse(loaderText.split('/*json-start*/')[1].split('/*json-end*/')[0]);
  let resourceCount = 0;
  for (const entries of Object.values(boot.resources)) {
    if (!Array.isArray(entries)) continue;
    for (const entry of entries) {
      const resource = path.resolve(root, '_framework', entry.name);
      assert.ok(resource.startsWith(path.resolve(root, '_framework') + path.sep));
      const actual = crypto.createHash('sha256').update(fs.readFileSync(resource)).digest('base64');
      if (entry.hash) assert.equal(`sha256-${actual}`, entry.hash, entry.name);
      resourceCount++;
    }
  }
  assert.ok(resourceCount > 0);
  const index = read(root, 'index.html');
  assert.ok(index.includes(`${first}/uno-bootstrap.js`));
  assert.equal((index.match(new RegExp(`pixelmap-build=${first.substring(8)}`, 'g')) || []).length, 2);
  console.log(`Published artifact: ${first}; ${resourceCount} boot resources and ${compressedCount} compressed representations verified; two passes preserve all artifact bytes.`);
}

try {
  const firstRoot = path.join(temporary, 'first output with spaces');
  fixture(firstRoot);
  const framework = snapshot(path.join(firstRoot, '_framework'));
  const first = run(firstRoot);
  verifyReferences(firstRoot, first);
  assert.deepEqual(snapshot(path.join(firstRoot, '_framework')), framework);
  const initial = snapshot(firstRoot);
  assert.equal(run(firstRoot), first);
  assert.deepEqual(snapshot(firstRoot), initial);

  const secondRoot = path.join(temporary, 'second');
  fixture(secondRoot);
  assert.equal(run(secondRoot), first, 'Identical contents at a different path must have the same identity');

  // Simulate a second SDK publish into an existing -o directory: the SDK's
  // original package reappears beside our already-finalized package.
  fixture(firstRoot);
  write(firstRoot, `${first}/require.js.gz`, zlib.gzipSync(Buffer.from('stale dependency from another release')));
  write(firstRoot, `${first}/require.js.br`, zlib.brotliCompressSync(Buffer.from('stale dependency from another release')));
  assert.equal(run(firstRoot), first, 'Incremental publish must reuse the matching finalized package');
  verifyReferences(firstRoot, first);
  assert.ok(!fs.existsSync(path.join(firstRoot, sdkPackage)), 'Retire only the former active package after successful commit');

  fs.appendFileSync(path.join(firstRoot, '_framework/resource-42.wasm'), Buffer.from([42]));
  const changedRuntime = run(firstRoot);
  assert.notEqual(changedRuntime, first, 'Runtime bytes must invalidate the whole bootstrap chain');
  assert.equal(run(firstRoot), changedRuntime);
  verifyReferences(firstRoot, changedRuntime);

  fs.appendFileSync(path.join(firstRoot, adapters[1]), '// new pointer adapter\n');
  const changedAdapter = run(firstRoot);
  assert.notEqual(changedAdapter, changedRuntime, 'Root adapter bytes must invalidate their URLs');
  verifyReferences(firstRoot, changedAdapter);

  fs.appendFileSync(path.join(firstRoot, changedAdapter, 'require.js'), '// new package asset\n');
  const changedAsset = run(firstRoot);
  assert.notEqual(changedAsset, changedAdapter, 'Packaged asset bytes must contribute to identity');

  const invalidRoot = path.join(temporary, 'invalid');
  fixture(invalidRoot);
  fs.unlinkSync(path.join(invalidRoot, '_framework/dotnet.fixture.js'));
  const invalidBefore = snapshot(invalidRoot);
  assert.match(run(invalidRoot, false), /required published file is missing/);
  assert.deepEqual(snapshot(invalidRoot), invalidBefore, 'Validate before changing invalid output');

  const conflictRoot = path.join(temporary, 'conflict');
  fixture(conflictRoot);
  const conflictPackage = run(conflictRoot);
  fixture(conflictRoot);
  fs.appendFileSync(path.join(conflictRoot, conflictPackage, 'require.js'), '// corrupt prior package\n');
  const conflictBefore = snapshot(conflictRoot);
  assert.match(run(conflictRoot, false), /existing finalized package has unexpected content/);
  assert.deepEqual(snapshot(conflictRoot), conflictBefore, 'Never overwrite a conflicting existing package');

  const readOnlyRoot = path.join(temporary, 'read-only');
  fixture(readOnlyRoot);
  fs.chmodSync(path.join(readOnlyRoot, 'index.html'), 0o444);
  const readOnlyBefore = snapshot(readOnlyRoot);
  assert.match(run(readOnlyRoot, false), /entry point is read-only/);
  assert.deepEqual(snapshot(readOnlyRoot), readOnlyBefore, 'Read-only entrypoints must fail before retiring their source package');
  fs.chmodSync(path.join(readOnlyRoot, 'index.html'), 0o644);
  assert.equal(run(readOnlyRoot), first, 'Fixing entrypoint permissions must make the identical publish retry succeed');
  verifyReferences(readOnlyRoot, first);

  console.log('Package identity checks passed: deterministic, repeatable, incremental, runtime/adapters/assets, references, gzip/Brotli reuse, preflight failures and read-only retry.');
  if (process.argv[2]) verifyPublished(path.resolve(process.argv[2]));
} finally {
  fs.rmSync(temporary, { recursive: true, force: true });
}
