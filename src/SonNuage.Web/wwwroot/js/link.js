// Canal entre les onglets du lecteur (même navigateur, même origine). Les messages sont des chaînes JSON
// construites et lues côté C# (LinkedPlayer) ; ce module ne fait que les transporter.

const CHANNEL = 'sonnuage-player';

let channel = null;

export function start(dotnetRef, deviceId) {
  if (!('BroadcastChannel' in window)) return false;
  channel = new BroadcastChannel(CHANNEL);
  channel.onmessage = (e) => {
    if (typeof e.data === 'string') dotnetRef.invokeMethodAsync('OnLinkMessage', e.data).catch(() => {});
  };
  // Fermeture ou rechargement : les autres onglets reprennent la main.
  window.addEventListener('pagehide', () => channel?.postMessage(JSON.stringify({ type: 'bye', from: deviceId })));
  return true;
}

export function send(json) {
  channel?.postMessage(json);
}
