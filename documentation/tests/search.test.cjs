const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const engine = require('../../help/assets/search.js');
const script = fs.readFileSync(path.join(__dirname, '../../site/assets/search-index.js'), 'utf8');
const documents = JSON.parse(script.replace(/^window\.DOCS_INDEX=/, '').trim().replace(/;$/, ''));
const index = engine.prepare(documents);

test('Steam Relay ranks its own guide ahead of incidental mentions', () => {
    assert.match(engine.search(index, 'Steam Relay')[0].url, /^steam-relay\//);
});
test('full-text search finds content beyond titles', () => {
    assert.ok(engine.search(index, 'CGNAT').some(hit => hit.url.startsWith('forwarding_troubleshoot/')));
    assert.ok(engine.search(index, 'UPnP').some(hit => hit.url.startsWith('forwarding/')));
});
test('all query terms must match and unknown queries give no hits', () => {
    assert.equal(engine.search(index, 'Steam zzzzunknown').length, 0);
    assert.equal(engine.search(index, 'zzzzunknown').length, 0);
});
test('empty, short, and punctuation-only queries are handled', () => {
    for (const query of ['', ' ', 's', '***', '[]()+']) assert.equal(engine.search(index, query).length, 0);
    assert.ok(engine.search(index, 'IP').length > 0);
});
test('case and punctuation do not prevent error-message matches', () => {
    assert.match(engine.search(index, 'THE PASSWORD WAS NOT ACCEPTED!')[0].url, /#the-password-was-not-accepted$/);
});
test('results are bounded and snippet includes matching context', () => {
    assert.equal(engine.search(index, 'the', 5).length, 5);
    const text = 'An introduction. '.repeat(40) + 'TCP port 4242 must be reachable. ' + 'More text. '.repeat(40);
    const snippet = engine.snippet(text, '4242');
    assert.ok(snippet.includes('4242'));
    assert.ok(snippet.startsWith('…'));
    assert.ok(snippet.length <= 182);
});
test('normalization handles accented text and repeated terms', () => {
    const prepared = engine.prepare([{title: 'Café', heading: 'Résumé', text: 'Résumé of a city.', url: 'city/'}]);
    assert.equal(engine.search(prepared, 'resume')[0].url, 'city/');
    assert.deepEqual(engine.words('Port port PORT'), ['port']);
});
