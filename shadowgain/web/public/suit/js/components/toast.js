export function toast(message, isError = false) {
  const root = document.getElementById('toast-root');
  const el = document.createElement('div');
  el.className = 'toast' + (isError ? ' toast--err' : '');
  el.textContent = message;
  root.appendChild(el);
  setTimeout(() => el.remove(), isError ? 6000 : 2500);
}
