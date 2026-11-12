// Where the home sends the dialog back to (same origin as the pane): hands the code to the pane with messageParent.
Office.onReady(() => {
  const q = new URLSearchParams(location.search);
  let attempt = {};
  try { attempt = JSON.parse(sessionStorage.getItem("hello.attempt") || "{}"); } catch { /* the dialog's storage was unavailable */ }
  const message = q.get("error") ? { error: q.get("error") + " " + (q.get("error_description") || "") }
    : q.get("state") !== attempt.state ? { error: "The answer does not belong to this sign-in." }
    : { code: q.get("code"), verifier: attempt.verifier };
  Office.context.ui.messageParent(JSON.stringify(message), { targetOrigin: location.origin });
});
