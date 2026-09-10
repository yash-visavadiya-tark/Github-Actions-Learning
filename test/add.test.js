const test = require('node:test');
const assert = require('node:assert');
const { add } = require('../index.js');

test('adds two numbers', () => {
  assert.strictEqual(add(2, 3), 5);
});

test('adds negatives', () => {
  assert.strictEqual(add(-1, -1), -2);
});
