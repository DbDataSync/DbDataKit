namespace DbDataKit.Identity;

/// <summary>
/// The identity home's own pages: plain HTML with a little script, no web app needed, so the home works wherever it runs (the serving layer has no console web app beside it).
/// The cookie of the home's session is SameSite Strict, so both pages are only ever reached on the home's own site.
/// </summary>
public static class IdentityHomePages
{
    private const string Style = """
        <style>body{font:16px system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f5f5f4;color:#1c1917}
        main{background:#fff;padding:2rem;border-radius:.5rem;box-shadow:0 1px 4px #0003;max-width:22rem;width:calc(100% - 2rem)}
        label{display:block;margin:.6rem 0 .2rem}input{font:inherit;width:100%;box-sizing:border-box;padding:.4rem}
        button{font:inherit;padding:.6rem 1rem;width:100%;cursor:pointer;margin-top:1rem}p.message{color:#b91c1c;min-height:1.2em}
        @media (prefers-color-scheme:dark){body{background:#1c1917;color:#f5f5f4}main{background:#292524}input{background:#1c1917;color:inherit;border:1px solid #57534e}}</style>
        """;

    private const string Base64 = """
        const toBuffer = b64 => { const p = b64.replace(/-/g, '+').replace(/_/g, '/'); const bin = atob(p.padEnd(Math.ceil(p.length / 4) * 4, '=')); return Uint8Array.from(bin, c => c.charCodeAt(0)).buffer; };
        const toB64 = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        const say = (id, text) => { document.getElementById(id).textContent = text; };
        async function errorOf(response, fallback) { try { return (await response.json()).error || fallback; } catch { return fallback; } }
        """;

    /// <summary>
    /// A person who is not signed in arrives here from <c>/identity/authorize</c> with <c>returnUrl</c>. With a session already they go straight back; otherwise they sign in with a passkey. Only an address
    /// under <c>/identity/</c> of the home is returned to; with none, the page says who is signed in.
    /// </summary>
    public const string SignIn = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in</title>
        """ + Style + """
        </head>
        <body><main data-testid="identity-sign-in"><h1>Sign in</h1><p id="intro">Use your passkey to sign in to the identity home.</p>
        <button id="passkey" data-testid="identity-sign-in-passkey" type="button">Sign in with a passkey</button>
        <p id="done" data-testid="identity-signed-in" hidden></p><p id="message" class="message" role="alert"></p></main>
        <script>
        """ + Base64 + """
        const requested = new URLSearchParams(location.search).get('returnUrl') || '';
        const target = requested.startsWith('/identity/') && !requested.startsWith('//') && !requested.includes('\\') ? requested : null;
        async function whoIsSignedIn() { const r = await fetch('/api/auth/status'); if (!r.ok) return null; const s = await r.json(); return s.userId ? s : null; }
        function signedIn(status) {
          if (target) { location.replace(target); return; }
          document.getElementById('passkey').hidden = true; document.getElementById('intro').hidden = true;
          const done = document.getElementById('done'); done.hidden = false; done.textContent = `Signed in as ${status.displayName}.`;
        }
        async function go() {
          const begin = await fetch('/api/auth/passkey/begin', { method: 'POST' });
          if (!begin.ok) { say('message', await errorOf(begin, 'Passkeys are not available here.')); return; }
          const o = await begin.json();
          const c = await navigator.credentials.get({ publicKey: { ...o, challenge: toBuffer(o.challenge), allowCredentials: (o.allowCredentials ?? []).map(a => ({ ...a, id: toBuffer(a.id) })) } });
          const r = c.response;
          const done = await fetch('/api/auth/passkey/complete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
            id: c.id, rawId: toB64(c.rawId), type: c.type, extensions: c.getClientExtensionResults(),
            response: { authenticatorData: toB64(r.authenticatorData), clientDataJson: toB64(r.clientDataJSON), signature: toB64(r.signature), userHandle: r.userHandle ? toB64(r.userHandle) : null } }) });
          if (!done.ok) { say('message', await errorOf(done, 'That did not sign you in.')); return; }
          signedIn(await whoIsSignedIn() ?? { displayName: 'you' });
        }
        document.getElementById('passkey').addEventListener('click', () => go().catch(e => say('message', e.message || String(e))));
        whoIsSignedIn().then(s => { if (s) signedIn(s); }).catch(() => { });
        </script></body></html>
        """;

    /// <summary>
    /// An invitation (<c>/invite#&lt;code&gt;</c>, the first-run one included): the person gives their name (and their address, when the invitation was made without one), makes a passkey, and is signed in at the home.
    /// </summary>
    public const string Invite = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Accept your invitation</title>
        """ + Style + """
        </head>
        <body><main data-testid="identity-invite"><h1>Accept your invitation</h1>
        <form id="form" hidden><p>Make a passkey on this device to sign in to the identity home.</p>
        <label for="name">Your name</label><input id="name" data-testid="identity-invite-name" autocomplete="name" required>
        <div id="email-field" hidden><label for="email">Your email address</label><input id="email" data-testid="identity-invite-email" type="email" autocomplete="email"></div>
        <button id="enrol" data-testid="identity-invite-enrol" type="submit">Make a passkey</button></form>
        <p id="done" data-testid="identity-invite-done" hidden></p><p id="message" class="message" data-testid="identity-invite-message" role="alert"></p></main>
        <script>
        """ + Base64 + """
        const code = decodeURIComponent(location.hash.slice(1));
        let emailAsked = false;
        async function check() {
          if (!code) { say('message', 'This address has no invitation in it. Open the link you were given.'); return; }
          const r = await fetch('/api/invites/check?code=' + encodeURIComponent(code));
          if (!r.ok) { say('message', await errorOf(r, 'This invitation is not valid.')); return; }
          emailAsked = (await r.json()).emailAsked;
          document.getElementById('email-field').hidden = !emailAsked;
          document.getElementById('form').hidden = false;
        }
        async function enrol() {
          const name = document.getElementById('name').value.trim();
          const email = emailAsked ? document.getElementById('email').value.trim() : '';
          const begin = await fetch('/api/invites/begin-registration', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code, displayName: name, email: email || null }) });
          if (!begin.ok) { say('message', await errorOf(begin, 'The passkey could not be started.')); return; }
          const o = await begin.json();
          const c = await navigator.credentials.create({ publicKey: { ...o, challenge: toBuffer(o.challenge), user: { ...o.user, id: toBuffer(o.user.id) }, excludeCredentials: (o.excludeCredentials ?? []).map(x => ({ ...x, id: toBuffer(x.id) })) } });
          const query = `code=${encodeURIComponent(code)}&displayName=${encodeURIComponent(name)}&email=${encodeURIComponent(email)}`;
          const done = await fetch('/api/invites/complete-registration?' + query, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
            id: c.id, rawId: toB64(c.rawId), type: c.type, extensions: c.getClientExtensionResults(),
            response: { attestationObject: toB64(c.response.attestationObject), clientDataJson: toB64(c.response.clientDataJSON) } }) });
          if (!done.ok) { say('message', await errorOf(done, 'The passkey could not be saved.')); return; }
          const status = await done.json();
          document.getElementById('form').hidden = true;
          const shown = document.getElementById('done'); shown.hidden = false; shown.textContent = `Signed in as ${status.displayName}.`;
        }
        document.getElementById('form').addEventListener('submit', e => { e.preventDefault(); say('message', ''); enrol().catch(x => say('message', x.message || String(x))); });
        check().catch(e => say('message', e.message || String(e)));
        </script></body></html>
        """;
}
