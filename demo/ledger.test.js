import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Ledger, race } from './ledger.js';

const seeds = [1, 2, 3, 42, 2026];
const params = { balance: 5000, amount: 100, requests: 200 };

test('atomic updates send exactly what the account holds', () => {
  for (const seed of seeds) {
    const events = race({ ...params, mode: 'atomic', seed });
    assert.equal(events.filter((e) => e.ok).length, 50);
    assert.equal(events.at(-1).balance, 0);
  }
});

test('read-then-write sends more than the account holds', () => {
  for (const seed of seeds) {
    assert.ok(race({ ...params, mode: 'naive', seed }).at(-1).sent > 5000, `seed ${seed}`);
  }
});

function pair(balance) {
  const l = new Ledger();
  const a = l.open('A');
  const b = l.open('B');
  if (balance) l.move('seed', null, a.id, balance);
  return [l, a, b];
}

test('a transfer moves money and writes both sides', () => {
  const [l, a, b] = pair(1000);
  assert.equal(l.move('t', a.id, b.id, 300).status, 201);
  assert.deepEqual([a.balance, b.balance], [700, 300]);
  assert.equal(l.statement(a.id).reduce((s, e) => s + e.amount, 0), 700);
});

test('insufficient funds change nothing and do not use up the key', () => {
  const [l, a, b] = pair(100);
  assert.equal(l.move('t', a.id, b.id, 200).status, 409);
  assert.equal(a.balance, 100);
  l.move('top', null, a.id, 100);
  assert.equal(l.move('t', a.id, b.id, 200).status, 201);
});

test('a retry with the same key returns the first result', () => {
  const [l, a, b] = pair(1000);
  const first = l.move('t', a.id, b.id, 300);
  const second = l.move('t', a.id, b.id, 300);
  assert.equal(second.status, 200);
  assert.equal(second.replayed, true);
  assert.equal(second.transfer.id, first.transfer.id);
  assert.equal(a.balance, 700);
});

test('the same key for a different request is refused', () => {
  const [l, a, b] = pair(1000);
  l.move('t', a.id, b.id, 300);
  assert.equal(l.move('t', a.id, b.id, 400).status, 422);
});

test('bad requests are rejected', () => {
  const [l, a, b] = pair(100);
  assert.equal(l.move('', a.id, b.id, 1).status, 400);
  assert.equal(l.move('t', a.id, b.id, 0).status, 400);
  assert.equal(l.move('t', a.id, a.id, 1).status, 400);
  assert.equal(l.move('t', a.id, 99, 1).status, 404);
});
