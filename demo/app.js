import { Ledger, race } from './ledger.js';

const BALANCE = 5000;
const AMOUNT = 100;
const REQUESTS = 200;
const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
const $ = (s) => document.querySelector(s);
const rub = (n) => n.toLocaleString('ru-RU') + ' ₽';

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

let run = 0;

function setScore(lane, sent, done) {
  const score = lane.querySelector('.score');
  score.textContent = `Отправлено ${rub(sent)} из ${rub(BALANCE)}` + (done ? (sent > BALANCE ? ` — из воздуха: ${rub(sent - BALANCE)}` : ' — ровно столько, сколько было') : '');
  score.className = `score ${done ? (sent > BALANCE ? 'bad' : 'good') : ''}`;
}

function resetLane(lane) {
  lane.querySelector('.dots').replaceChildren(...Array.from({ length: REQUESTS }, () => el('i')));
  setScore(lane, 0, false);
}

function play(lane, events, mine) {
  const dots = lane.querySelectorAll('.dots i');
  let at = 0;
  const batch = reduced ? REQUESTS : 4;
  const step = () => {
    if (mine !== run) return;
    for (const e of events.slice(at, at + batch)) {
      dots[e.id].className = e.ok ? 'ok' : 'no';
      setScore(lane, e.sent, false);
    }
    at += batch;
    if (at < events.length) setTimeout(step, 28);
    else setScore(lane, events.at(-1).sent, true);
  };
  step();
}

function start() {
  const mine = ++run;
  const seed = Math.floor(Math.random() * 1e9);
  for (const mode of ['naive', 'atomic']) {
    const lane = document.getElementById(mode);
    resetLane(lane);
    play(lane, race({ balance: BALANCE, amount: AMOUNT, requests: REQUESTS, mode, seed }), mine);
  }
}

// The API playground.
const ledger = new Ledger();
const anna = ledger.open('Анна');
const boris = ledger.open('Борис');
ledger.move('deposit-1', null, anna.id, 1000);
let keyCounter = 0;
let last = null;

function send(request, label) {
  const result = ledger.move(request.key, anna.id, boris.id, request.amount);
  const li = el('li', result.status >= 400 ? 'bad' : 'good');
  li.append(el('code', null, `POST /api/transfers · ключ ${request.key}${label}`), el('b', null, ` ${result.status}`));
  if (result.detail) li.append(el('span', null, ` ${result.detail}`));
  if (result.replayed) li.append(el('span', null, ' повтор: деньги не списаны'));
  $('#log').prepend(li);
  render();
}

function render() {
  $('#accounts').replaceChildren(...[anna, boris].map((a) => {
    const box = el('div', 'account');
    box.append(el('small', null, a.owner), el('b', null, rub(a.balance)));
    return box;
  }));
  $('#statement').replaceChildren(...[...ledger.statement(anna.id)].reverse().map((e) =>
    el('li', `item ${e.amount < 0 ? 'out' : 'in'}`, `${e.amount > 0 ? '+' : '−'}${rub(Math.abs(e.amount))} · перевод №${e.transfer}`)));
}

$('#run').addEventListener('click', start);
$('#form').addEventListener('submit', (e) => {
  e.preventDefault();
  last = { key: `t-${++keyCounter}`, amount: Number($('#amount').value) };
  $('#retry').disabled = false;
  send(last, '');
});
$('#retry').addEventListener('click', () => send(last, ' (повтор)'));

resetLane($('#naive'));
resetLane($('#atomic'));
render();
start();
