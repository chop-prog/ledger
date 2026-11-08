// The C# service's rules, ported to JavaScript so they can run on a static page.

export function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Plays `requests` transfers of `amount` against one balance in a random order.
 * 'naive' reads the balance and writes it back later, as two steps; 'atomic' is one step,
 * like `where Balance >= amount`. Returns one event per finished request.
 */
export function race({ balance, amount, requests, mode, seed }) {
  const random = rng(seed);
  let current = balance;
  const running = Array.from({ length: requests }, (_, id) => ({ id, seen: null }));
  const events = [];
  let sent = 0;
  while (running.length) {
    const at = Math.floor(random() * running.length);
    const r = running[at];
    if (mode === 'naive' && r.seen === null) {
      r.seen = current;
      continue;
    }
    const before = mode === 'naive' ? r.seen : current;
    const ok = before >= amount;
    if (ok) {
      current = before - amount;
      sent += amount;
    }
    events.push({ id: r.id, ok, sent, balance: current });
    running.splice(at, 1);
  }
  return events;
}

export class Ledger {
  constructor() {
    this.accounts = new Map();
    this.transfers = new Map();
    this.entries = [];
    this.nextAccount = 1;
    this.nextTransfer = 1;
  }

  open(owner) {
    const account = { id: this.nextAccount++, owner, balance: 0 };
    this.accounts.set(account.id, account);
    return account;
  }

  move(key, from, to, amount) {
    if (!key) return { status: 400, detail: 'Нужен Idempotency-Key' };
    if (!Number.isInteger(amount) || amount <= 0) return { status: 400, detail: 'Сумма должна быть больше нуля' };
    if (from === to) return { status: 400, detail: 'Нельзя перевести на тот же счёт' };
    const earlier = this.transfers.get(key);
    if (earlier) {
      if (earlier.from !== from || earlier.to !== to || earlier.amount !== amount) return { status: 422, detail: 'Этот ключ уже использован для другого запроса' };
      return { status: 200, transfer: earlier, replayed: true };
    }
    const target = this.accounts.get(to);
    const source = from === null ? null : this.accounts.get(from);
    if (!target || source === undefined) return { status: 404, detail: 'Нет такого счёта' };
    if (source && source.balance < amount) return { status: 409, detail: 'Недостаточно средств' };
    if (source) source.balance -= amount;
    target.balance += amount;
    const transfer = { id: this.nextTransfer++, key, from, to, amount };
    this.transfers.set(key, transfer);
    if (source) this.entries.push({ account: from, transfer: transfer.id, amount: -amount });
    this.entries.push({ account: to, transfer: transfer.id, amount });
    return { status: 201, transfer };
  }

  statement(id) {
    return this.entries.filter((e) => e.account === id);
  }
}
