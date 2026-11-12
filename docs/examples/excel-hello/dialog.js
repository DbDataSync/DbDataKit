// The first page of the dialog (same origin as the pane, as Office requires): builds the authorization request (code flow with PKCE S256)
// and sends the browser to the identity home's own sign-in. The home offers its passkeys, Entra and Windows as configured.
function b64url(bytes) { return btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, ""); }

(async () => {
  const { home, clientId, scope } = window.HELLO;
  const verifier = b64url(crypto.getRandomValues(new Uint8Array(32)));
  const challenge = b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier)));
  const state = b64url(crypto.getRandomValues(new Uint8Array(16)));
  sessionStorage.setItem("hello.attempt", JSON.stringify({ verifier, state }));
  const q = new URLSearchParams({
    response_type: "code", client_id: clientId, redirect_uri: location.origin + "/auth-done.html",
    scope, state, code_challenge: challenge, code_challenge_method: "S256",
  });
  location.replace(home + "/identity/authorize?" + q);
})();
